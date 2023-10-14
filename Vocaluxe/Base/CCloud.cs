using System;
using System.Text;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.IO;
using Vocaluxe.Base.Server;
using VocaluxeLib;
using Newtonsoft.Json.Linq;
using VocaluxeLib.Profile;
using System.Drawing;
using System.Collections.Generic;

namespace Vocaluxe.Base
{
    static class CCloud
    {
        private static readonly HttpClient _Client = new HttpClient();
        public static bool PauseSong;
        public static bool StopSong;
        public static bool RestartSong;

        public static void Init()
        {
            using (WebClient wc = new WebClient())
            {
                Uri uri = new Uri(CConfig.CloudServerURL + "/eventstream/?Key=" + CConfig.CloudServerKey);
                wc.OpenReadCompleted += (object sender, OpenReadCompletedEventArgs e) =>
                {
                    var sr = new StreamReader(e.Result);

                    while (!sr.EndOfStream)
                    {
                        String data = sr.ReadLine();
                        Console.WriteLine(data);
                        EventMessage message = JsonConvert.DeserializeObject<EventMessage>(data);
                        if (message != null)
                        {
                            switch (message.function)
                            {
                                case "ping":
                                    // Do nothing for now, maybe complain and restart connection if we havn't received one in 10 seconds?
                                    break;
                                case "previewSong":
                                    CVocaluxeServer.DoTask(CVocaluxeServer.PreviewSong, message.songID);
                                    break;
                                case "startSong":
                                    if (CGraphics.CurrentScreen.GetType() != typeof(Screens.CScreenSing))
                                    {
                                        StopSong = false;
                                        System.Threading.Thread.Sleep(1000);
                                        if (CVocaluxeServer.DoTask(CVocaluxeServer.PreviewSong, message.songID))
                                        {
                                            System.Threading.Thread.Sleep(5000);
                                        }
                                        CCloud.AssignPlayersFromCloud();
                                        CVocaluxeServer.DoTask(CVocaluxeServer.StartSong, message.songID);
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
                    }
                    sr.Close();
                    wc.OpenReadAsync(uri);
                };
                wc.OpenReadAsync(uri);
            }
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

        public static string putRound(SPlayer[] players)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = CSongs.GetSong(players[0].SongID).DataBaseSongID, SongFinished = players[0].SongFinished, Scores = JsonConvert.SerializeObject(players) });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/putRound", content).Result.Content;
            return JObject.Parse(response.ReadAsStringAsync().Result)["id"].ToString();
        }

        public static int putScore(int databaseSongId, string roundId, SPlayer player)
        {
            string json = JsonConvert.SerializeObject(new { Key = CConfig.CloudServerKey, DataBaseSongID = databaseSongId, RoundID = roundId, Data = player });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = _Client.PostAsync(CConfig.CloudServerURL + "/api/putScore", content).Result.Content;
            return JsonConvert.DeserializeObject<SDBScoreEntry>(response.ReadAsStringAsync().Result).ID;
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
}
