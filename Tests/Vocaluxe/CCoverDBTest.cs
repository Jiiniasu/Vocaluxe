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
using NUnit.Framework;
using Vocaluxe.Base;
using Vocaluxe.Lib.Database;
using VocaluxeLib.Draw;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Cover database tests. The success path of GetCover ends in CDraw.EnqueueTexture (OpenGL), which is
    ///     not available in the test host, so these tests cover the database side (schema creation and the
    ///     clean failure on a missing file) and leave the texture production to the playtest.
    /// </summary>
    [TestFixture]
    public class CCoverDBTest
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
        public void TestInit_CreatesNewV1DatabaseSchema()
        {
            string path = Path.Combine(_TempDir, "CoverDB");

            CCoverDB db = new CCoverDB(path);
            Assert.AreEqual(true, db.Init());
            db.Close();

            Assert.AreEqual(CSettings.DatabaseCoverVersion, _ScalarInt(path, "SELECT Value FROM Version"));
            Assert.AreEqual(
                2,
                _ScalarInt(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Cover', 'CoverData')"));
        }

        [Test]
        public void TestGetCover_MissingFile_ReturnsFalse()
        {
            string path = Path.Combine(_TempDir, "CoverDB");
            string missing = Path.Combine(_TempDir, "does-not-exist.jpg");

            CCoverDB db = new CCoverDB(path);
            Assert.AreEqual(true, db.Init());

            CTextureRef tex = null;
            Assert.AreEqual(false, db.GetCover(missing, ref tex, 256));
            db.Close();
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
