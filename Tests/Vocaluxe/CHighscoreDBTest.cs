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

using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System;
using System.IO;
using System.Text;
using Vocaluxe.Base;
using Vocaluxe.Lib.Database;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     The highscore database tests work on throw-away files in a temp directory and never touch the
    ///     Output/ game data. The legacy USDX fixtures reproduce the old schemas (USDX 1.10 and 1.01) so the
    ///     V1-to-V3 conversion chain and the CP1252 re-encoding are exercised through the public Init().
    /// </summary>
    [TestFixture]
    public class CHighscoreDBTest
    {
        private string _TempDir;

        [SetUp]
        public void SetUp()
        {
            _TempDir = Path.Combine(Path.GetTempPath(), "vocaluxe-dbtests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_TempDir);
        }

        [TearDown]
        public void TearDown()
        {
            //MDS pools connections by default; the pooled connections would keep the (now unneeded) file
            //handles on Windows and block the temp directory cleanup below.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_TempDir))
                Directory.Delete(_TempDir, true);
        }

        [Test]
        public void TestInit_CreatesNewV3Database()
        {
            string path = Path.Combine(_TempDir, "HighscoreDB");

            CHighscoreDB db = new CHighscoreDB(path);
            Assert.AreEqual(true, db.Init());
            db.Close();

            //A missing database is created at the current schema version...
            Assert.AreEqual(CSettings.DatabaseHighscoreVersion, _ScalarInt(path, "SELECT Value FROM Version"));
            //...with both the V2 and the V3 columns present.
            Assert.AreEqual(true, _HasColumn(path, "Songs", "DateAdded"));
            Assert.AreEqual(true, _HasColumn(path, "Scores", "ShortSong"));
        }

        [Test]
        public void TestAddScore_InsertsDeduplicatesAndCountsPlays()
        {
            string path = Path.Combine(_TempDir, "HighscoreDB");

            CHighscoreDB db = new CHighscoreDB(path);
            Assert.AreEqual(true, db.Init());

            int firstID = db.AddScore("Alice", 950, 1, 1720000000, 0, 0, 0, 1, "The Beatles", "Yesterday", 0, path);
            Assert.GreaterOrEqual(firstID, 1);

            //The very same score again resolves to the existing row instead of inserting a duplicate
            int secondID = db.AddScore("Alice", 950, 1, 1720000000, 0, 0, 0, 1, "The Beatles", "Yesterday", 0, path);
            Assert.AreEqual(firstID, secondID);

            //A different player inserts a new row...
            int thirdID = db.AddScore("Bob", 999, 2, 1720000100, 0, 0, 0, 1, "The Beatles", "Yesterday", 0, path);
            Assert.AreNotEqual(firstID, thirdID);
            Assert.AreEqual(2, _ScalarInt(path, "SELECT COUNT(*) FROM Scores"));

            //...and the song itself was registered
            Assert.AreEqual(true, db.GetDataBaseSongInfos("The Beatles", "Yesterday", out int numPlayed, out DateTime dateAdded, out int songID));
            Assert.GreaterOrEqual(songID, 1);
            Assert.AreEqual(0, numPlayed);
            Assert.Less(Math.Abs(DateTime.Now.Ticks - dateAdded.Ticks), TimeSpan.FromHours(1).Ticks);

            db.IncreaseSongCounter(songID);

            Assert.AreEqual(true, db.GetDataBaseSongInfos("The Beatles", "Yesterday", out int numPlayed2, out _, out _));
            Assert.AreEqual(1, numPlayed2);
            db.Close();
        }

        [Test]
        public void TestInit_ConvertsLegacyUSDX110Database()
        {
            string path = Path.Combine(_TempDir, "HighscoreDB");
            _CreateUsdx110Db(path);

            CHighscoreDB db = new CHighscoreDB(path);
            Assert.AreEqual(true, db.Init());
            db.Close();

            //The USDX 1.10 era database was migrated to the current schema...
            Assert.AreEqual(CSettings.DatabaseHighscoreVersion, _ScalarInt(path, "SELECT Value FROM Version"));
            //...its USDX tables are gone...
            Assert.AreEqual(0, _ScalarInt(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('US_Scores', 'US_Songs')"));
            //...and the scores and songs data made it over. The Unix timestamps became .NET ticks and the
            //V2 migration set the new ShortSong column to its default.
            Assert.AreEqual(1, _ScalarInt(path, "SELECT COUNT(*) FROM Scores"));
            Assert.AreEqual("John", _ScalarString(path, "SELECT PlayerName FROM Scores WHERE [id] = 1"));
            Assert.AreEqual(950, _ScalarInt(path, "SELECT Score FROM Scores WHERE [id] = 1"));
            Assert.AreEqual(0, _ScalarInt(path, "SELECT ShortSong FROM Scores WHERE [id] = 1"));
            Assert.AreEqual(_UnixToTicks(1700000000), _ScalarLong(path, "SELECT Date FROM Scores WHERE [id] = 1"));
            Assert.AreEqual("Miles Davis", _ScalarString(path, "SELECT Artist FROM Songs WHERE [ID] = 1"));
            Assert.AreEqual("So What", _ScalarString(path, "SELECT Title FROM Songs WHERE [ID] = 1"));
            //TimesPlayed was carried over and the V3 migration counted the imported score once
            Assert.AreEqual(6, _ScalarInt(path, "SELECT NumPlayed FROM Songs WHERE [ID] = 1"));
            Assert.Greater(_ScalarLong(path, "SELECT DateAdded FROM Songs WHERE [ID] = 1"), 0L);
        }

        [Test]
        public void TestInit_ConvertsLegacyUSDX101Database_AndFixesCp1252Text()
        {
            _Cp1252();

            string path = Path.Combine(_TempDir, "HighscoreDB");
            _CreateUsdx101Db(path);

            CHighscoreDB db = new CHighscoreDB(path);
            Assert.AreEqual(true, db.Init());
            db.Close();

            Assert.AreEqual(CSettings.DatabaseHighscoreVersion, _ScalarInt(path, "SELECT Value FROM Version"));
            //The CP1252 byte garbage of the USDX 1.01/CMD era must now come out as proper UTF-8 strings...
            Assert.AreEqual("M\u00FCller, J.", _ScalarString(path, "SELECT Artist FROM Songs WHERE [ID] = 1"));
            Assert.AreEqual("Caf\u00E9 Song", _ScalarString(path, "SELECT Title FROM Songs WHERE [ID] = 1"));
            Assert.AreEqual("J\u00FCrgen", _ScalarString(path, "SELECT PlayerName FROM Scores WHERE [id] = 1"));
            //...while the rest of the legacy data made it over (the 1.01 scores table has no Date column)
            Assert.AreEqual(0, _ScalarInt(path, "SELECT ShortSong FROM Scores WHERE [id] = 1"));
            Assert.AreEqual(0L, _ScalarLong(path, "SELECT Date FROM Scores WHERE [id] = 1"));
            Assert.AreEqual(6, _ScalarInt(path, "SELECT NumPlayed FROM Songs WHERE [ID] = 1"));
            Assert.AreEqual(0, _ScalarInt(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('US_Scores', 'US_Songs')"));
        }

        private static void _CreateUsdx110Db(string path)
        {
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE US_Songs (ID INTEGER PRIMARY KEY, Artist TEXT, Title TEXT, TimesPlayed INTEGER);";
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE US_Scores (SongID INTEGER, Player TEXT, Score INTEGER, Date INTEGER, Difficulty INTEGER);";
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO US_Songs (ID, Artist, Title, TimesPlayed) VALUES (1, 'Miles Davis', 'So What', 5);";
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO US_Scores (SongID, Player, Score, Date, Difficulty) VALUES (1, 'John', 950, 1700000000, 2);";
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();

                    command.CommandText = "PRAGMA user_version = 1;";
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void _CreateUsdx101Db(string path)
        {
            Encoding cp1252 = _Cp1252();

            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    //The 1.01/CMD era stored its text as raw CP1252 bytes and had no Date column in the
                    //scores table; reproduce both so the conversion's re-encoding branch is exercised.
                    command.CommandText = "CREATE TABLE US_Songs (ID INTEGER PRIMARY KEY, Artist BLOB, Title BLOB, TimesPlayed INTEGER);";
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE US_Scores (SongID INTEGER, Player BLOB, Score INTEGER, Difficulty INTEGER);";
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO US_Songs (ID, Artist, Title, TimesPlayed) VALUES (1, @artist, @title, 5);";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@artist", cp1252.GetBytes("M\u00FCller, J."));
                    command.Parameters.AddWithValue("@title", cp1252.GetBytes("Caf\u00E9 Song"));
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO US_Scores (SongID, Player, Score, Difficulty) VALUES (1, @player, 1250, 2);";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@player", cp1252.GetBytes("J\u00FCrgen"));
                    command.ExecuteNonQuery();
                }
            }
        }

        // .NET does not register legacy code pages by default; make CP1252 available the same way the
        // production code does (CHighscoreDB registers the provider on first type use).
        private static Encoding _Cp1252()
        {
            try
            {
                return Encoding.GetEncoding(1252);
            }
            catch (NotSupportedException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252);
            }
        }

        private static int _ScalarInt(string dbPath, string sql)
        {
            using (var connection = new SqliteConnection("Data Source=" + dbPath))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static long _ScalarLong(string dbPath, string sql)
        {
            using (var connection = new SqliteConnection("Data Source=" + dbPath))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static string _ScalarString(string dbPath, string sql)
        {
            using (var connection = new SqliteConnection("Data Source=" + dbPath))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return Convert.ToString(command.ExecuteScalar());
                }
            }
        }

        private static bool _HasColumn(string dbPath, string table, string column)
        {
            return _ScalarInt(dbPath, "SELECT COUNT(*) FROM pragma_table_info('" + table + "') WHERE name = '" + column + "'") > 0;
        }

        private static long _UnixToTicks(int unixTime)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, 0).AddSeconds(unixTime).Ticks;
        }
    }
}