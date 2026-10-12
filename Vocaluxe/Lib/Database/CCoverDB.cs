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
using System.Data;
using System.Drawing;
using System.IO;
using SkiaSharp;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
#if WIN
using System.Data.SQLite;

#else
using Mono.Data.Sqlite;
using SQLiteCommand = Mono.Data.Sqlite.SqliteCommand;
using SQLiteDataReader = Mono.Data.Sqlite.SqliteDataReader;
using SQLiteTransaction = Mono.Data.Sqlite.SqliteTransaction;
#endif

namespace Vocaluxe.Lib.Database
{
    public class CCoverDB : CDatabaseBase
    {
        private SQLiteTransaction _TransactionCover;

        public CCoverDB(string filePath) : base(filePath) { }

        public override bool Init()
        {
            lock (_Mutex)
            {
                if (!base.Init())
                    return false;

                if (_Version < 0)
                    return _CreateCoverDB();
                if (_Version < CSettings.DatabaseCoverVersion)
                    throw new NotImplementedException("Upgrading of cover DB not implemented");
            }
            return true;
        }

        public override void Close()
        {
            //Do commit and close atomicly otherwhise we may loose changes
            lock (_Mutex)
            {
                _CommitCovers();

                base.Close();
            }
        }

        public bool GetCover(string coverPath, ref CTextureRef tex, int maxSize)
        {
            if (_Connection == null)
                return false;
            if (!File.Exists(coverPath))
            {
                CLog.Error("Can't find File: " + coverPath);
                return false;
            }

            lock (_Mutex)
            {
                //Double check here because we may have just closed our connection
                if (_Connection == null)
                    return false;
                using (var command = new SQLiteCommand(_Connection))
                {
                    command.CommandText = "SELECT id, width, height FROM Cover WHERE [Path] = @path";
                    command.Parameters.Add("@path", DbType.String).Value = coverPath;

                    SQLiteDataReader reader = command.ExecuteReader();

                    if (reader != null && reader.HasRows)
                    {
                        reader.Read();
                        int id = reader.GetInt32(0);
                        int w = reader.GetInt32(1);
                        int h = reader.GetInt32(2);
                        reader.Close();

                        command.CommandText = "SELECT Data FROM CoverData WHERE CoverID = @id";
                        command.Parameters.Add("@id", DbType.Int32).Value = id;
                        reader = command.ExecuteReader();

                        if (reader.HasRows)
                        {
                            reader.Read();
                            byte[] data2 = _GetBytes(reader);
                            reader.Dispose();
                            tex = CDraw.EnqueueTexture(w, h, data2);
                            return true;
                        }
                        command.CommandText = "DELETE FROM Cover WHERE id = @id";
                        command.Parameters.Add("@id", DbType.Int32).Value = id;
                        command.ExecuteNonQuery();
                    }
                    if (reader != null)
                        reader.Close();
                }
            }

            // At this point we do not have a mathing entry in the CoverDB (either no Data found and deleted or nothing at all)
            // We break out of the lock to do the bitmap loading and resizing here to allow multithreaded loading

            // Cross-platform image decode via SkiaSharp (the former GDI+ Bitmap.LockBits path is Windows-only).
            SKBitmap origin;
            using (var codec = SKCodec.Create(coverPath))
            {
                if (codec == null)
                    return false;
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                origin = new SKBitmap(info);
                SKCodecResult res = codec.GetPixels(info, origin.GetPixels());
                if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
                {
                    origin.Dispose();
                    return false;
                }
            }

            Size size = new Size(origin.Width, origin.Height);
            byte[] data;
            if (size.Width > maxSize || size.Height > maxSize)
            {
                size = CHelper.FitInBounds(new SRectF(0, 0, maxSize, maxSize, 0), (float)size.Width / size.Height, EAspect.LetterBox).SizeI;
                using (var scaled = origin.Resize(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul), SKSamplingOptions.Default))
                {
                    if (scaled == null)
                    {
                        origin.Dispose();
                        return false;
                    }
                    data = scaled.Bytes;
                }
            }
            else
                data = origin.Bytes;
            origin.Dispose();

            tex = CDraw.EnqueueTexture(size.Width, size.Height, data);

            lock (_Mutex)
            {
                //Double check here because we may have just closed our connection
                if (_Connection == null)
                    return false;
                if (_TransactionCover == null)
                    _TransactionCover = _Connection.BeginTransaction();
                using (var command = new SQLiteCommand(_Connection))
                {
                    command.CommandText = "INSERT INTO Cover (Path, width, height) VALUES (@path, @w, @h)";
                    command.Parameters.Add("@w", DbType.Int32).Value = size.Width;
                    command.Parameters.Add("@h", DbType.Int32).Value = size.Height;
                    command.Parameters.Add("@path", DbType.String).Value = coverPath;
                    command.ExecuteNonQuery();

                    command.CommandText = "SELECT id FROM Cover WHERE [Path] = @path";
                    command.Parameters.Add("@path", DbType.String).Value = coverPath;
                    SQLiteDataReader reader = command.ExecuteReader();

                    if (reader != null)
                    {
                        reader.Read();
                        int id = reader.GetInt32(0);
                        reader.Dispose();
                        command.CommandText = "INSERT INTO CoverData (CoverID, Data) VALUES (@id, @data)";
                        command.Parameters.Add("@id", DbType.Int32).Value = id;
                        command.Parameters.Add("@data", DbType.Binary).Value = data;
                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            return false;
        }

        public void CommitCovers()
        {
            lock (_Mutex)
            {
                _CommitCovers();
            }
        }

        /// <summary>
        ///     You have to hold the CoverMutex when calling this!
        /// </summary>
        private void _CommitCovers()
        {
            if (_TransactionCover == null)
                return;
            _TransactionCover.Commit();
            _TransactionCover.Dispose();
            _TransactionCover = null;
        }

        private bool _CreateCoverDB()
        {
            try
            {
                using (var command = new SQLiteCommand(_Connection))
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS Version (Value INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO Version (Value) VALUES(@Value)";
                    command.Parameters.Add("@Value", DbType.Int32).Value = CSettings.DatabaseCoverVersion;
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE IF NOT EXISTS Cover ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                          "Path TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE IF NOT EXISTS CoverData ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                          "CoverID INTEGER NOT NULL, Data BLOB NOT NULL);";
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error creating Cover DB " + e);
                return false;
            }
            return true;
        }
    }
}