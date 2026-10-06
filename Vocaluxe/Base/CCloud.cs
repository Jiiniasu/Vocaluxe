using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
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
            // .NET Framework's HTTP transport automatically appends "Expect: 100-continue"
            // to POST/PUT requests (default of ServicePointManager.Expect100Continue is true).
            // The Apache front of our cloud server answers that header with a 400.
            // Disabling it only skips the handshake; the body is sent with the headers,
            // which is all the server expects.
            System.Net.ServicePointManager.Expect100Continue = false;

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
        private static volatile bool _Connected;
        // Serializes the upload-queue flush so concurrent triggers (start-up + reconnect) never fight
        // over the same queued records.
        private static readonly object _FlushLock = new object();

        /// <summary>
        /// True while the WebSocket to the cloud server is open; false while it is down,
        /// reconnecting, or before the first successful connection.
        /// </summary>
        public static bool IsConnected
        {
            get { return _Connected; }
        }

        // Number of rounds currently queued for a later upload. Exposed only so the UI can surface
        // that a score is still pending; the queue logic itself lives in COutbox + FlushUploadQueue.
        public static int PendingUploads
        {
            get { return COutbox.Count; }
        }
        private static readonly JsonSerializerSettings _JsonSerializerSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        private static string _State = "loading_game";
        // Written by the WebSocket dispatch, read via isUpdated(). Both run on the UI thread, so
        // no extra synchronization is required.
        private static DateTime _LastPlayerChange = DateTime.Now;
        // Set by the WebSocket dispatch (UI thread) and read by the game loop (UI thread).
        public static bool PauseSong;
        public static bool StopSong;
        public static bool RestartSong;

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
        }

        // Sends a JSON payload to the cloud API and reports the outcome (see CloudPost above).
        //
        // The whole request runs on the thread pool (Task.Run). This detaches the completion path
        // from the UI thread's SynchronizationContext, so a synchronous caller (a WinForms screen,
        // on the UI thread) can block on the result below WITHOUT deadlocking - Polly + HttpClient
        // would otherwise marshal their continuations back to the very UI thread we are blocked on.
        private static CloudPost DoPost(string url, string payload, AsyncRetryPolicy policy)
        {
            string uri = CConfig.CloudServerURL + url;
            int payloadLength = payload != null ? payload.Length : 0;
            CLog.CCloudLog.Information("POST to {uri} (payload {len} bytes)", CLog.Params(uri, payloadLength));

            try
            {
                return Task.Run(delegate
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
                            return new CloudPost { Success = false, Transient = IsRetryableStatus(statusCode), StatusCode = statusCode };
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
                }).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // Last-resort guard: nothing from the HTTP layer may ever reach the UI thread.
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
                return JsonConvert.DeserializeObject<T>(json);
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
            // HTTP endpoint) is likely still reachable. Bounded and non-blocking past the wait so
            // exit stays responsive; a flush that cannot finish in time is simply abandoned and will
            // be retried on start-up / reconnect.
            try
            {
                Task flush = Task.Run(new Action(FlushUploadQueue));
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
            string message = JsonConvert.SerializeObject(new
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
                            // running locally in the meantime.
                            await Task.Delay(TimeSpan.FromSeconds(5), _ShutdownCts.Token);
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
                        // a good moment to retry the rounds that are still queued. Run off the UI
                        // thread so the read loop and the menu stay responsive.
                        FlushOnNextConnection();

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
                    await Task.Delay(TimeSpan.FromSeconds(1), _ShutdownCts.Token);
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
                    message = JsonConvert.DeserializeObject<EventMessage>(incoming);
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
                        await DispatchEvent(message);
                    }
                    catch (Exception e)
                    {
                        CLog.CCloudLog.Error(e, "Error handling event: {msg}", CLog.Params(e.Message));
                    }
                }
            }
        }

        private static async Task DispatchEvent(EventMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.eventName))
                return;

            switch (message.eventName)
            {
                case "previewSong":
                    if (CGraphics.CurrentScreen.GetType() == typeof(Screens.CScreenSong))
                        PreviewSong(ExtractIdFromEvent(message));
                    break;

                case "startSong":
                    if (CGraphics.CurrentScreen.GetType() == typeof(Screens.CScreenSong))
                    {
                        int songId = ExtractIdFromEvent(message);
                        StopSong = false;
                        await setState("preparing_song", songId);
                        StartSong(songId);
                    }
                    break;

                case "togglePause":
                    PauseSong = !PauseSong;
                    break;

                case "stopSong":
                    StopSong = true;
                    break;

                case "restartSong":
                    RestartSong = true;
                    break;

                case "PlayerUpdated":
                    HandlePlayerUpdated(message);
                    break;

                default:
                    break;
            }
        }

        // Safely extracts the song id from an event, returning -1 (handled as "none") on bad input.
        private static int ExtractIdFromEvent(EventMessage message)
        {
            try
            {
                EventData data = JsonConvert.DeserializeObject<EventData>(message.data);
                return data != null ? data.id : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static void HandlePlayerUpdated(EventMessage message)
        {
            try
            {
                CloudPlayer player = JsonConvert.DeserializeObject<CloudPlayer>(message.data);
                if (player == null)
                    return;

                int index = player.Id - 1;
                if (index < 0 || index >= CGame.NumPlayers)
                {
                    CLog.CCloudLog.Warning("PlayerUpdated ignored: player id {id} out of range", CLog.Params(player.Id));
                    return;
                }

                CGame.Players[index].ProfileID = player.ProfileGuid;
                CGame.Players[index].Difficulty = player.Difficulty;
                CGame.Players[index].ToneHelperText = player.ToneHelperText;
                CGame.Players[index].VoiceNr = player.Voice;
                _LastPlayerChange = DateTime.Now;
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "PlayerUpdated handling failed: {msg}", CLog.Params(e.Message));
            }
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
            string message = JsonConvert.SerializeObject(new GameStateMessage(new GameState(state)), _JsonSerializerSettings);
            return SendToSocket(message);
        }

        public static Task setState(string state, int songId)
        {
            _State = state;
            string message = JsonConvert.SerializeObject(new GameStateMessage(new GameState(state, songId)), _JsonSerializerSettings);
            return SendToSocket(message);
        }

        public static bool isUpdated(DateTime lastCheck)
        {
            return (_LastPlayerChange > lastCheck);
        }

        public static CloudSong[] loadSongs(List<CloudSong> songs)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, Data = songs });
            CloudSong[] result = SafeDeserialize<CloudSong[]>(PostStringToCloud("/api/loadSongs", json, _HTTPRetryPolicy));
            return result ?? new CloudSong[0];
        }
        public static CProfile[] getProfiles()
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey });
            CProfile[] result = SafeDeserialize<CProfile[]>(PostStringToCloud("/api/getProfiles", json, _HTTPRetryPolicy));
            return result ?? new CProfile[0];
        }

        public static Image getAvatar(string fileName)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, FileName = fileName });

            byte[] data = PostBytesToCloud("/api/getAvatar", json, _HTTPRetryPolicy);
            if (data == null || data.Length == 0)
                return null;

            try
            {
                // Copy into a Bitmap so the returned image is fully loaded and independent of any
                // underlying stream's lifetime.
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms))
                {
                    return new Bitmap(img);
                }
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "Failed to decode cloud avatar: {msg}", CLog.Params(e.Message));
                return null;
            }
        }

        public static SDBScoreEntry[] getHighScores(int databaseSongId, EGameMode gameMode, EHighscoreStyle highscoreStyle)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, GameMode = gameMode, Style = highscoreStyle });
            SDBScoreEntry[] result = SafeDeserialize<SDBScoreEntry[]>(PostStringToCloud("/api/getHighScores", json, _HTTPRetryPolicy));
            return result ?? new SDBScoreEntry[0];
        }
        public static void putCover(int databaseSongId, string data, string format)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, Data = data, Format = format });
            // Upload is fire-and-forget from the caller's perspective; failures are logged in PostBytesToCloud.
            PostStringToCloud("/api/putCover", json, _HTTPRetryPolicy);
        }

        public static List<int> putRound(SPlayer[] players)
        {
            if (players == null || players.Length == 0)
            {
                CLog.CCloudLog.Warning("putRound skipped: no players in round");
                return new List<int>();
            }

            CSong song = CSongs.GetSong(players[0].SongID);
            if (song == null || song.DataBaseSongID < 0)
            {
                CLog.CCloudLog.Warning("putRound skipped: no valid cloud song for the round");
                return new List<int>();
            }

            const string Endpoint = "/api/putRound";
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = song.DataBaseSongID, SongFinished = players[0].SongFinished, Scores = players });

            // Non-idempotent: use the conservative policy (connection failures only) to avoid
            // submitting the same round twice if the server already accepted it.
            CloudPost result = DoPost(Endpoint, json, _HTTPRetryPolicyNonIdempotent);
            if (result.Success)
                return SafeDeserialize<List<int>>(Encoding.UTF8.GetString(result.Body)) ?? new List<int>();

            // The round was NOT recorded server-side.
            if (result.Transient)
            {
                // No HTTP response was ever received, so the server almost certainly did not store
                // the round. Queue it so it can be retried later (on reconnect / startup) - even with
                // no server-side deduplication, retrying after "no reply" is the safe case.
                COutbox.Enqueue(Endpoint, json, Guid.NewGuid().ToString());
                CLog.CCloudLog.Warning("putRound could not be delivered now - queued for a later upload ({n} pending)", CLog.Params(COutbox.Count));
            }
            else
            {
                // A permanent or ambiguous error (the safe 429/503/408 cases are handled by the
                // transient branch above, which queues them). Since a 5xx may already have recorded the
                // round and we have no server-side deduplication, drop it and log clearly rather than
                // risk a double entry.
                CLog.CCloudLog.Warning("putRound was refused by the server (HTTP {code}) and dropped to avoid a double record", CLog.Params(result.StatusCode));
            }
            return new List<int>();
        }

        // Delivers whichever rounds are queued, oldest first, in order, as far as the connection
        // allows. Each pass is bounded (at most MaxPerPass records) and stops at the first
        // connection-level failure, so it never hammers a down server. The HTTP itself runs on the
        // thread pool (inside DoPost); callers on the UI thread should invoke this from a Task.Run.
        public static void FlushUploadQueue()
        {
            if (!CConfig.UseCloudServer)
                return;

            lock (_FlushLock)
            {
                const int MaxPerPass = 5;
                int processed = 0;

                while (processed < MaxPerPass)
                {
                    COutbox.SOutboxEntry entry = COutbox.Oldest();
                    if (entry == null)
                        break;

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
                        // Connection is still down: keep the record for the next opportunity, and stop
                        // this pass (FIFO order preserved, no server hammering).
                        COutbox.BumpAttempt(entry.Id);
                        CLog.CCloudLog.Warning("Flush paused: no cloud connection ({n} queued)", CLog.Params(COutbox.Count));
                        break;
                    }

                    // Not a "wait and retry" status (a safe 429/503/408 is kept above for retry), so it
                    // is a permanent/ambiguous error; with no server dedup, drop it rather than risk a
                    // duplicate.
                    COutbox.Drop(entry.Id);
                    CLog.CCloudLog.Warning("Dropping queued {ep} (HTTP {code}) - not safe to retry without server dedupe", CLog.Params(entry.Endpoint, result.StatusCode));
                    processed++;
                }
            }
        }

        // Fire-and-forget the upload flush off the UI thread. A non-async wrapper keeps the
        // un-awaited task out of the (async) call site, so it does not raise a CS4014 warning.
        private static void FlushOnNextConnection()
        {
            Task.Run(new Action(FlushUploadQueue));
        }

        public static void AssignPlayersFromCloud()
        {
            CProfiles.LoadProfiles();

            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey });
            CloudPlayer[] cloudPlayers = SafeDeserialize<CloudPlayer[]>(PostStringToCloud("/api/getPlayers", json, _HTTPRetryPolicy));
            if (cloudPlayers == null)
                return;

            int count = Math.Min(CGame.NumPlayers, cloudPlayers.Length);
            for (int i = 0; i < count; i++)
            {
                CGame.Players[i].ProfileID = cloudPlayers[i].ProfileGuid;
                CGame.Players[i].Difficulty = cloudPlayers[i].Difficulty;
                CGame.Players[i].ToneHelperText = cloudPlayers[i].ToneHelperText;
                CGame.Players[i].VoiceNr = cloudPlayers[i].Voice;
            }
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
        [JsonProperty("state")]
        public string State { get; set; }
        [JsonProperty("song_id")]
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
        [JsonProperty("channel")]
        public string Channel { get; } = "game-state";
        [JsonProperty("event")]
        public string Event { get; } = "client-setState";
        [JsonProperty("data")]
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
        [JsonProperty("event")]
        public string eventName { get; set; }

        [JsonProperty("data")]
        public string data { get; set; }

        [JsonProperty("channel")]
        public string channel { get; set; }
    }

    class EventData
    {
        [JsonProperty("id")]
        public int id { get; set; }
    }

    class CloudPlayer
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("profile_guid")]
        public Guid ProfileGuid { get; set; }
        [JsonProperty("game_difficulty")]
        public EGameDifficulty Difficulty { get; set; }
        [JsonProperty("voice")]
        public int Voice { get; set; }
        [JsonProperty("tone_helper_text")]
        public EOffOn ToneHelperText { get; set; }
    }
}