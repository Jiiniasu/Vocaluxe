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
using SkiaSharp;
using System;
using System.Drawing;
using System.IO;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Database
{
    public class CCoverDB : CDatabaseBase
    {
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

        public bool GetCover(string coverPath, ref CTextureRef tex, int maxSize)
        {
            if (!_Initialized)
                return false;
            if (!File.Exists(coverPath))
            {
                CLog.Error("Can't find File: " + coverPath);
                return false;
            }

            lock (_Mutex)
            {
                //Double check here because we may have just closed the database
                if (!_Initialized)
                    return false;
                using (var connection = _OpenConnection(_FilePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, width, height FROM Cover WHERE [Path] = @path";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@path", coverPath);

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
                        //Reader is closed, so this command can be reused for a second query
                        command.CommandText = "SELECT Data FROM CoverData WHERE CoverID = @id";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@id", id);

                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                byte[] data2 = _GetBytes(reader);
                                tex = CDraw.EnqueueTexture(w, h, data2);
                                return true;
                            }
                        }

                        //Stale entry without data: drop it, the fallback below rebuilds the cover
                        command.CommandText = "DELETE FROM Cover WHERE id = @id";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@id", id);
                        command.ExecuteNonQuery();
                    }
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
                //Double check here because we may have just closed the database
                if (!_Initialized)
                    return false;
                using (var connection = _OpenConnection(_FilePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO Cover (Path, width, height) VALUES (@path, @w, @h)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@w", size.Width);
                    command.Parameters.AddWithValue("@h", size.Height);
                    command.Parameters.AddWithValue("@path", coverPath);
                    command.ExecuteNonQuery();

                    int id = -1;
                    command.CommandText = "SELECT id FROM Cover WHERE [Path] = @path";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@path", coverPath);
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                            id = reader.GetInt32(0);
                    }

                    if (id >= 0)
                    {
                        command.CommandText = "INSERT INTO CoverData (CoverID, Data) VALUES (@id, @data)";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@id", id);
                        command.Parameters.AddWithValue("@data", data);
                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        ///     Covers are written to the database right away (each operation commits itself), so there
        ///     is nothing left to commit; kept for API compatibility with the deferred-transaction days.
        /// </summary>
        public void CommitCovers()
        {
            //No-op by design: see the comment on GetCover - every write is self-committing
        }

        private bool _CreateCoverDB()
        {
            try
            {
                using (var connection = _OpenConnection(_FilePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE IF NOT EXISTS Version (Value INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO Version (Value) VALUES(@Value)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@Value", CSettings.DatabaseCoverVersion);
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