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
using System.Text;
using Microsoft.Data.Sqlite;

namespace Vocaluxe.Lib.Database
{
    public abstract class CDatabaseBase
    {
        protected readonly string _FilePath;

        //True between Init() and Close()
        protected bool _Initialized;

        //You have to lock all actions on a database using _Mutex, otherwise order is not guaranteed
        protected readonly object _Mutex = new object();

        protected int _Version; //Version of the database (value of row "Value" in table "Version") or -1 if none

        protected CDatabaseBase(string filePath)
        {
            _FilePath = filePath;
        }

        //Microsoft.Data.Sqlite connections are thread-affine, so the databases no longer hold one persistent
        //connection: every operation opens a short-lived connection and closes it right away (MDS pools them
        //under the hood, so this is cheap). Throws if the database cannot be opened - use a using block.
        protected static SqliteConnection _OpenConnection(string filePath)
        {
            var connection = new SqliteConnection("Data Source=" + filePath);
            connection.Open();
            return connection;
        }

        public virtual bool Init()
        {
            lock (_Mutex)
            {
                if (_Initialized)
                    return false;

                SqliteConnection connection;

                try
                {
                    connection = _OpenConnection(_FilePath);
                }
                catch (Exception)
                {
                    return false;
                }

                using (connection)
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Value FROM Version";

                    SqliteDataReader reader = null;

                    try
                    {
                        reader = command.ExecuteReader();
                    }
                    catch (Exception) { }

                    if (reader == null || !reader.Read() || reader.FieldCount == 0)
                        _Version = -1;
                    else
                        _Version = reader.GetInt32(0);

                    if (reader != null)
                        reader.Dispose();
                }
                _Initialized = true;
            }
            return true;
        }

        public virtual void Close()
        {
            lock (_Mutex)
            {
                _Initialized = false;
            }
        }

        protected static long _UnixTimeToTicks(int unixTime)
        {
            var t70 = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            t70 = t70.AddSeconds(unixTime);
            return t70.Ticks;
        }

        /// <summary>
        ///     Reads the contents of a field as a byte array <br />
        ///     Assumes you did the locking!
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="field">Field number to read from (default=first)</param>
        /// <returns></returns>
        protected static byte[] _GetBytes(SqliteDataReader reader, int field = 0)
        {
            //MDS has no zero-copy chunked BLOB reader (the offset overloads of reader.GetBytes throw
            //NotSupportedException): the value is already fully materialized, so read it as one array.
            var value = reader.GetValue(field);
            if (value is byte[] bytes)
                return bytes;
            if (value is string text)
                return Encoding.UTF8.GetBytes(text);
            throw new InvalidCastException("Field " + field + " is not a BLOB (" + (value == null ? "null" : value.GetType().Name) + ")");
        }
    }
}
