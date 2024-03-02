using System;
using System.Text;
using Newtonsoft.Json;
using System.Net.Http;
using System.IO;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Profile;
using System.Drawing;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;
using VocaluxeLib.Songs;

namespace Vocaluxe.Base
{
    static class CCloud
    {
        private static readonly HttpClient _Client = new HttpClient();
        private static readonly AsyncRetryPolicy _RetryPolicy = Policy.Handle<HttpRequestException>().WaitAndRetryAsync(6, retryAttempt =>
        {
            CLog.CCloudLog.Information("Retry attempt {attempt}...", CLog.Params(retryAttempt));
            return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
        });
        private static readonly ClientWebSocket _WebSocket = new ClientWebSocket();
        public static bool PauseSong;
        public static bool StopSong;
        public static bool RestartSong;

        private static async Task<HttpResponseMessage> SendToCloud(string url, string payload, string info = "")
        {
            HttpResponseMessage response;
            CLog.CCloudLog.Information("POST to {uri}...", CLog.Params(url));
            return await _RetryPolicy.ExecuteAsync(async () =>
            {
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                response = await _Client.PostAsync(CConfig.CloudServerURL + url, content);
                CLog.CCloudLog.Information("Status code: {code}", CLog.Params(response.StatusCode));
                response.EnsureSuccessStatusCode();
                return response;
            });
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
            CLog.CCloudLog.Information("Connecting to websocket: {uri}", CLog.Params(CConfig.CloudServerWebsocketURI));
            await _WebSocket.ConnectAsync(new Uri(CConfig.CloudServerWebsocketURI), CancellationToken.None);
            CLog.CCloudLog.Information("Websocket status: {status}", CLog.Params(_WebSocket.State.ToString()));
            await subscribeToChannel("game-control");
            await subscribeToChannel("game-state");
            await setState("loading_game");
            while (_WebSocket.State == WebSocketState.Open)
            {
                EventMessage message = JsonConvert.DeserializeObject<EventMessage>(await readString(_WebSocket));
                CLog.CCloudLog.Information("Event \"{eventName}\" received with data: {data}", CLog.Params(message.eventName, message.data));
                switch (message.eventName)
                {
                    case "previewSong":
                        PreviewSong(JsonConvert.DeserializeObject<EventData>(message.data).id);
                        break;
                    case "startSong":
                        if (CGraphics.CurrentScreen.GetType() != typeof(Screens.CScreenSing))
                        {
                            await setState("starting_song");
                            StopSong = false;
                            if (PreviewSong(JsonConvert.DeserializeObject<EventData>(message.data).id))
                            {
                                System.Threading.Thread.Sleep(5000);
                            }
                            AssignPlayersFromCloud();
                            StartSong(JsonConvert.DeserializeObject<EventData>(message.data).id);
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
                    default:
                        break;
                }
            }
            CLog.CCloudLog.Warning("Connection to websocket closed!");
        }

        public static Task setState(string state)
        {
            string message = JsonConvert.SerializeObject(new
            {
                channel = "game-state",
                @event = "client-setState",
                data = JsonConvert.SerializeObject(new
                {
                    state = state,
                })
            });
            return sendString(_WebSocket, message, CancellationToken.None);
        }

        public static Task setState(string state, int songId)
        {
            string message = JsonConvert.SerializeObject(new
            {
                channel = "game-state",
                @event = "client-setState",
                data = JsonConvert.SerializeObject(new
                {
                    state = state,
                    song_id = songId
                })
            });

            return sendString(_WebSocket, message, CancellationToken.None);
        }

        public static CloudSong[] loadSongs(List<CloudSong> songs)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, Data = songs });
            var response = SendToCloud("/api/loadSongs", json).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<CloudSong[]>(responseString);
        }
        public static CProfile[] getProfiles()
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey });

            var response = SendToCloud("/api/getProfiles", json).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<CProfile[]>(responseString);
        }

        public static Image getAvatar(string fileName)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, FileName = fileName });

            var response = SendToCloud("/api/getAvatar", json).Result.Content;
            byte[] responseString = response.ReadAsByteArrayAsync().Result;

            return Image.FromStream(new MemoryStream(responseString));
        }

        public static SDBScoreEntry[] getHighScores(int databaseSongId, EGameMode gameMode, EHighscoreStyle highscoreStyle)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, GameMode = gameMode, Style = highscoreStyle });

            var response = SendToCloud("/api/getHighScores", json).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<SDBScoreEntry[]>(responseString);
        }
        public static void putCover(int databaseSongId, string data, string format)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, Data = data, Format = format });

            var response = SendToCloud("/api/putCover", json).Result.Content;
        }

        public static List<int> putRound(SPlayer[] players)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = CSongs.GetSong(players[0].SongID).DataBaseSongID, SongFinished = players[0].SongFinished, Scores = players });

            var response = SendToCloud("/api/putRound", json).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<List<int>>(responseString);
        }

        public static void AssignPlayersFromCloud()
        {
            CProfiles.LoadProfiles();

            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey });
            var response = SendToCloud("/api/getPlayers", json).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;

            CloudPlayer[] cloudPlayers = JsonConvert.DeserializeObject<CloudPlayer[]>(responseString);

            for (int i = 0; i < CGame.NumPlayers; i++)
            {
                CGame.Players[i].ProfileID = cloudPlayers[i].PlayerGuid;
                CGame.Players[i].Difficulty = cloudPlayers[i].Difficulty;
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

            EGameMode gm = CSongs.GetSong(songID).IsDuet ? EGameMode.TR_GAMEMODE_DUET : EGameMode.TR_GAMEMODE_NORMAL;

            CGame.Reset();
            CGame.ClearSongs();

            if (CGame.AddSong(songID, gm))
            {
                CGraphics.FadeTo(EScreen.Sing);
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
    public class CloudSong
    {
        public Guid GUID { get; set; }
        public string Artist { get; set; }
        public string Title { get; set; }
        public List<string> Editions { get; set; }
        public List<string> Genres { get; set; }
        public string Album { get; set; }
        public string Year { get; set; }
        public int DataBaseSongID { get; set; }
        public int NumPlayed { get; set; }
        public System.DateTime DateAdded { get; set; }
        public int NewToCloud { get; set; }
    }

    class EventMessage
    {
        [JsonProperty("event")]
        public string eventName { get; set; }

        [JsonProperty("data")]
        public string data { get; set; }
    }

    class EventData
    {
        [JsonProperty("id")]
        public int id { get; set; }
    }

    class CloudPlayer
    {
        public Guid PlayerGuid { get; set; }
        public EGameDifficulty Difficulty { get; set; }
    }
}
