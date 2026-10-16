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
using System.Linq;
using Vocaluxe.Base;
using Vocaluxe.Lib.Database;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     The repository ships the genuine CreditsRessourcesDB.sqlite that was written by the legacy
    ///     System.Data.SQLite provider. These tests prove the new Microsoft.Data.Sqlite reader opens the very
    ///     same file and gets identical, intact data out of it. (The texture-producing half of
    ///     GetCreditsRessource goes through CDraw/OpenGL and is covered by the playtest.)
    /// </summary>
    [TestFixture]
    public class CResourceDBTest
    {
        private string _RealDbPath;

        [SetUp]
        public void SetUp()
        {
            string root = _FindRepoRoot();
            Assert.IsNotNull(root, "Vocaluxe.slnx not found above the test output directory");

            _RealDbPath = Path.Combine(root, "Output", "CreditsRessourcesDB.sqlite");
            Assert.IsTrue(File.Exists(_RealDbPath), "committed credits resource DB missing: " + _RealDbPath);
        }

        [Test]
        public void TestInit_OpensRealCommittedCreditsRessourcesDb()
        {
            CResourceDB db = new CResourceDB(_RealDbPath);
            Assert.AreEqual(true, db.Init());
            db.Close();

            //The legacy file reports exactly the version the game expects - no conversion path is triggered
            Assert.AreEqual(CSettings.DatabaseCreditsRessourcesVersion, _ScalarInt(_RealDbPath, "SELECT Value FROM Version"));
            Assert.AreEqual(12, _ScalarInt(_RealDbPath, "SELECT COUNT(*) FROM Images"));
        }

        [Test]
        public void TestRealCreditsRessourcesDb_ImageBlobsAreIntact()
        {
            string fileName;
            int width;
            int height;
            byte[] data;

            using (var connection = new SqliteConnection("Data Source=" + _RealDbPath))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM Images";
                    int images = Convert.ToInt32(command.ExecuteScalar());
                    Assert.Greater(images, 0);

                    command.CommandText = "SELECT i.Path, i.Width, i.Height, d.Data " +
                                          "FROM Images i INNER JOIN ImageData d ON d.ImageID = i.id " +
                                          "ORDER BY i.id LIMIT 1";

                    using (var reader = command.ExecuteReader())
                    {
                        Assert.AreEqual(true, reader.Read());
                        fileName = reader.GetString(0);
                        width = reader.GetInt32(1);
                        height = reader.GetInt32(2);
                        data = reader.GetValue(3) as byte[];
                    }
                }
            }

            StringAssert.EndsWith(".png", fileName);
            Assert.IsNotNull(data, "the image data BLOB must be readable");

            //The resource DB stores the decoded image as raw BGRA8888 pixels (4 bytes per pixel) - that is
            //exactly what the game uploads to the GPU - so the blob must cover the full image, and it must
            //actually contain pixels.
            Assert.AreEqual((long)width * height * 4, data.Length);
            Assert.IsTrue(data.Any(b => b != 0), "the stored image blob must not be all zero");
        }

        private static string _FindRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Vocaluxe.slnx")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
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
    }
}