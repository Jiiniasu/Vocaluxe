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
using Vocaluxe.Base;
using Vocaluxe.Lib.Sound.Record;
using VocaluxeLib.Xml;

namespace Tests.Vocaluxe
{
    [TestFixture]
    public class CConfigTest
    {
        [SetUp]
        public void SetUp()
        {
            CConfig.Config = new CConfig.SConfig();
            CConfig.Config.Game.SongFolder = new string[0];
            CConfig.Config.Game.Players = new string[CSettings.MaxNumPlayer];
            CConfig.Config.Record.MicConfig = new SMicConfig[CSettings.MaxNumPlayer];
        }

        //Same lenient deserialization as used in CConfig._LoadConfig
        private readonly CXmlDeserializer _Deserializer = new CXmlDeserializer(new CXmlErrorHandler(e => { }));

        private static void _SetMicConfig(int player, string name, string driver, int channel)
        {
            CConfig.Config.Record.MicConfig[player] = new SMicConfig(0)
            {
                DeviceName = name,
                DeviceDriver = driver,
                Channel = channel
            };
        }

        [Test]
        public void TestGetPlayerFromMicConfig_MatchOnNameAndChannel()
        {
            _SetMicConfig(1, "Mic A", "Mic A0", 2);

            //Same name and channel should match, also with a changed driver (e.g. PortAudio device indices shift between sessions)
            Assert.AreEqual(2, CConfig.GetPlayerFromMicConfig("Mic A", "Mic A0", 2));
            Assert.AreEqual(2, CConfig.GetPlayerFromMicConfig("Mic A", "Mic A1", 2));

            //A different channel or a different device must not match
            Assert.AreEqual(0, CConfig.GetPlayerFromMicConfig("Mic A", "Mic A1", 3));
            Assert.AreEqual(0, CConfig.GetPlayerFromMicConfig("Mic B", "other", 2));
        }

        [Test]
        public void TestGetPlayerFromMicConfig_MatchOnDriverAndChannel()
        {
            _SetMicConfig(1, "old name", "GUID-123", 1);

            //A different name with the same driver (e.g. a changed display name) should still match
            Assert.AreEqual(2, CConfig.GetPlayerFromMicConfig("new name", "GUID-123", 1));
            Assert.AreEqual(0, CConfig.GetPlayerFromMicConfig("new name", "GUID-124", 1));
        }

        [Test]
        public void TestGetPlayerFromMicConfig_Unconfigured()
        {
            //No player with a saved configuration must match
            Assert.AreEqual(0, CConfig.GetPlayerFromMicConfig("Mic A", "Mic A0", 1));

            _SetMicConfig(1, "Mic A", "Mic A0", 2);
            Assert.AreEqual(0, CConfig.GetPlayerFromMicConfig("Mic A", "Mic A0", 1));
        }

        [Test]
        public void TestMicConfig_RoundTrip()
        {
            _SetMicConfig(1, "Mic A", "Mic A0", 2);

            CConfig.SConfig restored;
            _RoundTripConfig(out restored);

            //The saved configuration must survive a save/load cycle
            Assert.AreEqual("Mic A", restored.Record.MicConfig[1].DeviceName);
            Assert.AreEqual("Mic A0", restored.Record.MicConfig[1].DeviceDriver);
            Assert.AreEqual(2, restored.Record.MicConfig[1].Channel);

            //Other players must stay empty
            Assert.AreEqual("", restored.Record.MicConfig[0].DeviceName);
            Assert.AreEqual(0, restored.Record.MicConfig[0].Channel);
        }

        [Test]
        public void TestMicConfig_OfflineEntrySurvives()
        {
            //A configuration for a device that is not present must keep its values, it will resume when the device is back
            _SetMicConfig(1, "Not Plugged In Mic", "Not Plugged In Mic0", 1);

            CConfig.SConfig restored;
            _RoundTripConfig(out restored);

            Assert.AreEqual("Not Plugged In Mic", restored.Record.MicConfig[1].DeviceName);
            Assert.AreEqual(1, restored.Record.MicConfig[1].Channel);
        }

        private void _RoundTripConfig(out CConfig.SConfig restored)
        {
            string xml = new CXmlSerializer(true).Serialize(CConfig.Config);
            //Same lenient deserialization as CConfig._LoadConfig
            restored = _Deserializer.DeserializeString<CConfig.SConfig>(xml);
        }
    }
}
