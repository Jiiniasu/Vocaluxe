using System;
using System.Text;
using Newtonsoft.Json;
using System.Net.Http;

namespace Vocaluxe.Base
{
    static class CCloud
    {
        private static readonly HttpClient _Client = new HttpClient();

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
}
