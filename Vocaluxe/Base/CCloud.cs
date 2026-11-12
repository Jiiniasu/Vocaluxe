using Polly;
using Polly.Retry;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Profile;
using VocaluxeLib.Songs;

namespace Vocaluxe.Base
{
    static class CCloud
    {
        static CCloud()
        {
            // Note: the old .NET Framework transport (HttpWebRequest/ServicePointManager) auto-appended
            // "Expect: 100-continue" to POST/PUT, which the Apache front of the cloud server answered with
            // a 400. The net10 HttpClient (SocketsHttpHandler) never sends that header, so no opt-out is
            // needed here any more.

            // Never let an unobserved task fault (e.g. a screen ignoring a fire-and-forget cloud
            // send) bring the whole process down - log it instead.
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private static readonly HttpClient _Client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Connection-level (HttpRequestException) failures are retried with capped backoff.
        // 4xx / 5xx responses are deliberately NOT retried: 4xx are permanent client errors, and for
        // non-idempotent PUTs (e.g. putRound) a 5xx can mean the round was already recorded -
        // re-sending could duplicate it.
        private static readonly AsyncRetryPolicy _HTTPRetryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(4, retryAttempt =>
            {
                CLog.CCloudLog.Information("HTTP retry attempt {attempt}...", CLog.Params(retryAttempt));
                return TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, retryAttempt)));
            });
        // Non-idempotent calls (e.g. putRound): same connection-level handling, but with fewer
        // attempts to further minimize the window in which a duplicate could occur.
        private static readonly AsyncRetryPolicy _HTTPRetryPolicyNonIdempotent = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt =>
            {
                CLog.CCloudLog.Information("HTTP retry attempt {attempt}...", CLog.Params(retryAttempt));
                return TimeSpan.FromSeconds(Math.Min(6, Math.Pow(2, retryAttempt)));
            });
        // Broad handling: any connect failure (WebSocketException, auth, socket, timeout, bad URI)
        // is retried with capped backoff, after which Init() gives up for the round and retries
        // gently. This keeps a down server from ever crashing the game.
        private static readonly AsyncRetryPolicy _WSRetryPolicy = Policy.Handle<Exception>().WaitAndRetryAsync(4, retryAttempt =>
        {
            CLog.CCloudLog.Information("WS retry attempt {attempt}...", CLog.Params(retryAttempt));
            return TimeSpan.FromSeconds(Math.Min(4, Math.Pow(2, retryAttempt)));
        });
        private static readonly object _WebSocketLock = new object();
        private static ClientWebSocket _WebSocket;
        // One-shot cancellation for the whole cloud subsystem: Shutdown() cancels it to abort any
        // in-flight ConnectAsync/ReceiveAsync and to stop the reconnect loop.
        private static readonly CancellationTokenSource _ShutdownCts = new CancellationTokenSource();
        private static int _ShutdownRequested;
        // RequestReconnect() swaps in a fresh CTS and cancels the old one: any back-off delay the
        // connection loop is currently serving ends early and the next attempt starts right away.
        private static readonly object _ReconnectLock = new object();
        private static CancellationTokenSource _ReconnectCts = new CancellationTokenSource();
        private static volatile bool _Connected;
        // Single-flight upload flush: at most one worker thread runs flush passes; concurrent
        // triggers (start-up, reconnect, failed putCover/putRound, shutdown) only raise
        // _FlushPassRequested, which the worker consumes on its next lap. This replaces the old
        // _FlushLock, which stacked one blocked pool thread per trigger behind a pass that itself
        // could wait up to 120 s on a 429 - the pile-up behind that lock was the thread blow-up
        // seen during upload bursts.
        private static readonly object _FlushGate = new object();
        private static bool _FlushPassRequested;
        private static Task _FlushPass;

        // How long to wait before retrying when the server is unreachable at all (distinct from a
        // 429, whose Retry-After the server dictates). Keeps a down server from being hammered in
        // a tight loop while still resuming the drain without waiting for an unrelated trigger.
        private const int ConnectionRetrySeconds = 15;

        // Pending timer-based flush retries (429 back-off, connection down). Rooted in a static
        // list: an unawaited Task.Delay chain that nothing else references can be collected by
        // the GC, and an unrooted pending task is finalizer-cancelled - which would silently drop
        // the retry. Completed entries are reaped lazily on the next scheduling.
        private static readonly object _ScheduledRetryLock = new object();
        private static readonly List<Task> _ScheduledRetries = new List<Task>();

        // Bounds the direct (fast-path) uploads: putCover / putRound each fire their own request,
        // and the load screen fires one putCover per new-to-cloud song (hundreds at once). Without
        // a bound that pins hundreds of pool threads (each for its 20 s timeout + Polly back-offs)
        // and overloads the server into connection refusals. Two in flight keeps the server calm;
        // the rest go straight to the outbox, which the flush worker drains.
        private static readonly SemaphoreSlim _DirectUploadGate = new SemaphoreSlim(2);

        // There is no SynchronizationContext installed on the main loop thread (net10), so an async
        // method started there - Init() below - resumes on the thread pool, and so does the websocket
        // read/dispatch loop. Anything that mutates game or UI state (CGame / CGraphics / CBase /
        // CProfiles) must therefore be applied on the loop thread: we queue those actions here and let
        // CGraphics.UpdateGameLogic drain the queue once per frame (Update). Post is safe from any thread.
        private static readonly Queue<Action> _PendingActions = new Queue<Action>();
        private static readonly object _PendingActionsMutex = new object();

        internal static void Post(Action action)
        {
            if (action != null)
            {
                lock (_PendingActionsMutex)
                {
                    _PendingActions.Enqueue(action);
                }
            }
        }

        public static void Update()
        {
            Action[] batch;
            lock (_PendingActionsMutex)
            {
                if (_PendingActions.Count == 0)
                    return;

                batch = new Action[_PendingActions.Count];
                for (int i = 0; i < batch.Length; i++)
                    batch[i] = _PendingActions.Dequeue();
            }

            for (int i = 0; i < batch.Length; i++)
            {
                try
                {
                    batch[i]();
                }
                catch (Exception e)
                {
                    CLog.CCloudLog.Error(e, "Cloud pump action failed: {msg}", CLog.Params(e.Message));
                }
            }
        }

        // Runs a blocking cloud call on the thread pool, then applies onComplete with its result on the
        // loop thread. The calling screen therefore never blocks the render pass; the result arrives the
        // following frame(s) via the pump. On any fault the caller gets the CLR default (null / empty) so
        // it can treat it as "no data yet" rather than crash.
        private static void RunAsync<T>(Func<T> work, Action<T> onComplete)
        {
            Task.Run(delegate
            {
                T result;
                try
                {
                    result = work();
                }
                catch (Exception e)
                {
                    CLog.CCloudLog.Error(e, "Cloud request failed: {msg}", CLog.Params(e.Message));
                    result = default(T);
                }

                Post(delegate
                {
                    if (onComplete != null)
                        onComplete(result);
                });
            });
        }

        /// <summary>
        /// True while the WebSocket to the cloud server is open; false while it is down,
        /// reconnecting, or before the first successful connection.
        /// </summary>
        public static bool IsConnected
        {
            get { return _Connected; }
        }

        // Endpoint names for the durable outbox; shared by the enqueue sites (putRound / putCover) and
        // the status overlay, so both sides agree on which queued record is a score vs a cover.
        public const string PutRoundEndpoint = "/api/putRound";
        public const string PutCoverEndpoint = "/api/putCover";

        // Number of score rounds currently queued for a later upload. Exposed only so the UI can
        // surface that a score is still pending; the queue logic lives in COutbox + FlushUploadQueue.
        public static int PendingScores
        {
            get { return COutbox.CountForEndpoint(PutRoundEndpoint); }
        }

        // Number of song covers currently queued for a later upload.
        public static int PendingCovers
        {
            get { return COutbox.CountForEndpoint(PutCoverEndpoint); }
        }
        // System.Text.Json options for the cloud wire format. PropertyNameCaseInsensitive is a
        // superset of the old Json.NET default (case-sensitive matching), so it accepts everything
        // the old deserializer accepted. WhenWritingNull reproduces the old
        // NullValueHandling.Ignore that the state sends used; it is a no-op when reading.
        // JsonStringEnumConverter: the server returns enum values as their name strings
        // ("TR_CONFIG_NORMAL") on some endpoints while other endpoints use bare integers;
        // Json.NET accepted both, System.Text.Json rejects strings on enum types by default.
        // CloudDateTimeConverter: the server stamps high-score dates as zone-less
        // "yyyy-MM-dd HH:mm:ss"; Json.NET parsed that, System.Text.Json only accepts RFC 3339.
        // Write paths deliberately keep the default options so payloads still carry the shapes
        // (bare integers, ISO dates) the server already accepts.
        // internal: Tests assert the production options directly (wire-shape regression guards).
        internal static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(), new CloudDateTimeConverter() }
        };
        private static string _State = "loading_game";
        // Written by the websocket dispatch when it is posted onto the loop thread (see Post/Update) and
        // read via isUpdated(); once dispatched, both sides run on the loop thread, so no extra sync.
        private static DateTime _LastPlayerChange = DateTime.Now;
        // Set by the websocket dispatch (posted onto the loop thread) and read by the sing screen (loop
        // thread); the same-thread discipline makes these plain fields safe.
        public static bool PauseSong;
        public static bool StopSong;
        public static bool RestartSong;
        // True from the moment the sing screen commits to a song (CScreenSing._LoadCurrentSong)
        // until the next song starts (startSong event). While committed, scoring settings from
        // player updates (difficulty and voice) must NOT be applied any more, otherwise a
        // console update that arrives late would flip the voice of an already playing song,
        // scoring one part of it against one voice and the rest against another. Profile
        // identity and the tone helper keep applying, as they do not affect the score.
        public static bool PlayerSettingsCommitted;

        // Result of a cloud POST, so a caller (like putRound) can tell a successful request apart
        // from a retryable "the server never answered" failure and a permanent HTTP error status.
        private struct CloudPost
        {
            public bool Success;
            // Only meaningful when Success == false.
            // true  -> transient, safe to retry later: either no HTTP response at all (network down), or
            //          the server signalled "wait and retry" (throttle 429 / unavailable 503 / 408). On
            //          this server a 429 is raised before the round is written, so it cannot duplicate.
            // false -> the server replied with a permanent or ambiguous error (400/401/403/404, 500/502/
            //          504); with no server-side dedup, retrying risks a double record, so callers drop it.
            public bool Transient;
            public byte[] Body;      // present only when Success
            public int StatusCode;  // 0 when there was no HTTP response at all
            // Seconds the server asked us to wait before retrying (Retry-After header), or 0 if it
            // did not tell us. Only set on a throttled/unavailable (transient) response.
            public int RetryAfterSeconds;
        }

        // Sends a JSON payload to the cloud API and reports the outcome (see CloudPost above).
        //
        // Blocks the calling thread until the request (plus its retry policy) finishes - on
        // purpose: every caller already runs on the thread pool (RunAsync / putCover / putRound /
        // FlushWorkerLoop / getAvatar), so no loop thread is involved and there is no
        // SynchronizationContext for the async continuations to pile up on. The old
        // .NET Framework build wrapped the request in a Task.Run to detach the completion path
        // from the WinForms UI context; on net10 that wrapper would only park one extra pool
        // thread per request for its whole duration, so it is gone.
        private static CloudPost DoPost(string url, string payload, AsyncRetryPolicy policy)
        {
            string uri = CConfig.CloudServerURL + url;
            int payloadLength = payload != null ? payload.Length : 0;
            CLog.CCloudLog.Information("POST to {uri} (payload {len} bytes)", CLog.Params(uri, payloadLength));

            try
            {
                HttpResponseMessage response;
                try
                {
                    response = policy.ExecuteAsync(async () =>
                    {
                        using (var content = new StringContent(payload ?? string.Empty, Encoding.UTF8, "application/json"))
                            return await _Client.PostAsync(uri, content);
                    }).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    // No HTTP response at all (connection refused / dropped / timeout). The retry
                    // policy has already retried the transient network hiccups that are safe to
                    // blind-retry, so a failure reaching here is a genuine "server was not
                    // reached" condition - safe to defer for a later upload.
                    CLog.CCloudLog.Error(e, "HTTP request to {uri} failed: {msg}", CLog.Params(uri, e.Message));
                    return new CloudPost { Success = false, Transient = true, StatusCode = 0 };
                }

                using (response)
                {
                    int statusCode = (int)response.StatusCode;
                    CLog.CCloudLog.Information("Status code: {code}", CLog.Params(statusCode));
                    if (statusCode < 200 || statusCode >= 300)
                    {
                        // The server answered but did not succeed.
                        // 408 / 429 / 503 are the "temporary, not processed, wait and retry" signals:
                        // our server's throttle throws 429 *before* the controller runs, so the round
                        // was never written and retrying cannot duplicate. Treat those as transient so
                        // they are queued and retried later.
                        // Any other 4xx/5xx (400/401/403/404, 500/502/504) is permanent or ambiguous
                        // (a 5xx may already have recorded the round), and with no server-side dedup we
                        // report it as not-retryable so the caller drops it rather than risk a double
                        // entry.
                        CLog.CCloudLog.Warning("Non-success status {code} from {uri}", CLog.Params(statusCode, uri));
                        int retryAfter = ParseRetryAfter(response);
                        return new CloudPost { Success = false, Transient = IsRetryableStatus(statusCode), StatusCode = statusCode, RetryAfterSeconds = retryAfter };
                    }
                    try
                    {
                        return new CloudPost
                        {
                            Success = true,
                            Transient = false,
                            StatusCode = statusCode,
                            Body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                        };
                    }
                    catch (Exception e)
                    {
                        CLog.CCloudLog.Error(e, "Reading response body from {uri} failed: {msg}", CLog.Params(uri, e.Message));
                        return new CloudPost { Success = false, Transient = true, StatusCode = statusCode };
                    }
                }
            }
            catch (Exception e)
            {
                // Last-resort guard: nothing from the HTTP layer may ever reach the loop thread.
                CLog.CCloudLog.Error(e, "Unexpected error contacting {uri}: {msg}", CLog.Params(uri, e.Message));
                return new CloudPost { Success = false, Transient = true, StatusCode = 0 };
            }
        }

        // "Wait and retry" statuses: transient by definition, and guaranteed not to have been applied
        // by the server (a 429 is raised before the round is written), so retrying them is safe even
        // without server-side deduplication. Permanent client errors (4xx) and ambiguous server errors
        // (500/502/504) are deliberately excluded.
        private static bool IsRetryableStatus(int statusCode)
        {
            return statusCode == 408 || statusCode == 429 || statusCode == 503;
        }

        // Reads the Retry-After header as a number of seconds (0 if absent or not a non-negative
        // integer). Laravel's throttle sends a plain integer of seconds, but be defensive.
        private static int ParseRetryAfter(HttpResponseMessage response)
        {
            IEnumerable<string> values;
            if (response.Headers.TryGetValues("Retry-After", out values))
            {
                foreach (string v in values)
                {
                    int seconds;
                    if (int.TryParse(v, out seconds) && seconds >= 0)
                        return seconds;
                }
            }
            return 0;
        }

        // Convenience wrapper for the read paths (loadSongs, getHighScores, getProfiles, getAvatar),
        // which care only about a successful body. Returns the raw bytes, or null on any failure.
        private static byte[] PostBytesToCloud(string url, string payload, AsyncRetryPolicy policy)
        {
            CloudPost result = DoPost(url, payload, policy);
            return result.Success ? result.Body : null;
        }

        private static string PostStringToCloud(string url, string payload, AsyncRetryPolicy policy)
        {
            byte[] data = PostBytesToCloud(url, payload, policy);
            return data == null ? null : Encoding.UTF8.GetString(data);
        }

        // Deserializes a JSON response into T, returning the CLR default on any failure instead of
        // throwing out through a UI-thread caller.
        private static T SafeDeserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json))
                return default(T);
            try
            {
                return JsonSerializer.Deserialize<T>(json, _JsonOptions);
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "JSON deserialization failed: {msg}", CLog.Params(e.Message));
                return default(T);
            }
        }

        private static ClientWebSocket GetWebSocket()
        {
            lock (_WebSocketLock)
            {
                return _WebSocket;
            }
        }

        private static void PublishWebSocket(ClientWebSocket newSocket)
        {
            ClientWebSocket oldSocket;
            lock (_WebSocketLock)
            {
                oldSocket = _WebSocket;
                _WebSocket = newSocket;
            }
            _Connected = newSocket != null && newSocket.State == WebSocketState.Open;

            if (oldSocket != null)
            {
                try { oldSocket.Dispose(); } catch { /* best-effort teardown of the replaced socket */ }
            }
        }

        private static bool IsShuttingDown()
        {
            return _ShutdownCts.IsCancellationRequested;
        }

        /// <summary>
        /// Asks the connection loop to drop its current back-off wait and re-attempt immediately. Best
        /// effort: a connect attempt already in flight runs to its own (bounded) timeout first.
        /// </summary>
        public static void RequestReconnect()
        {
            CancellationTokenSource current;
            lock (_ReconnectLock)
            {
                current = _ReconnectCts;
                _ReconnectCts = new CancellationTokenSource();
            }

            try
            {
                current.Cancel();
            }
            catch
            {
                // Best effort: cancelling a disposed CTS must never reach the game.
            }
        }

        // Back-off delay that also honors RequestReconnect(): cancelling it ends the wait early so the
        // loop re-attempts right away. Shutdown still wins via the linked token.
        private static async Task _Backoff(TimeSpan delay)
        {
            CancellationTokenSource reconnect;
            lock (_ReconnectLock)
            {
                reconnect = _ReconnectCts;
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_ShutdownCts.Token, reconnect.Token))
            {
                try
                {
                    await Task.Delay(delay, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    // An immediate retry was requested (or shutdown began); the loop decides the next step.
                }
            }
        }

        /// <summary>
        /// Cleanly tears down the cloud connection: notifies the peer, aborts any in-flight I/O,
        /// stops the reconnect loop and disposes the socket. Safe to call more than once.
        /// </summary>
        public static void Shutdown()
        {
            // First caller drives the teardown; any subsequent call is a no-op.
            if (Interlocked.Exchange(ref _ShutdownRequested, 1) == 1)
                return;

            CLog.CCloudLog.Information("Shutting down cloud connection...");

            // Notify the peer, entirely on the thread pool so that awaiting the send cannot deadlock
            // the UI thread's message pump, then wait (bounded) for it to be delivered.
            try
            {
                Task.Run(delegate
                {
                    try
                    {
                        Task sendState = setState("shutting_down");
                        // Give the message a moment to reach the peer before we tear the socket down.
                        sendState.Wait(TimeSpan.FromMilliseconds(500));
                    }
                    catch
                    {
                        // Best effort: a failed notification must not block shutdown.
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "Could not send 'shutting_down' state: {msg}", CLog.Params(e.Message));
            }

            // Best-effort final chance to deliver queued rounds while the connection (and hence the
            // HTTP endpoint) is likely still reachable. Runs as one pass on the single-flight flush
            // worker; the wait is bounded so exit stays responsive. A pass that cannot finish in
            // time is left to complete (or be abandoned at exit) and will be retried on start-up
            // / reconnect.
            try
            {
                Task flush = RequestUploadFlush();
                if (!flush.Wait(TimeSpan.FromSeconds(2)))
                    CLog.CCloudLog.Warning("Final upload flush still running; leaving it to complete or be abandoned at exit");
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "Final upload flush (best effort) failed: {msg}", CLog.Params(e.Message));
            }

            // Abort any in-flight ConnectAsync/ReceiveAsync (which unwinds the reconnect loop) and
            // release the socket.
            try { _ShutdownCts.Cancel(); } catch { /* ignore */ }
            PublishWebSocket(null);

            CLog.CCloudLog.Information("Cloud connection closed");
        }

        // Sends one text frame. Never faults: a send that cannot go through (socket closed,
        // mid-reconnect, ...) is logged and swallowed, so callers may ignore the returned Task
        // without producing an unobserved-task exception.
        private static async Task SendToSocket(string data)
        {
            ClientWebSocket ws = GetWebSocket();
            if (ws == null || ws.State != WebSocketState.Open)
            {
                CLog.CCloudLog.Warning("Websocket send skipped (no open socket), {len} bytes", CLog.Params(data != null ? data.Length : 0));
                return;
            }

            byte[] encoded = Encoding.UTF8.GetBytes(data);
            var segment = new ArraySegment<Byte>(encoded, 0, encoded.Length);
            CLog.CCloudLog.Information("Sending Websocket message: {data}", CLog.Params(data));

            try
            {
                await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "Websocket send failed: {msg}", CLog.Params(e.Message));
            }
        }

        private static Task SubscribeToChannel(string channel)
        {
            string message = JsonSerializer.Serialize(new
            {
                @event = "pusher:subscribe",
                data = new
                {
                    auth = "",
                    channel = channel
                }
            });

            return SendToSocket(message);
        }

        // Bounded receive into a MemoryStream so a large (or hostile) frame cannot OOM the client.
        private static async Task<string> ReadFromSocket(ClientWebSocket ws)
        {
            const int MaxMessageBytes = 8 * 1024 * 1024;

            using (var ms = new MemoryStream())
            {
                var segment = new ArraySegment<Byte>(new Byte[8192]);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(segment, _ShutdownCts.Token);
                    ms.Write(segment.Array, segment.Offset, result.Count);
                    if (ms.Length > MaxMessageBytes)
                        throw new InvalidOperationException("Incoming websocket message exceeds the allowed size.");
                }
                while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);

                using (var reader = new StreamReader(ms, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        public static async void Init()
        {
            // async void: an exception escaping this method would be raised on the UI
            // SynchronizationContext and terminate the process. This top-level catch guarantees the
            // cloud service can only ever stop trying, never crash the game.
            try
            {
                while (true)
                {
                    if (IsShuttingDown())
                        break;

                    try
                    {
                        if (!await TryConnect())
                        {
                            if (IsShuttingDown())
                                break;
                            // Server not reachable: back off briefly, then try again. The game keeps
                            // running locally in the meantime. RequestReconnect() skips the wait.
                            await _Backoff(TimeSpan.FromSeconds(5));
                            continue;
                        }

                        if (IsShuttingDown())
                            break;

                        await SubscribeToChannel("game-control");
                        await SubscribeToChannel("game-state");
                        await setState(_State);

                        if (IsShuttingDown())
                            break;

                        // The connection (and hence the HTTP endpoint behind it) is reachable again -
                        // a good moment to retry the rounds that are still queued. The pass runs on
                        // the single-flight flush worker, so the read loop and the menu stay
                        // responsive.
                        _ = RequestUploadFlush();

                        // Read and dispatch until the connection drops; then reconnect below.
                        await ReadLoop();

                        // Reaching this point only happens once the socket has dropped.
                        _Connected = false;
                    }
                    catch (Exception e)
                    {
                        if (IsShuttingDown())
                            break;
                        CLog.CCloudLog.Error(e, "Cloud connection error, will reconnect: {msg}", CLog.Params(e.Message));
                    }

                    if (IsShuttingDown())
                        break;

                    CLog.CCloudLog.Warning("Connection to websocket closed; reconnecting...");
                    await _Backoff(TimeSpan.FromSeconds(1));
                }

                // The only way out of the loop is being asked to shut down.
                CLog.CCloudLog.Information("Cloud service stopped (shutting down)");
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "Cloud service stopped: {msg}", CLog.Params(e.Message));
            }
        }

        private static async Task<bool> TryConnect()
        {
            string uri = CConfig.CloudServerWebsocketURI;
            CLog.CCloudLog.Information("Connecting to websocket: {uri}", CLog.Params(uri));

            ClientWebSocket newSocket = null;
            try
            {
                await _WSRetryPolicy.ExecuteAsync(async () =>
                {
                    ClientWebSocket ws = new ClientWebSocket();
                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ShutdownCts.Token, timeoutCts.Token))
                    {
                        try
                        {
                            await ws.ConnectAsync(new Uri(uri), linkedCts.Token);
                            newSocket = ws;
                        }
                        catch
                        {
                            ws.Dispose();
                            throw;
                        }
                    }
                });
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "Websocket connect failed: {msg}", CLog.Params(e.Message));
                return false;
            }

            if (newSocket == null || newSocket.State != WebSocketState.Open)
            {
                CLog.CCloudLog.Warning("Websocket connect did not reach Open state");
                return false;
            }

            CLog.CCloudLog.Information("Websocket connected");
            PublishWebSocket(newSocket);
            return true;
        }

        private static async Task ReadLoop()
        {
            while (true)
            {
                ClientWebSocket ws = GetWebSocket();
                if (ws == null || ws.State != WebSocketState.Open)
                    return;

                EventMessage message = null;
                try
                {
                    string incoming = await ReadFromSocket(ws);
                    CLog.CCloudLog.Information("Received message: {data}", CLog.Params(incoming));
                    message = JsonSerializer.Deserialize<EventMessage>(incoming, _JsonOptions);
                }
                catch (Exception e)
                {
                    // A transport or parse problem ends this connection; Init() reconnects.
                    CLog.CCloudLog.Error(e, "Websocket read error: {msg}", CLog.Params(e.Message));
                    return;
                }

                // Dispatch is isolated so one malformed event cannot tear down the whole connection.
                if (message != null)
                {
                    CLog.CCloudLog.Information("Event \"{eventName}\" received with data: {data}", CLog.Params(message.eventName, message.data));
                    try
                    {
                        // Runs on the thread pool: it only parses the payload and posts the state
                        // changes onto the loop thread (Post), so it must never touch game/UI state here.
                        DispatchEvent(message);
                    }
                    catch (Exception e)
                    {
                        CLog.CCloudLog.Error(e, "Error handling event: {msg}", CLog.Params(e.Message));
                    }
                }
            }
        }

        private static void DispatchEvent(EventMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.eventName))
                return;

            // Runs on the thread pool: only parse the payload and post the state changes onto the loop
            // thread (Post). Reading CGraphics.CurrentScreen or writing CGame / CBase here would race the
            // main loop thread, so those happen entirely inside the posted actions.
            switch (message.eventName)
            {
                case "previewSong":
                    {
                        int previewSongId = ExtractIdFromEvent(message);
                        Post(delegate
                        {
                            if (CGraphics.CurrentScreen.GetType() == typeof(Screens.CScreenSong))
                                PreviewSong(previewSongId);
                        });
                        break;
                    }

                case "startSong":
                    {
                        int songId = ExtractIdFromEvent(message);
                        Post(delegate
                        {
                            if (CGraphics.CurrentScreen.GetType() == typeof(Screens.CScreenSong))
                            {
                                StopSong = false;
                                // A new song starts: the prepare window is open again, so player settings
                                // (voice and difficulty) apply live until the song commits.
                                PlayerSettingsCommitted = false;
                                // Fire-and-forget: SendToSocket swallows/never faults, so the loop thread does
                                // not block waiting for the socket frame to be enqueued.
                                setState("preparing_song", songId);
                                StartSong(songId);
                            }
                        });
                        break;
                    }

                case "togglePause":
                    Post(delegate { PauseSong = !PauseSong; });
                    break;

                case "stopSong":
                    Post(delegate { StopSong = true; });
                    break;

                case "restartSong":
                    Post(delegate { RestartSong = true; });
                    break;

                case "PlayerUpdated":
                    {
                        CloudPlayer player = SafeParsePlayer(message);
                        if (player == null)
                            break;

                        Post(delegate { ApplyPlayerUpdate(player); });
                        break;
                    }

                default:
                    break;
            }
        }

        // Safely extracts the song id from an event, returning -1 (handled as "none") on bad input.
        private static int ExtractIdFromEvent(EventMessage message)
        {
            try
            {
                EventData data = JsonSerializer.Deserialize<EventData>(message.data, _JsonOptions);
                return data != null ? data.id : -1;
            }
            catch
            {
                return -1;
            }
        }

        // Parses a PlayerUpdated payload off the loop thread (JSON only; no game-state mutation) so the
        // network thread never blocks and never touches CGame. Returns null on any malformed input.
        private static CloudPlayer SafeParsePlayer(EventMessage message)
        {
            try
            {
                return JsonSerializer.Deserialize<CloudPlayer>(message.data, _JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        // Runs on the loop thread (posted by DispatchEvent): applies a player update to CGame.Players.
        private static void ApplyPlayerUpdate(CloudPlayer player)
        {
            int index = player.Id - 1;
            if (index < 0 || index >= CGame.NumPlayers)
            {
                CLog.CCloudLog.Warning("PlayerUpdated ignored: player id {id} out of range", CLog.Params(player.Id));
                return;
            }

            CGame.Players[index].ProfileID = player.ProfileGuid;
            CGame.Players[index].ToneHelperText = player.ToneHelperText;

            if (PlayerSettingsCommitted)
            {
                // The current song is already committed: applying a difficulty or voice change here would
                // rewrite the scoring of a song that is being evaluated mid-way. The change is kept
                // server-side and therefore takes effect for the next song, where the prepare window
                // applies it again.
                CLog.CCloudLog.Warning("PlayerUpdated: ignoring difficulty {difficulty} and voice {voice} of player {id}, the current song is committed",
                    CLog.Params((int)player.Difficulty, player.Voice, player.Id));
            }
            else
            {
                CGame.Players[index].Difficulty = player.Difficulty;
                CGame.Players[index].VoiceNr = player.Voice;
            }

            _LastPlayerChange = DateTime.Now;
        }

        // Log (and mark observed) any task fault that no caller awaited, so nothing crashes the app.
        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                foreach (Exception ex in e.Exception.InnerExceptions)
                {
                    CLog.CCloudLog.Error(ex, "Unobserved task exception: {msg}", CLog.Params(ex.Message));
                }
            }
            catch
            {
                // Never let the handler itself take the process down.
            }
            finally
            {
                e.SetObserved();
            }
        }

        public static Task setState(string state)
        {
            _State = state;
            string message = JsonSerializer.Serialize(new GameStateMessage(new GameState(state)), _JsonOptions);
            return SendToSocket(message);
        }

        public static Task setState(string state, int songId)
        {
            _State = state;
            string message = JsonSerializer.Serialize(new GameStateMessage(new GameState(state, songId)), _JsonOptions);
            return SendToSocket(message);
        }

        public static bool isUpdated(DateTime lastCheck)
        {
            return (_LastPlayerChange > lastCheck);
        }

        public static CloudSong[] loadSongs(List<CloudSong> songs)
        {
            string json = JsonSerializer.Serialize(new { Key = CConfig.CloudServerKey, Data = songs });
            CloudSong[] result = SafeDeserialize<CloudSong[]>(PostStringToCloud("/api/loadSongs", json, _HTTPRetryPolicy));
            return result ?? new CloudSong[0];
        }
        public static void getProfiles(Action<CProfile[]> onReady)
        {
            string json = JsonSerializer.Serialize(new { Key = CConfig.CloudServerKey });
            RunAsync(delegate
            {
                CProfile[] profiles = SafeDeserialize<CProfile[]>(PostStringToCloud("/api/getProfiles", json, _HTTPRetryPolicy));
                return profiles ?? new CProfile[0];
            }, onReady);
        }

        // Downloads the avatar image on the thread pool and calls onReady (also on a pool thread, so the
        // caller can do the file write off the loop thread) with the validated bytes, or null on failure.
        public static void getAvatar(string fileName, Action<byte[]> onReady)
        {
            string json = JsonSerializer.Serialize(new { Key = CConfig.CloudServerKey, FileName = fileName });
            Task.Run(delegate
            {
                byte[] data = PostBytesToCloud("/api/getAvatar", json, _HTTPRetryPolicy);
                byte[] result = null;
                if (data != null && data.Length > 0)
                {
                    // Validate that the image decodes fully before handing it out (equivalent of the
                    // former GDI+ "copy into a Bitmap" load, done with SkiaSharp).
                    try
                    {
                        using (var bmp = SKBitmap.Decode(data))
                        {
                            if (bmp != null)
                                result = data;
                        }
                    }
                    catch (Exception e)
                    {
                        CLog.CCloudLog.Error(e, "Failed to decode cloud avatar: {msg}", CLog.Params(e.Message));
                    }
                }

                if (onReady != null)
                    onReady(result);
            });
        }

        public static void getHighScores(int databaseSongId, EGameMode gameMode, EHighscoreStyle highscoreStyle, Action<SDBScoreEntry[]> onReady)
        {
            string json = JsonSerializer.Serialize(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, GameMode = gameMode, Style = highscoreStyle });
            RunAsync(delegate
            {
                SDBScoreEntry[] scores = SafeDeserialize<SDBScoreEntry[]>(PostStringToCloud("/api/getHighScores", json, _HTTPRetryPolicy));
                return scores ?? new SDBScoreEntry[0];
            }, onReady);
        }
        public static void putCover(int databaseSongId, string data, string format)
        {
            string json = JsonSerializer.Serialize(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, Data = data, Format = format });
            string id = Guid.NewGuid().ToString();
            const string Endpoint = PutCoverEndpoint;

            // putCover is an idempotent PUT (the same cover for the same song), so it is always safe
            // to retry. Run the request off the calling thread (DoPost blocks until the request plus
            // its retry policy finish) and, on a "wait and retry" throttle (429/503/408) or a network
            // miss, queue it in the durable outbox for a later flush instead of dropping it. A
            // permanent/ambiguous error is dropped and logged: re-sending what the server keeps
            // refusing would just fail again.
            //
            // The direct POST is gated (two in flight): the load screen fires one putCover per
            // new-to-cloud song (hundreds at once), and unbounded parallelism would pin a pool
            // thread per request (20 s timeout + Polly back-offs each) and overload the server into
            // connection refusals. When the gate is busy the cover goes straight to the outbox and
            // the flush worker delivers it.
            Task.Run(delegate
            {
                if (!_DirectUploadGate.Wait(0))
                {
                    COutbox.Enqueue(Endpoint, json, id);
                    CLog.CCloudLog.Information("putCover deferred to the upload queue ({n} pending)", CLog.Params(COutbox.Count));
                    RequestUploadFlush();
                    return;
                }

                try
                {
                    CloudPost result = DoPost(Endpoint, json, _HTTPRetryPolicy);
                    if (result.Success)
                        return;

                    if (result.Transient)
                    {
                        COutbox.Enqueue(Endpoint, json, id);
                        CLog.CCloudLog.Warning("putCover could not be delivered now - queued for a later upload ({n} pending)", CLog.Params(COutbox.Count));
                        RequestUploadFlush();
                    }
                    else
                    {
                        CLog.CCloudLog.Warning("putCover was refused by the server (HTTP {code}) and dropped", CLog.Params(result.StatusCode));
                    }
                }
                finally
                {
                    _DirectUploadGate.Release();
                }
            });
        }

        public static void putRound(SPlayer[] players, Action<List<int>> onReady)
        {
            // Posts the delivered (new-entry) IDs onto the loop thread - the caller adds them to
            // CGame.NewEntryIDs there, which is the one write that must not happen off the loop thread.
            Action noData = delegate
            {
                if (onReady != null)
                    onReady(new List<int>());
            };

            if (players == null || players.Length == 0)
            {
                CLog.CCloudLog.Warning("putRound skipped: no players in round");
                Post(noData);
                return;
            }

            CSong song = CSongs.GetSong(players[0].SongID);
            if (song == null || song.DataBaseSongID < 0)
            {
                CLog.CCloudLog.Warning("putRound skipped: no valid cloud song for the round");
                Post(noData);
                return;
            }

            const string Endpoint = PutRoundEndpoint;
            string json = JsonSerializer.Serialize(new { Key = CConfig.CloudServerKey, DataBaseSongID = song.DataBaseSongID, SongFinished = players[0].SongFinished, Scores = players });
            string id = Guid.NewGuid().ToString();

            // Deliberately off the loop thread: DoPost blocks the calling thread until the request (plus
            // the retry policy) completes, and the transient branch below writes the outbox to disk. Both
            // are I/O that must never stall the render pass. Only the application of the resulting
            // new-entry IDs is marshalled back onto the loop thread via Post.
            // The direct POST is gated (two in flight, shared with putCover): under load the round
            // goes straight to the outbox and the flush worker delivers it, instead of piling
            // another blocked pool thread onto the server.
            Task.Run(delegate
            {
                if (!_DirectUploadGate.Wait(0))
                {
                    // Safe to queue: a round that never got a response was never applied, so the
                    // worker's later delivery cannot duplicate it (no server-side dedup needed).
                    COutbox.Enqueue(Endpoint, json, id);
                    CLog.CCloudLog.Information("putRound deferred to the upload queue ({n} pending)", CLog.Params(COutbox.Count));
                    RequestUploadFlush();
                    Post(noData);
                    return;
                }

                try
                {
                    // Non-idempotent: use the conservative policy (connection failures only) to avoid
                    // submitting the same round twice if the server already accepted it.
                    CloudPost result = DoPost(Endpoint, json, _HTTPRetryPolicyNonIdempotent);

                    List<int> ids = new List<int>();
                    if (result.Success)
                        ids = SafeDeserialize<List<int>>(Encoding.UTF8.GetString(result.Body)) ?? new List<int>();
                    else if (result.Transient)
                    {
                        // Safe to retry: either no response at all, or a "wait and retry" throttle (429/503)
                        // that the server rejected *before* writing the round. We never got an application of
                        // it, so queueing it cannot produce a duplicate even with no server-side dedup.
                        COutbox.Enqueue(Endpoint, json, id);
                        CLog.CCloudLog.Warning("putRound could not be delivered now - queued for a later upload ({n} pending)", CLog.Params(COutbox.Count));

                        // Ask the background flush to retry it promptly. It honors the server's Retry-After
                        // (when this was a throttle) on the flush worker, so this screen doesn't block.
                        RequestUploadFlush();
                    }
                    else
                    {
                        // A permanent or ambiguous error (the safe 429/503/408 cases are handled by the
                        // transient branch above, which queues them). Since a 5xx may already have recorded
                        // the round and we have no server-side deduplication, drop it and log clearly rather
                        // than risk a double entry.
                        CLog.CCloudLog.Warning("putRound was refused by the server (HTTP {code}) and dropped to avoid a double record", CLog.Params(result.StatusCode));
                    }

                    List<int> delivered = ids;
                    Post(delegate
                    {
                        if (onReady != null)
                            onReady(delivered);
                    });
                }
                finally
                {
                    _DirectUploadGate.Release();
                }
            });
        }

        // Outcome of one bounded flush pass, telling the worker how to continue.
        private enum FlushResult
        {
            // Kept delivering (or dropped records) until the per-pass cap or an empty queue.
            Continue,
            // The server throttled us; a retry after its back-off is already scheduled.
            Throttled,
            // No connection at all; the worker backs off on a timer and retries.
            ConnectionDown
        }

        // Delivers whichever records are queued, oldest first, in order, as far as the connection
        // allows. Bounded to MaxPerPass records per pass; the worker loop runs pass after pass
        // while records remain, so the queue keeps draining at a server-tolerated rate instead of
        // moving only on unrelated triggers.
        //
        // Concurrency: only the single-flight flush worker below calls this, so passes never
        // overlap and no lock is needed here. The HTTP (DoPost) blocks this worker thread, which
        // is fine - it is a dedicated pool thread, never the loop thread.
        private static FlushResult FlushUploadQueue()
        {
            if (!CConfig.UseCloudServer)
                return FlushResult.Continue;

            const int MaxPerPass = 5;
            const int MaxRetryAfterSeconds = 120;
            int processed = 0;

            while (processed < MaxPerPass)
            {
                COutbox.SOutboxEntry entry = COutbox.Oldest();
                if (entry == null)
                    return FlushResult.Continue;

                // Enforced caps: give up on records that are too old or have been retried too
                // many times, so a long outage can't accumulate or replay without limit.
                if (entry.Attempts >= COutbox.MaxAttempts || DateTime.Now - entry.CreatedAt > COutbox.MaxAge)
                {
                    COutbox.Drop(entry.Id);
                    CLog.CCloudLog.Warning("Dropping queued cloud upload {ep} (gave up after {n} attempts)", CLog.Params(entry.Endpoint, entry.Attempts));
                    processed++;
                    continue;
                }

                CloudPost result = DoPost(entry.Endpoint, entry.Payload, _HTTPRetryPolicyNonIdempotent);
                if (result.Success)
                {
                    COutbox.Remove(entry.Id);
                    CLog.CCloudLog.Information("Queued {ep} uploaded successfully ({n} left)", CLog.Params(entry.Endpoint, COutbox.Count));
                    processed++;
                    continue;
                }

                if (result.Transient)
                {
                    // Keep the record for a later pass (FIFO order preserved, no hammering).
                    COutbox.BumpAttempt(entry.Id);

                    // If the server throttled us (a 429/503) and told us when to try again, honor
                    // that wait (capped) by scheduling ONE more pass after the back-off. The wait
                    // is a rooted timer, so no thread is parked for its duration - the pre-fix
                    // code blocked a pool thread here for up to 120 s.
                    if (result.RetryAfterSeconds > 0)
                    {
                        int waitSeconds = Math.Min(result.RetryAfterSeconds, MaxRetryAfterSeconds);
                        CLog.CCloudLog.Warning("Throttled (HTTP {code}); resuming the flush in {s}s ({n} queued)", CLog.Params(result.StatusCode, waitSeconds, COutbox.Count));
                        ScheduleFlushIn(waitSeconds);
                        return FlushResult.Throttled;
                    }

                    CLog.CCloudLog.Warning("Flush paused: no cloud connection ({n} queued)", CLog.Params(COutbox.Count));
                    return FlushResult.ConnectionDown;
                }

                // Not a "wait and retry" status (a safe 429/503/408 is kept above for retry), so it
                // is a permanent/ambiguous error; with no server dedup, drop it rather than risk a
                // duplicate.
                COutbox.Drop(entry.Id);
                CLog.CCloudLog.Warning("Dropping queued {ep} (HTTP {code}) - not safe to retry without server dedupe", CLog.Params(entry.Endpoint, result.StatusCode));
                processed++;
            }

            return FlushResult.Continue;
        }

        // The single-flight flush worker. Runs pass after pass while the queue has records (or a
        // trigger is pending), so the queue KEEPS DRAINING while the game runs instead of moving
        // only on unrelated triggers (reconnect / start-up / shutdown). A burst of N triggers
        // still coalesces into this one worker, so it never stacks passes or blocked threads.
        // Retires when the queue is empty and no trigger is pending, or when the server asks for
        // a back-off (429) or is unreachable (timed retry scheduled).
        private static void FlushWorkerLoop()
        {
            while (true)
            {
                bool runPass;
                lock (_FlushGate)
                {
                    runPass = CConfig.UseCloudServer && (_FlushPassRequested || COutbox.Count > 0);
                    _FlushPassRequested = false;
                    if (!runPass)
                    {
                        // Retire under the gate - before the task is observable as completed - so
                        // the next trigger (which also takes the gate) always starts a fresh
                        // worker instead of setting a flag nobody is left to consume.
                        _FlushPass = null;
                        return;
                    }
                }

                FlushResult result;
                try
                {
                    result = FlushUploadQueue();
                }
                catch (Exception e)
                {
                    CLog.CCloudLog.Error(e, "Upload flush pass failed: {msg}", CLog.Params(e.Message));
                    result = FlushResult.ConnectionDown;
                }

                if (result == FlushResult.Throttled)
                    return; // the server-directed retry is already scheduled

                if (result == FlushResult.ConnectionDown)
                {
                    // No connection: retry on a rooted timer instead of hammering in a tight loop
                    // or waiting for an unrelated trigger to wake us.
                    ScheduleFlushIn(ConnectionRetrySeconds);
                    return;
                }

                // Continue: the loop head re-checks queue/flag and runs another pass, or retires.
            }
        }

        // Schedules one more flush pass after a back-off, without parking a thread for the wait.
        // The task is rooted in _ScheduledRetries: an unawaited Task.Delay chain with no other
        // reference can be collected by the GC, and an unrooted pending task is finalizer-
        // cancelled - which would silently drop the retry and park the queue.
        private static void ScheduleFlushIn(int waitSeconds)
        {
            Task retry = Task.Delay(TimeSpan.FromSeconds(waitSeconds)).ContinueWith(_ => RequestUploadFlush(), TaskScheduler.Default);
            lock (_ScheduledRetryLock)
            {
                for (int i = _ScheduledRetries.Count - 1; i >= 0; i--)
                {
                    if (_ScheduledRetries[i].IsCompleted)
                        _ScheduledRetries.RemoveAt(i);
                }
                _ScheduledRetries.Add(retry);
            }
        }

        // Requests one bounded upload-flush pass on the single-flight worker, starting the worker
        // if none is running, and returns the pass's task so shutdown can await it with a bound;
        // fire-and-forget callers ignore it (or discard it in an async context). Repeated
        // triggers coalesce: the flag is re-asserted and the running worker picks it up on its
        // next lap, so a burst of triggers costs one extra pass, not one blocked thread each.
        public static Task RequestUploadFlush()
        {
            lock (_FlushGate)
            {
                _FlushPassRequested = true;
                if (_FlushPass == null || _FlushPass.IsCompleted)
                    _FlushPass = Task.Run(FlushWorkerLoop);
                return _FlushPass;
            }
        }

        public static void AssignPlayersFromCloud()
        {
            CProfiles.LoadProfiles();

            string json = JsonSerializer.Serialize(new { Key = CConfig.CloudServerKey });
            // Fetch off the loop thread; the assignment writes CGame.Players, which must land on the loop
            // thread (Post), not on the pool thread this is called from.
            Task.Run(delegate
            {
                CloudPlayer[] cloudPlayers = SafeDeserialize<CloudPlayer[]>(PostStringToCloud("/api/getPlayers", json, _HTTPRetryPolicy));
                if (cloudPlayers == null)
                    return;

                Post(delegate
                {
                    int count = Math.Min(CGame.NumPlayers, cloudPlayers.Length);
                    for (int i = 0; i < count; i++)
                    {
                        CGame.Players[i].ProfileID = cloudPlayers[i].ProfileGuid;
                        CGame.Players[i].Difficulty = cloudPlayers[i].Difficulty;
                        CGame.Players[i].ToneHelperText = cloudPlayers[i].ToneHelperText;
                        CGame.Players[i].VoiceNr = cloudPlayers[i].Voice;
                    }
                });
            });
        }

        public static int GetCurrentSongId()
        {
            CSong song = CGame.GetSong();
            if (song == null)
                return -1;
            return song.ID;
        }

        public static bool StartSong(int dataBaseSongID)
        {
            if (GetCurrentSongId() != -1)
                return false;

            int songID = CSongs.GetSongIdFromDataBaseSongId(dataBaseSongID);

            if (songID == -1)
                return false;

            CSong song = CSongs.GetSong(songID);

            EGameMode gm = CSongs.GetSong(songID).IsDuet ? EGameMode.TR_GAMEMODE_DUET : EGameMode.TR_GAMEMODE_NORMAL;

            CGame.Reset();
            CGame.ClearSongs();

            CBase.BackgroundMusic.LoadPreview(song, song.Preview.StartTime);

            if (CGame.AddSong(songID, gm))
            {
                CGraphics.FadeTo(EScreen.Prepare);
                return true;
            }
            else
            {
                return false;
            }

        }

        public static bool PreviewSong(int dataBaseSongID)
        {
            if (GetCurrentSongId() != -1)
                return false;

            int songID = CSongs.GetSongIdFromDataBaseSongId(dataBaseSongID);

            if (songID == -1)
                return false;

            if (songID == CBase.BackgroundMusic.GetSongID())
                return false;

            CSong song = CSongs.GetSong(songID);

            CBase.BackgroundMusic.LoadPreview(song, song.Preview.StartTime);

            CGraphics.FadeTo(EScreen.Song);
            return true;
        }

    }
    class GameState
    {
        [JsonPropertyName("state")]
        public string State { get; set; }
        [JsonPropertyName("song_id")]
        public int? SongId { get; set; }

        public GameState(string state)
        {
            State = state;
        }

        public GameState(string state, int songId)
        {
            State = state;
            SongId = songId;
        }
    }

    class GameStateMessage
    {
        [JsonPropertyName("channel")]
        public string Channel { get; } = "game-state";
        [JsonPropertyName("event")]
        public string Event { get; } = "client-setState";
        [JsonPropertyName("data")]
        public GameState State { get; set; }

        public GameStateMessage(GameState state)
        {
            State = state;
        }

    }

    public class CloudSong
    {
        public Guid GUID { get; set; }
        public string Artist { get; set; }
        public string Title { get; set; }
        public List<string> Editions { get; set; }
        public List<string> Genres { get; set; }
        public List<string> Languages { get; set; }
        public string Album { get; set; }
        public string Year { get; set; }
        public int DataBaseSongID { get; set; }
        public int NumPlayed { get; set; }
        public System.DateTime DateAdded { get; set; }
        public int NewToCloud { get; set; }
        public List<string> Voices { get; set; }
    }

    class EventMessage
    {
        [JsonPropertyName("event")]
        public string eventName { get; set; }

        [JsonPropertyName(nameof(data))]
        public string data { get; set; }

        [JsonPropertyName(nameof(channel))]
        public string channel { get; set; }
    }

    class EventData
    {
        [JsonPropertyName(nameof(id))]
        public int id { get; set; }
    }

    class CloudPlayer
    {
        [JsonPropertyName(nameof(Id))]
        public int Id { get; set; }
        [JsonPropertyName("profile_guid")]
        public Guid ProfileGuid { get; set; }
        [JsonPropertyName("game_difficulty")]
        public EGameDifficulty Difficulty { get; set; }
        // "voice" stays lowercase: it is the server's literal wire name (not a casing of "Voice").
        [JsonPropertyName("voice")]
        public int Voice { get; set; }
        [JsonPropertyName("tone_helper_text")]
        public EOffOn ToneHelperText { get; set; }
    }

    // The SSIS server stamps high-score dates as "yyyy-MM-dd HH:mm:ss" (a space, no zone). The
    // legacy Json.NET reader parsed that, but System.Text.Json only accepts RFC 3339, so one such
    // entry sank the whole getHighScores response. Read that exact shape as local time (it is the
    // zone-less wall clock of the user's own server, and CScreenHighscore's "ToLocalTime()"
    // needs Local kind) and fall back to the strict reader for everything else. Write the same
    // zone-less form, the shape the server already accepts from us.
    class CloudDateTimeConverter : JsonConverter<DateTime>
    {
        private const string _SpaceFormat = "yyyy-MM-dd HH:mm:ss";

        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && DateTime.TryParseExact(reader.GetString(), _SpaceFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime date))
                return date;

            return reader.GetDateTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(_SpaceFormat, CultureInfo.InvariantCulture));
        }
    }
}