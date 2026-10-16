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
using System.IO;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using Vocaluxe.Base;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Database
{
    public class CResourceDB : CDatabaseBase
    {
#if DEBUG
        private readonly string[] _FilesV1 = new string[]
            {
                "Logo_voc.png", "PerfectNoteStar.png", "redDot.png", "blueDot.png", "brunzel.png", "Darkice.png", "flokuep.png", "flamefire.png", "bohning.png", "mesand.png",
                "babene03.png", "lukeIam.png"
            };
#endif
        public CResourceDB(string filePath) : base(filePath) { }

        public override bool Init()
        {
            if (!base.Init())
                return false;
            if (_Version < 0)
            {
                CLog.Error("Can't find Ressource-DB!");
                return false;
            }

            if (_Version < CSettings.DatabaseCreditsRessourcesVersion)
            {
#if DEBUG
                return _CreateDB() && _UpdateV1();
#else
                CLog.Error("Upgrading Ressource-DB not possible");
                return false;
#endif
            }

            return true;
        }

        public bool GetCreditsRessource(string fileName, ref CTextureRef tex)
        {
            if (!_Initialized)
                return false;
            bool result = false;

            lock (_Mutex)
            {
                using (var connection = _OpenConnection(_FilePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, width, height FROM Images WHERE [Path] = @path";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@path", fileName);

                    int id = -1;
                    int w = 0, h = 0;
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            id = reader.GetInt32(0);
                            w = reader.GetInt32(1);
                            h = reader.GetInt32(2);
                        }
                    }
                    if (id >= 0)
                    {
                        //Reader is closed, so this command can be reused for the second query
                        command.CommandText = "SELECT Data FROM ImageData WHERE ImageID = @id";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@id", id);

                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                result = true;
                                byte[] data = _GetBytes(reader);
                                tex = CDraw.AddTexture(w, h, data);
                            }
                        }
                    }
                }
            }

            return result;
        }

#if DEBUG
        //Update from V0 (empty) to V1
        private bool _UpdateV1()
        {
            if (!_Initialized || _Version >= 1)
                return false;
            //Run on one short-lived connection (MDS connections are thread-affine); the whole update
            //stays in a single transaction so a failure leaves the database untouched.
            try
            {
                using (var connection = _OpenConnection(_FilePath))
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            foreach (string file in _FilesV1)
                            {
                                string filePath = Path.Combine(CSettings.ProgramFolder, file);
                                if (!_AddImageToCreditsDB(filePath, connection, transaction))
                                {
                                    transaction.Rollback();
                                    return false;
                                }
                            }
                            using (var command = connection.CreateCommand())
                            {
                                command.Transaction = transaction;
                                command.CommandText = "Update Version SET Value=@Value";
                                command.Parameters.Clear();
                                command.Parameters.AddWithValue("@Value", 1);
                                command.ExecuteNonQuery();
                            }
                            transaction.Commit();
                        }
                        catch (Exception)
                        {
                            transaction.Rollback();
                            return false;
                        }
                    }
                }
                _Version = 1;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        //If you want to add an image to db, call this method!
        private bool _AddImageToCreditsDB(string imagePath, SqliteConnection connection, SqliteTransaction transaction)
        {
            if (!File.Exists(imagePath))
                return false;

            SKBitmap origin;
            using (var codec = SKCodec.Create(imagePath))
            {
                if (codec == null)
                {
                    CLog.Error("Error loading image: " + imagePath);
                    return false;
                }
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                origin = new SKBitmap(info);
                SKCodecResult res = codec.GetPixels(info, origin.GetPixels());
                if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
                {
                    origin.Dispose();
                    CLog.Error("Error loading image: " + imagePath);
                    return false;
                }
            }
            try
            {
                int w = origin.Width;
                int h = origin.Height;
                byte[] data = origin.Bytes;

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO Images (Path, width, height) VALUES (@path, @w, @h)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@path", Path.GetFileName(imagePath));
                    command.Parameters.AddWithValue("@w", w);
                    command.Parameters.AddWithValue("@h", h);
                    command.ExecuteNonQuery();
                }

                int id = -1;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT id FROM Images WHERE [Path] = @path";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@path", Path.GetFileName(imagePath));
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                            id = reader.GetInt32(0);
                    }
                }
                if (id < 0)
                    return false;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO ImageData (ImageID, Data) VALUES (@id, @data)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@data", data);
                    command.ExecuteNonQuery();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                origin.Dispose();
            }
        }

        private bool _CreateDB()
        {
            try
            {
                using (var connection = _OpenConnection(_FilePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS Version (Value INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO Version (Value) VALUES(0)";
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE IF NOT EXISTS Images ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                          "Path TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE IF NOT EXISTS ImageData ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                          "ImageID INTEGER NOT NULL, Data BLOB NOT NULL);";
                    command.ExecuteNonQuery();

                    return true;
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error creating Ressource DB " + e);
                return false;
            }
        }
#endif
    }
}
