#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using NUnit.Framework;
using System.Text.Json;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Profile;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Regression guard for the 2026-10-16 cloud wire-format break: the SSIS server returns enum
    ///     values as their name strings, which the legacy Json.NET reader accepted but
    ///     System.Text.Json rejects by default - the whole getProfiles response failed and no
    ///     profiles loaded (sing button dead). These tests pin the production options against that
    ///     exact shape.
    /// </summary>
    [TestFixture]
    public class CCloudJsonTest
    {
        // One profile from the server's /api/getProfiles response, captured verbatim on 2026-10-16
        // (AvatarFileName left out: its setter has CProfiles/avatar side effects and is not part of
        // the enum regression).
        private const string _ServerProfileJson = @"
[
    {
        ""ID"": ""de166291-6dd7-4628-aefc-dd394834eb9e"",
        ""PlayerName"": ""Jiiniasu"",
        ""Difficulty"": ""TR_CONFIG_NORMAL"",
        ""UserRole"": ""TR_USERROLE_NORMAL"",
        ""Active"": ""TR_CONFIG_ON""
    }
]";

        [Test]
        public void TestServerWireFormat_EnumNamesDeserialize()
        {
            CProfile[] profiles = JsonSerializer.Deserialize<CProfile[]>(_ServerProfileJson, CCloud._JsonOptions);

            Assert.AreEqual(1, profiles.Length);
            Assert.AreEqual("de166291-6dd7-4628-aefc-dd394834eb9e", profiles[0].ID.ToString());
            Assert.AreEqual("Jiiniasu", profiles[0].PlayerName);
            Assert.AreEqual(EGameDifficulty.TR_CONFIG_NORMAL, profiles[0].Difficulty);
            Assert.AreEqual(EUserRole.TR_USERROLE_NORMAL, profiles[0].UserRole);
            Assert.AreEqual(EOffOn.TR_CONFIG_ON, profiles[0].Active);
        }

        // The client has always sent enum values as bare integers; the read path must keep
        // accepting that shape as well (older servers, or a future one that switches style).
        [Test]
        public void TestReadPath_StillAcceptsBareIntegers()
        {
            // EUserRole is a [Flags] enum: GUEST = 0, NORMAL = 1 (not a zero-based run).
            string json = @"[ { ""PlayerName"": ""Ints"", ""Difficulty"": 1, ""UserRole"": 1, ""Active"": 1 } ]";
            CProfile[] profiles = JsonSerializer.Deserialize<CProfile[]>(json, CCloud._JsonOptions);

            Assert.AreEqual(EGameDifficulty.TR_CONFIG_NORMAL, profiles[0].Difficulty);
            Assert.AreEqual(EUserRole.TR_USERROLE_NORMAL, profiles[0].UserRole);
            Assert.AreEqual(EOffOn.TR_CONFIG_ON, profiles[0].Active);
        }

        // The websocket player-update payload mixes literal server wire names (game_difficulty,
        // lowercase voice, tone_helper_text) with the same name-string enums.
        [Test]
        public void TestCloudPlayerWireFormat_EnumNamesAndLiteralNames()
        {
            string json = @"
{
    ""Id"": 0,
    ""profile_guid"": ""de166291-6dd7-4628-aefc-dd394834eb9e"",
    ""game_difficulty"": ""TR_CONFIG_HARD"",
    ""voice"": 1,
    ""tone_helper_text"": ""TR_CONFIG_ON""
}";
            CloudPlayer player = JsonSerializer.Deserialize<CloudPlayer>(json, CCloud._JsonOptions);

            Assert.AreEqual(EGameDifficulty.TR_CONFIG_HARD, player.Difficulty);
            Assert.AreEqual(1, player.Voice);
            Assert.AreEqual(EOffOn.TR_CONFIG_ON, player.ToneHelperText);
        }
    }
}