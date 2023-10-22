using System;
using System.Text;
using Newtonsoft.Json;
using System.Net.Http;
using System.IO;
using Vocaluxe.Base.Server;
using VocaluxeLib;
using VocaluxeLib.Log;
using Newtonsoft.Json.Linq;
using VocaluxeLib.Profile;
using System.Drawing;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Vocaluxe.Base
{
    static class CCloud
    {
        private static readonly HttpClient _Client = new HttpClient();
        private static readonly ClientWebSocket _WebSocket = new ClientWebSocket();
        public static bool PauseSong;
        public static bool StopSong;
        public static bool RestartSong;

        private static Task sendString(ClientWebSocket ws, String data, CancellationToken cancellation)
        {
            var encoded = Encoding.UTF8.GetBytes(data);
            var buffer = new ArraySegment<Byte>(encoded, 0, encoded.Length);
            return ws.SendAsync(buffer, WebSocketMessageType.Text, true, cancellation);
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
            await sendString(_WebSocket, "{\"event\":\"pusher:subscribe\",\"data\":{\"auth\":\"\",\"channel\":\"game-control\"}}", CancellationToken.None);
            while (_WebSocket.State == WebSocketState.Open)
            {
                EventMessage message = JsonConvert.DeserializeObject<EventMessage>(await readString(_WebSocket));
                CLog.CCloudLog.Information("Event \"{eventName}\" received with data: {data}", CLog.Params(message.eventName, message.data));
                switch (message.eventName)
                {
                    case "previewSong":
                        CVocaluxeServer.DoTask(CVocaluxeServer.PreviewSong, JsonConvert.DeserializeObject<EventData>(message.data).id);
                        break;
                    case "startSong":
                        if (CGraphics.CurrentScreen.GetType() != typeof(Screens.CScreenSing))
                        {
                            StopSong = false;
                            System.Threading.Thread.Sleep(1000);
                            if (CVocaluxeServer.DoTask(CVocaluxeServer.PreviewSong, JsonConvert.DeserializeObject<EventData>(message.data).id))
                            {
                                System.Threading.Thread.Sleep(5000);
                            }
                            CCloud.AssignPlayersFromCloud();
                            CVocaluxeServer.DoTask(CVocaluxeServer.StartSong, JsonConvert.DeserializeObject<EventData>(message.data).id);
                            System.Threading.Thread.Sleep(1000);
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

        public static CloudSong[] loadSongs(List<CloudSong> songs)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, Data = songs });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/loadSongs", content).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<CloudSong[]>(responseString);
        }
        public static CProfile[] getProfiles()
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/getProfiles", content).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<CProfile[]>(responseString);
        }

        public static Image getAvatar(string fileName)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, AvatarId = fileName });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/getAvatar", content).Result.Content;
            byte[] responseString = response.ReadAsByteArrayAsync().Result;

            return Image.FromStream(new MemoryStream(responseString));
        }

        public static SDBScoreEntry[] getHighScores(int databaseSongId, EGameMode gameMode, EHighscoreStyle highscoreStyle)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, GameMode = gameMode, Style = highscoreStyle });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/getHighScores", content).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<SDBScoreEntry[]>(responseString);
        }
        public static void putCover(int databaseSongId, string data, string format)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, Data = data, Format = format });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/putCover", content).Result.Content;
        }

        public static List<int> putRound(SPlayer[] players)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = CSongs.GetSong(players[0].SongID).DataBaseSongID, SongFinished = players[0].SongFinished, Scores = players });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/putRound", content).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;
            return JsonConvert.DeserializeObject<List<int>>(responseString);
        }      

        public static void AssignPlayersFromCloud()
        {
            CProfiles.LoadProfiles();

            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/getPlayers", content).Result.Content;
            string responseString = response.ReadAsStringAsync().Result;

            Guid[] cloudPlayers = JsonConvert.DeserializeObject<Guid[]>(responseString);

            for (int i = 0; i < CGame.NumPlayers; i++)
            {
                CGame.Players[i].ProfileID = cloudPlayers[i];
            }
        }

    }
    public class CloudSong
    {
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
}
