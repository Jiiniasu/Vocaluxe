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
        private static readonly AsyncRetryPolicy _WSRetryPolicy = Policy.Handle<WebSocketException>().WaitAndRetryAsync(6, retryAttempt =>
        {
            CLog.CCloudLog.Information("Retry attempt {attempt}...", CLog.Params(retryAttempt));
            return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
        });
        private static ClientWebSocket _WebSocket;
        private static readonly JsonSerializerSettings _JsonSerializerSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        private static string _State = "loading_game";
        private static DateTime _LastPlayerChange = DateTime.Now;
        public static bool PauseSong;
        public static bool StopSong;
        public static bool RestartSong;

        // Sends a JSON payload to the cloud API and returns the raw response body bytes (or null,
        // with a log entry, if the request fails after retries or returns an error status).
        //
        // The whole request runs on the thread pool (Task.Run). This detaches the completion path
        // from the UI thread's SynchronizationContext, so a synchronous caller (a WinForms screen,
        // on the UI thread) can block on the result below WITHOUT deadlocking - Polly + HttpClient
        // would otherwise marshal their continuations back to the very UI thread we are blocked on.
        private static byte[] PostBytesToCloud(string url, string payload, AsyncRetryPolicy policy)
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
                        CLog.CCloudLog.Error(e, "HTTP request to {uri} failed: {msg}", CLog.Params(uri, e.Message));
                        return null;
                    }

                    using (response)
                    {
                        int statusCode = (int)response.StatusCode;
                        CLog.CCloudLog.Information("Status code: {code}", CLog.Params(statusCode));
                        if (statusCode < 200 || statusCode >= 300)
                        {
                            CLog.CCloudLog.Warning("Non-success status {code} from {uri}", CLog.Params(statusCode, uri));
                            return null;
                        }
                        try
                        {
                            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        }
                        catch (Exception e)
                        {
                            CLog.CCloudLog.Error(e, "Reading response body from {uri} failed: {msg}", CLog.Params(uri, e.Message));
                            return null;
                        }
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // Last-resort guard: nothing from the HTTP layer may ever reach the UI thread.
                CLog.CCloudLog.Error(e, "Unexpected error contacting {uri}: {msg}", CLog.Params(uri, e.Message));
                return null;
            }
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

        private static Task sendString(ClientWebSocket ws, String data, CancellationToken cancellation)
        {
            var encoded = Encoding.UTF8.GetBytes(data);
            var buffer = new ArraySegment<Byte>(encoded, 0, encoded.Length);
            CLog.CCloudLog.Information("Sending Websocket message: {data}", CLog.Params(data));
            return ws.SendAsync(buffer, WebSocketMessageType.Text, true, cancellation);
        }

        private static Task subscribeToChannel(string channel)
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

            return sendString(_WebSocket, message, CancellationToken.None);
        }

        private static async Task<String> readString(ClientWebSocket ws)
        {
            ArraySegment<Byte> buffer = new ArraySegment<byte>(new Byte[8192]);

            WebSocketReceiveResult result = null;

            using (var ms = new MemoryStream())
            {
                do
                {
                    result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);

                using (var reader = new StreamReader(ms, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        public static async void Init()
        {
            while (true)
            {
                CLog.CCloudLog.Information("Connecting to websocket: {uri}", CLog.Params(CConfig.CloudServerWebsocketURI));
                await _WSRetryPolicy.ExecuteAsync(async () =>
                {
                    _WebSocket = new ClientWebSocket();
                    await _WebSocket.ConnectAsync(new Uri(CConfig.CloudServerWebsocketURI), CancellationToken.None);
                });
                CLog.CCloudLog.Information("Websocket status: {status}", CLog.Params(_WebSocket.State.ToString()));
                await subscribeToChannel("game-control");
                await subscribeToChannel("game-state");
                await setState(_State);
                while (_WebSocket.State == WebSocketState.Open)
                {
                    EventMessage message;
                    try
                    {
                        string incoming = await readString(_WebSocket);
                        CLog.CCloudLog.Information("Received message: {data}", CLog.Params(incoming));
                        message = JsonConvert.DeserializeObject<EventMessage>(incoming);
                    }
                    catch (Exception e)
                    {
                        CLog.CCloudLog.Error(e, "Websocket error: {ExceptionMessage}", CLog.Params(e.Message));
                        break;
                    }
                    CLog.CCloudLog.Information("Event \"{eventName}\" received with data: {data}", CLog.Params(message.eventName, message.data));
                    switch (message.eventName)
                    {
                        case "previewSong":
                            if (CGraphics.CurrentScreen.GetType() == typeof(Screens.CScreenSong))
                                PreviewSong(JsonConvert.DeserializeObject<EventData>(message.data).id);
                            break;
                        case "startSong":
                            if (CGraphics.CurrentScreen.GetType() == typeof(Screens.CScreenSong))
                            {
                                int songId = JsonConvert.DeserializeObject<EventData>(message.data).id;
                                await setState("preparing_song", songId);
                                StopSong = false;
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
                            CloudPlayer player = JsonConvert.DeserializeObject<CloudPlayer>(message.data);
                            CGame.Players[player.Id - 1].ProfileID = player.ProfileGuid;
                            CGame.Players[player.Id - 1].Difficulty = player.Difficulty;
                            CGame.Players[player.Id - 1].ToneHelperText = player.ToneHelperText;
                            CGame.Players[player.Id - 1].VoiceNr = player.Voice;
                            _LastPlayerChange = DateTime.Now;
                            break;
                        default:
                            break;
                    }
                }
                CLog.CCloudLog.Warning("Connection to websocket closed!");
            }
            ;
        }

        public static Task setState(string state)
        {
            string message = JsonConvert.SerializeObject(new GameStateMessage(new GameState(state)), _JsonSerializerSettings);
            _State = state;
            return sendString(_WebSocket, message, CancellationToken.None);
        }

        public static Task setState(string state, int songId)
        {
            string message = JsonConvert.SerializeObject(new GameStateMessage(new GameState(state, songId)), _JsonSerializerSettings);
            _State = state;
            return sendString(_WebSocket, message, CancellationToken.None);
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

            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = song.DataBaseSongID, SongFinished = players[0].SongFinished, Scores = players });

            // Non-idempotent: use the conservative policy (connection failures only) to avoid
            // submitting the same round twice if the server already accepted it.
            List<int> result = SafeDeserialize<List<int>>(PostStringToCloud("/api/putRound", json, _HTTPRetryPolicyNonIdempotent));
            return result ?? new List<int>();
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