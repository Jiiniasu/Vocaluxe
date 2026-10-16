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

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Text;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Songs;

namespace Vocaluxe.Lib.Database
{
    public class CHighscoreDB : CDatabaseBase
    {
        private struct SData
        {
            public int Id;
            public long Ticks;
            public string Str1;
            public string Str2;
        }

        static CHighscoreDB()
        {
            //The USDX 1.01/CMD import below re-encodes CP1252 text. On .NET (Core) that code page
            //is not registered by default: the provider ships with the framework on net10.0, but it
            //has to be registered once before Encoding.GetEncoding(1252) works.
            try
            {
                Encoding.GetEncoding(1252);
            }
            catch (NotSupportedException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
        }

        public CHighscoreDB(string filePath) : base(filePath) { }

        public override bool Init()
        {
            string oldDBFilePath = Path.Combine(CSettings.DataFolder, CSettings.FileNameOldHighscoreDB);
            if (File.Exists(oldDBFilePath))
            {
                if (File.Exists(_FilePath))
                {
                    if (!_CreateOrConvert(oldDBFilePath))
                    {
                        CLog.Fatal("Cannot init Highscore DB: Error opening old database: {DBFile}" + CLog.Params(oldDBFilePath));
                        return false;
                    }
                    if (!_CreateOrConvert(_FilePath))
                    {
                        CLog.Fatal("Cannot init Highscore DB: Error opening database: {DBFile}", CLog.Params(_FilePath));
                        return false;
                    }
                    if (!_ImportData(oldDBFilePath))
                    {
                        CLog.Fatal("Cannot init Highscore DB: Error importing data");
                        return false;
                    }
                }
                else
                {
                    File.Copy(oldDBFilePath, _FilePath);
                    if (!_CreateOrConvert(_FilePath))
                    {
                        CLog.Fatal("Cannot init Highscore DB: Error opening database: {DBFile}" + CLog.Params(_FilePath));
                        return false;
                    }
                }
                File.Delete(oldDBFilePath);
            }
            else if (!_CreateOrConvert(_FilePath))
            {
                CLog.Fatal("Cannot init Highscore DB: Error opening database: {DBFile}", CLog.Params(_FilePath));
                return false;
            }
            return true;
        }

        public override void Close()
        {
            //Do nothing
        }

        public bool GetDataBaseSongInfos(string artist, string title, out int numPlayed, out DateTime dateAdded, out int highscoreID)
        {
            string sArtist;
            string sTitle;
            int songID;
            using (var connection = _OpenConnection(_FilePath))
            using (var command = connection.CreateCommand())
            {
                songID = _GetDataBaseSongID(artist, title, 0, command);
                highscoreID = songID;
            }
            return _GetDataBaseSongInfos(songID, out sArtist, out sTitle, out numPlayed, out dateAdded, _FilePath);
        }

        public void IncreaseSongCounter(int dataBaseSongID)
        {
            using (var connection = _OpenConnection(_FilePath))
            using (var command = connection.CreateCommand())
                _IncreaseSongCounter(dataBaseSongID, command);
        }

        public int AddScore(string playerName, int score, int lineNr, long date, int medley, int duet, int shortSong, int difficulty,
                            string artist, string title, int numPlayed, string filePath)
        {
            SqliteConnection connection;

            try
            {
                connection = _OpenConnection(filePath);
            }
            catch (Exception)
            {
                return -1;
            }

            using (connection)
            using (var command = connection.CreateCommand())
            {
                int dataBaseSongID = _GetDataBaseSongID(artist, title, numPlayed, command);
                return _AddScore(playerName, score, lineNr, date, medley, duet, shortSong, difficulty, dataBaseSongID, command);
            }
        }

        public int AddScore(SPlayer player)
        {
            SqliteConnection connection;

            try
            {
                connection = _OpenConnection(_FilePath);
            }
            catch (Exception)
            {
                return -1;
            }

            using (connection)
            {
                int medley = 0;
                int duet = 0;
                int shortSong = 0;
                switch (player.GameMode)
                {
                    case EGameMode.TR_GAMEMODE_MEDLEY:
                        medley = 1;
                        break;
                    case EGameMode.TR_GAMEMODE_DUET:
                        duet = 1;
                        break;
                    case EGameMode.TR_GAMEMODE_SHORTSONG:
                        shortSong = 1;
                        break;
                }

                using (var command = connection.CreateCommand())
                {
                    int dataBaseSongID = CSongs.GetSong(player.SongID).DataBaseSongID;
                    return _AddScore(CProfiles.GetPlayerName(player.ProfileID), (int)Math.Round(player.Points), player.VoiceNr, player.DateTicks, medley,
                                     duet, shortSong, (int)player.Difficulty, dataBaseSongID, command);
                }
            }
        }

        private int _AddScore(string playerName, int score, int lineNr, long date, int medley, int duet, int shortSong, int difficulty,
                              int dataBaseSongID, SqliteCommand command)
        {
            int lastInsertID = -1;

            if (dataBaseSongID >= 0)
            {
                command.CommandText = "SELECT id FROM Scores WHERE SongID = @SongID AND PlayerName = @PlayerName AND Score = @Score AND " +
                                      "LineNr = @LineNr AND Date = @Date AND Medley = @Medley AND Duet = @Duet AND ShortSong = @ShortSong AND Difficulty = @Difficulty";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@SongID", dataBaseSongID);
                command.Parameters.AddWithValue("@PlayerName", playerName);
                command.Parameters.AddWithValue("@Score", score);
                command.Parameters.AddWithValue("@LineNr", lineNr);
                command.Parameters.AddWithValue("@Date", date);
                command.Parameters.AddWithValue("@Medley", medley);
                command.Parameters.AddWithValue("@Duet", duet);
                command.Parameters.AddWithValue("@ShortSong", shortSong);
                command.Parameters.AddWithValue("@Difficulty", difficulty);

                SqliteDataReader reader = null;
                try
                {
                    reader = command.ExecuteReader();
                }
                catch (Exception) { }

                if (reader != null && reader.Read())
                    return reader.GetInt32(0);

                if (reader != null)
                    reader.Dispose();

                command.CommandText = "INSERT INTO Scores (SongID, PlayerName, Score, LineNr, Date, Medley, Duet, ShortSong, Difficulty) " +
                                      "VALUES (@SongID, @PlayerName, @Score, @LineNr, @Date, @Medley, @Duet, @ShortSong, @Difficulty)";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@SongID", dataBaseSongID);
                command.Parameters.AddWithValue("@PlayerName", playerName);
                command.Parameters.AddWithValue("@Score", score);
                command.Parameters.AddWithValue("@LineNr", lineNr);
                command.Parameters.AddWithValue("@Date", date);
                command.Parameters.AddWithValue("@Medley", medley);
                command.Parameters.AddWithValue("@Duet", duet);
                command.Parameters.AddWithValue("@ShortSong", shortSong);
                command.Parameters.AddWithValue("@Difficulty", difficulty);
                command.ExecuteNonQuery();

                //Read last insert line
                command.CommandText = "SELECT id FROM Scores ORDER BY id DESC LIMIT 0, 1";
                command.Parameters.Clear();

                SqliteDataReader lastReader = command.ExecuteReader();
                if (lastReader != null)
                {
                    while (lastReader.Read())
                        lastInsertID = lastReader.GetInt32(0);
                    lastReader.Dispose();
                }
            }

            return lastInsertID;
        }

        public List<SDBScoreEntry> LoadScore(int songID, EGameMode gameMode, EHighscoreStyle style)
        {
            var scores = new List<SDBScoreEntry>();
            SqliteConnection connection;

            try
            {
                connection = _OpenConnection(_FilePath);
            }
            catch (Exception)
            {
                return scores;
            }

            using (connection)
            using (var command = connection.CreateCommand())
            {
                int medley = 0;
                int duet = 0;
                int shortSong = 0;
                switch (gameMode)
                {
                    case EGameMode.TR_GAMEMODE_MEDLEY:
                        medley = 1;
                        break;
                    case EGameMode.TR_GAMEMODE_DUET:
                        duet = 1;
                        break;
                    case EGameMode.TR_GAMEMODE_SHORTSONG:
                        shortSong = 1;
                        break;
                }

                int dataBaseSongID = _GetDataBaseSongID(songID, command);
                if (dataBaseSongID < 0)
                    return scores;

                switch (style)
                {
                    case EHighscoreStyle.TR_CONFIG_HIGHSCORE_LIST_BEST:
                        command.CommandText = "SELECT os.PlayerName, os.Score, os.Date, os.Difficulty, os.LineNr, os.id " +
                        "FROM Scores os " +
                        "INNER JOIN ( " +
                        "SELECT sc.PlayerName, sc.Score, sc.Difficulty, sc.LineNr, MIN(sc.Date) AS Date " +
                        "FROM Scores sc " +
                        "INNER JOIN ( " +
                        "SELECT Playername, MAX(Score) AS Score, Difficulty, LineNr " +
                        "FROM Scores " +
                        "WHERE [SongID] = @SongID AND [Medley] = @Medley AND [Duet] = @Duet AND [ShortSong] = @ShortSong " +
                        "GROUP BY PlayerName, Difficulty, LineNr " +
                        ") AS mc " +
                        "ON sc.PlayerName = mc.PlayerName AND sc.Difficulty = mc.Difficulty AND sc.LineNr = mc.LineNr AND sc.Score = mc.Score " +
                        "WHERE [SongID] = @SongID AND [Medley] = @Medley AND [Duet] = @Duet AND [ShortSong] = @ShortSong " +
                        "GROUP BY sc.PlayerName, sc.Difficulty, sc.LineNr, sc.Score " +
                        ") AS iq " +
                        "ON os.PlayerName = iq.PlayerName AND os.Difficulty = iq.Difficulty AND os.LineNr = iq.LineNr AND os.Score = iq.Score AND os.Date = iq.Date " +
                        "WHERE [SongID] = @SongID AND [Medley] = @Medley AND [Duet] = @Duet AND [ShortSong] = @ShortSong " +
                        "GROUP BY os.PlayerName, os.Difficulty, os.LineNr, os.Score " +
                        "ORDER BY os.Score DESC, os.Date ASC";
                        break;
                    case EHighscoreStyle.TR_CONFIG_HIGHSCORE_LIST_ALL:
                        command.CommandText = "SELECT PlayerName, Score, Date, Difficulty, LineNr, id " +
                        "FROM Scores " +
                        "WHERE [SongID] = @SongID AND [Medley] = @Medley AND [Duet] = @Duet AND [ShortSong] = @ShortSong " +
                        "ORDER BY [Score] DESC, [Date] ASC";
                        break;
                }

                command.Parameters.Clear();
                command.Parameters.AddWithValue("@SongID", dataBaseSongID);
                command.Parameters.AddWithValue("@Medley", medley);
                command.Parameters.AddWithValue("@Duet", duet);
                command.Parameters.AddWithValue("@ShortSong", shortSong);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var score = new SDBScoreEntry
                        {
                            Name = reader.GetString(0),
                            Score = reader.GetInt32(1),
                            Date = new DateTime(reader.GetInt64(2)),
                            Difficulty = (EGameDifficulty)reader.GetInt32(3),
                            VoiceNr = reader.GetInt32(4),
                            ID = reader.GetInt32(5)
                        };

                        scores.Add(score);
                    }
                }
            }
            return scores;
        }

        private static void _IncreaseSongCounter(int dataBaseSongID, SqliteCommand command)
        {
            command.CommandText = "UPDATE Songs SET NumPlayed = NumPlayed + 1 WHERE [id] = @id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@id", dataBaseSongID);
            command.ExecuteNonQuery();
        }

        private int _GetDataBaseSongID(int songID, SqliteCommand command)
        {
            CSong song = CSongs.GetSong(songID);

            if (song == null)
                return -1;

            return _GetDataBaseSongID(song.Artist, song.Title, 0, command);
        }

        private int _GetDataBaseSongID(string artist, string title, int defNumPlayed, SqliteCommand command)
        {
            command.CommandText = "SELECT id FROM Songs WHERE [Title] = @title AND [Artist] = @artist";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@artist", artist);

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                    return reader.GetInt32(0);
            }

            command.CommandText = "INSERT INTO Songs (Title, Artist, NumPlayed, DateAdded) " +
                                  "VALUES (@title, @artist, @numplayed, @dateadded)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@artist", artist);
            command.Parameters.AddWithValue("@numplayed", defNumPlayed);
            command.Parameters.AddWithValue("@dateadded", DateTime.Now.Ticks);
            command.ExecuteNonQuery();

            command.CommandText = "SELECT id FROM Songs WHERE [Title] = @title AND [Artist] = @artist";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@artist", artist);

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                    return reader.GetInt32(0);
            }

            return -1;
        }

        private bool _GetDataBaseSongInfos(int songID, out string artist, out string title, out int numPlayed, out DateTime dateAdded, string filePath)
        {
            artist = String.Empty;
            title = String.Empty;
            numPlayed = 0;
            dateAdded = DateTime.Today;

            SqliteConnection connection;

            try
            {
                connection = _OpenConnection(filePath);
            }
            catch (Exception)
            {
                return false;
            }

            using (connection)
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Artist, Title, NumPlayed, DateAdded FROM Songs WHERE [id] = @id";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@id", songID);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    if (!reader.IsDBNull(0))
                        artist = reader.GetString(0);

                    if (!reader.IsDBNull(1))
                        title = reader.GetString(1);

                    if (!reader.IsDBNull(2))
                        numPlayed = reader.GetInt32(2);

                    if (!reader.IsDBNull(3))
                        dateAdded = new DateTime(reader.GetInt64(3));

                    return true;
                }
            }
        }

        private static void _CreateHighscoreDB(string filePath)
        {
            using (var connection = _OpenConnection(filePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS Version ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Value INTEGER NOT NULL);";
                command.ExecuteNonQuery();

                command.CommandText = "INSERT INTO Version (id, Value) VALUES(NULL, @Value)";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@Value", CSettings.DatabaseHighscoreVersion);
                command.ExecuteNonQuery();

                command.CommandText = "CREATE TABLE IF NOT EXISTS Songs ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                      "Artist TEXT NOT NULL, Title TEXT NOT NULL, NumPlayed INTEGER, DateAdded BIGINT);";
                command.ExecuteNonQuery();

                command.CommandText = "CREATE TABLE IF NOT EXISTS Scores ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                      "SongID INTEGER NOT NULL, PlayerName TEXT NOT NULL, Score INTEGER NOT NULL, LineNr INTEGER NOT NULL, Date BIGINT NOT NULL, " +
                                      "Medley INTEGER NOT NULL, Duet INTEGER NOT NULL, ShortSong INTEGER NOT NULL, Difficulty INTEGER NOT NULL);";
                command.ExecuteNonQuery();
            }
        }

        private static void _CreateHighscoreDBV1(string filePath)
        {
            using (var connection = _OpenConnection(filePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS Version ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Value INTEGER NOT NULL);";
                command.ExecuteNonQuery();

                command.CommandText = "INSERT INTO Version (id, Value) VALUES(NULL, 1 )";
                command.ExecuteNonQuery();

                command.CommandText = "CREATE TABLE IF NOT EXISTS Songs ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                      "Artist TEXT NOT NULL, Title TEXT NOT NULL, NumPlayed INTEGER);";
                command.ExecuteNonQuery();

                command.CommandText = "CREATE TABLE IF NOT EXISTS Scores ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                      "SongID INTEGER NOT NULL, PlayerName TEXT NOT NULL, Score INTEGER NOT NULL, LineNr INTEGER NOT NULL, Date BIGINT NOT NULL, " +
                                      "Medley INTEGER NOT NULL, Duet INTEGER NOT NULL, Difficulty INTEGER NOT NULL);";
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        ///     Creates a new Vocaluxe Database if no file exists. Converts an existing old Ultrastar Deluxe highscore database into vocaluxe format.
        /// </summary>
        /// <param name="filePath">Database file path</param>
        /// <returns></returns>
        private static bool _CreateOrConvert(string filePath)
        {
            bool result = true;
            SqliteConnection connection;

            try
            {
                connection = _OpenConnection(filePath);
            }
            catch (Exception)
            {
                return false;
            }

            using (connection)
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version";
                command.Parameters.Clear();

                int version;
                using (var versionReader = command.ExecuteReader())
                {
                    versionReader.Read();
                    version = versionReader.GetInt32(0);
                }

                //Check if old scores table exists
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='US_Scores';";
                command.Parameters.Clear();

                bool scoresTableExists;
                using (var tableReader = command.ExecuteReader())
                {
                    scoresTableExists = tableReader.Read();
                }

                command.CommandText = "SELECT Value FROM Version";
                command.Parameters.Clear();

                bool hasVersionRow = false;
                int currentVersion = 0;

                SqliteDataReader reader = null;

                try
                {
                    reader = command.ExecuteReader();
                }
                catch (Exception) { }

                if (reader != null)
                {
                    using (reader)
                    {
                        if (reader.FieldCount > 0 && reader.Read())
                        {
                            hasVersionRow = true;
                            currentVersion = reader.GetInt32(0);
                        }
                    }
                }

                if (!hasVersionRow)
                {
                    // create new database/tables
                    if (version == 1)
                    {
                        //Check for USDX 1.1 DB
                        _CreateHighscoreDBV1(filePath);
                        result &= _ConvertFrom110(filePath);
                        result &= _UpdateDatabase(1, connection);
                        result &= _UpdateDatabase(2, connection);
                    }
                    else if (version == 0 && scoresTableExists)
                    {
                        //Check for USDX 1.01 or CMD Mod DB
                        _CreateHighscoreDBV1(filePath);
                        result &= _ConvertFrom101(filePath);
                        result &= _UpdateDatabase(1, connection);
                        result &= _UpdateDatabase(2, connection);
                    }
                    else
                        _CreateHighscoreDB(filePath);
                }
                else if (currentVersion < CSettings.DatabaseHighscoreVersion)
                {
                    // update database
                    result &= _UpdateDatabase(currentVersion, connection);
                }
            }

            return result;
        }

        /// <summary>
        ///     Converts a USDX 1.1 database into the Vocaluxe format
        /// </summary>
        /// <param name="filePath">Database file path</param>
        /// <returns>True if succeeded</returns>
        private static bool _ConvertFrom110(string filePath)
        {
            SqliteConnection connection;

            try
            {
                connection = _OpenConnection(filePath);
            }
            catch (Exception)
            {
                return false;
            }

            using (connection)
            using (var command = connection.CreateCommand())
            {
                //The USDX database has no column for LineNr, Medley and Duet so just fill 0 in there
                command.CommandText =
                    "INSERT INTO Scores (SongID, PlayerName, Score, LineNr, Date, Medley, Duet, Difficulty) SELECT SongID, Player, Score, '0', Date, '0', '0', Difficulty from US_Scores";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "INSERT INTO Songs SELECT ID, Artist, Title, TimesPlayed from US_Songs";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                var scores = new List<SData>();
                var songs = new List<SData>();

                command.CommandText = "SELECT id, PlayerName, Date FROM Scores";
                command.Parameters.Clear();

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var data = new SData { Id = reader.GetInt32(0), Str1 = reader.GetString(1) };
                        Int64 ticks = 0;

                        try
                        {
                            ticks = reader.GetInt64(2);
                        }
                        catch { }

                        data.Ticks = _UnixTimeToTicks((int)ticks);

                        scores.Add(data);
                    }
                }

                command.CommandText = "SELECT id, Artist, Title FROM Songs";
                command.Parameters.Clear();

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var data = new SData { Id = reader.GetInt32(0), Str1 = reader.GetString(1), Str2 = reader.GetString(2) };
                        songs.Add(data);
                    }
                }

                using (var transaction = connection.BeginTransaction())
                {
                    // update Title and Artist strings
                    foreach (SData data in songs)
                    {
                        command.CommandText = "UPDATE Songs SET [Artist] = @artist, [Title] = @title WHERE [ID] = @id";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@title", data.Str2);
                        command.Parameters.AddWithValue("@artist", data.Str1);
                        command.Parameters.AddWithValue("@id", data.Id);
                        command.Transaction = transaction;
                        command.ExecuteNonQuery();
                    }

                    // update player names
                    foreach (SData data in scores)
                    {
                        command.CommandText = "UPDATE Scores SET [PlayerName] = @player, [Date] = @date WHERE [id] = @id";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@player", data.Str1);
                        command.Parameters.AddWithValue("@date", data.Ticks);
                        command.Parameters.AddWithValue("@id", data.Id);
                        command.Transaction = transaction;
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }

                //Delete old tables after conversion
                command.Transaction = null;
                command.CommandText = "DROP TABLE IF EXISTS us_scores;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "DROP TABLE IF EXISTS us_songs;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "DROP TABLE IF EXISTS us_statistics_info;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "DROP TABLE IF EXISTS us_users_info;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "DROP TABLE IF EXISTS us_webs;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "DROP TABLE IF EXISTS us_webs_stats;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                //This versioning is not used in Vocaluxe so reset it to 0
                command.CommandText = "PRAGMA user_version = 0";
                command.Parameters.Clear();
                command.ExecuteNonQuery();
            }

            return true;
        }

        /// <summary>
        ///     Converts a USDX 1.01 or CMD 1.01 database to Vocaluxe format
        /// </summary>
        /// <param name="filePath">Database file path</param>
        /// <returns>True if succeeded</returns>
        private static bool _ConvertFrom101(string filePath)
        {
            SqliteConnection connection;

            try
            {
                connection = _OpenConnection(filePath);
            }
            catch (Exception)
            {
                return false;
            }

            using (connection)
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(US_Scores);";
                command.Parameters.Clear();
                bool dateExists = false;
                using (var reader = command.ExecuteReader())
                {
                    //Check for column Date
                    while (reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (reader.GetName(i) == "name")
                            {
                                if (reader.GetString(i) == "Date")
                                    dateExists = true;
                                break;
                            }
                        }
                    }
                }

                //This is a USDX 1.01 DB
                command.CommandText = !dateExists
                                          ? "INSERT INTO Scores (SongID, PlayerName, Score, LineNr, Date, Medley, Duet, Difficulty) SELECT SongID, Player, Score, '0', '0', '0', '0', Difficulty from US_Scores"
                                          : "INSERT INTO Scores (SongID, PlayerName, Score, LineNr, Date, Medley, Duet, Difficulty) SELECT SongID, Player, Score, '0', Date, '0', '0', Difficulty from US_Scores";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "INSERT INTO Songs SELECT ID, Artist, Title, TimesPlayed from US_Songs";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                // convert from CP1252 to UTF8: the USDX 1.01/CMD databases store their text as CP1252 bytes,
                // which a UTF8-based reader mangles. Read the raw stored bytes (CAST ... AS BLOB keeps them as
                // stored, no transcoding) and re-encode them as UTF8.
                var scores = new List<SData>();
                var songs = new List<SData>();

                command.CommandText = "SELECT id, CAST(Artist AS BLOB), CAST(Title AS BLOB) FROM Songs";
                command.Parameters.Clear();
                Encoding utf8 = Encoding.UTF8;
                Encoding cp1252 = Encoding.GetEncoding(1252);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var data = new SData { Id = reader.GetInt32(0) };

                        byte[] bytes = _GetBytes(reader, 1);
                        data.Str1 = bytes != null ? utf8.GetString(Encoding.Convert(cp1252, utf8, bytes)) : "Someone";

                        bytes = _GetBytes(reader, 2);
                        data.Str2 = bytes != null ? utf8.GetString(Encoding.Convert(cp1252, utf8, bytes)) : "Someone";

                        songs.Add(data);
                    }
                }

                // ReSharper disable ConvertIfStatementToConditionalTernaryExpression
                if (!dateExists)
                {
                    command.CommandText = "SELECT id, CAST(PlayerName AS BLOB) FROM Scores";
                }
                else
                {
                    command.CommandText = "SELECT id, CAST(PlayerName AS BLOB), Date FROM Scores";
                }
                // ReSharper restore ConvertIfStatementToConditionalTernaryExpression
                command.Parameters.Clear();

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var data = new SData { Id = reader.GetInt32(0) };

                        byte[] bytes = _GetBytes(reader, 1);
                        data.Str1 = bytes != null ? utf8.GetString(Encoding.Convert(cp1252, utf8, bytes)) : "Someone";

                        if (dateExists)
                            data.Ticks = _UnixTimeToTicks(reader.GetInt32(2));

                        scores.Add(data);
                    }
                }

                using (var transaction = connection.BeginTransaction())
                {
                    // update Title and Artist strings
                    foreach (SData data in songs)
                    {
                        command.CommandText = "UPDATE Songs SET [Artist] = @artist, [Title] = @title WHERE [ID] = @id";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@title", data.Str2);
                        command.Parameters.AddWithValue("@artist", data.Str1);
                        command.Parameters.AddWithValue("@id", data.Id);
                        command.Transaction = transaction;
                        command.ExecuteNonQuery();
                    }

                    // update player names
                    foreach (SData data in scores)
                    {
                        command.CommandText = !dateExists
                                                  ? "UPDATE Scores SET [PlayerName] = @player WHERE [id] = @id"
                                                  : "UPDATE Scores SET [PlayerName] = @player, [Date] = @date WHERE [id] = @id";
                        command.Parameters.Clear();
                        if (dateExists)
                            command.Parameters.AddWithValue("@date", data.Ticks);
                        command.Parameters.AddWithValue("@player", data.Str1);
                        command.Parameters.AddWithValue("@id", data.Id);
                        command.Transaction = transaction;
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }

                //Delete old tables after conversion
                command.Transaction = null;
                command.CommandText = "DROP TABLE US_Scores;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();

                command.CommandText = "DROP TABLE US_Songs;";
                command.Parameters.Clear();
                command.ExecuteNonQuery();
            }

            return true;
        }

        private static bool _UpdateDatabase(int currentVersion, SqliteConnection connection)
        {
            bool updated = true;

            if (currentVersion < 2)
                updated &= _ConvertV1toV2(connection);
            else if (currentVersion < 3)
                updated &= _ConvertV2toV3(connection);

            return updated;
        }

        private static bool _ConvertV1toV2(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE Scores ADD ShortSong INTEGER";
                command.Parameters.Clear();
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE Scores SET [ShortSong] = @ShortSong";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@ShortSong", 0);
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE Version SET [Value] = @version";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@version", 2);
                command.ExecuteNonQuery();
            }

            return true;
        }

        private static bool _ConvertV2toV3(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE Songs ADD DateAdded BIGINT";
                command.Parameters.Clear();

                command.ExecuteNonQuery();
                command.CommandText = "UPDATE Songs SET [DateAdded] = @DateAdded";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@DateAdded", DateTime.Now.Ticks);
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE Version SET [Value] = @version";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@version", 3);
                command.ExecuteNonQuery();

                //Read NumPlayed from Scores and save to Songs
                command.CommandText = "SELECT SongID, Date FROM Scores ORDER BY Date ASC";
                command.Parameters.Clear();

                long lastDateAdded = -1;
                int lastID = -1;
                DateTime dt = new DateTime(1, 1, 1, 0, 0, 5);
                long sec = dt.Ticks;
                List<int> ids = new List<int>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int id = reader.GetInt32(0);
                        long dateAdded = reader.GetInt64(1);
                        if (id != lastID || dateAdded > lastDateAdded + sec)
                        {
                            ids.Add(id);
                            lastID = id;
                            lastDateAdded = dateAdded;
                        }
                    }
                }

                foreach (int id in ids)
                    _IncreaseSongCounter(id, command);
            }

            return true;
        }

        private bool _ImportData(string sourceDBPath)
        {
            SqliteConnection connSource;

            try
            {
                connSource = _OpenConnection(sourceDBPath);
            }
            catch (Exception e)
            {
                CLog.Error("Error on import high score data. Can't open source database \"" + sourceDBPath + "\" (" + e.Message + ")");
                return false;
            }

            using (connSource)
            using (var cmdSource = connSource.CreateCommand())
            {
                cmdSource.CommandText = "SELECT SongID, PlayerName, Score, LineNr, Date, Medley, Duet, ShortSong, Difficulty FROM Scores";
                cmdSource.Parameters.Clear();
                using (var source = cmdSource.ExecuteReader())
                {
                    if (source.FieldCount == 0)
                        return true;

                    while (source.Read())
                    {
                        int songid = source.GetInt32(0);
                        string player = source.GetString(1);
                        int score = source.GetInt32(2);
                        int linenr = source.GetInt32(3);
                        long date = source.GetInt64(4);
                        int medley = source.GetInt32(5);
                        int duet = source.GetInt32(6);
                        int shortsong = source.GetInt32(7);
                        int diff = source.GetInt32(8);

                        string artist, title;
                        DateTime dateadded;
                        int numplayed;
                        if (_GetDataBaseSongInfos(songid, out artist, out title, out numplayed, out dateadded, sourceDBPath))
                            AddScore(player, score, linenr, date, medley, duet, shortsong, diff, artist, title, numplayed, _FilePath);
                    }
                }
            }

            return true;
        }
    }
}
