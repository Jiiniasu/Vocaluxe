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
// MERCHANTABILITY; FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe.Base
{
    // A small, durable, first-in-first-out queue for cloud uploads that could not be delivered
    // (e.g. a putRound while the network was down). It survives an app restart so queued rounds
    // get a later chance to be uploaded, in order, and it is bounded (max attempts / max age)
    // so a long outage cannot accumulate without limit or be replayed without restraint.
    //
    // Deliberately kept independent of CDataBase: it has its own schema and no relationship to
    // the highscore/cover tables.
    static class COutbox
    {
        private static readonly object _Lock = new object();
        private static readonly List<SOutboxEntry> _Entries = new List<SOutboxEntry>();
        private static bool _Loaded;

        // A record that has been retried this many times (on top of the original attempt) is dropped.
        public static int MaxAttempts { get { return 5; } }

        // Records older than this are dropped, so old rounds aren't silently replayed days later.
        public static TimeSpan MaxAge { get { return TimeSpan.FromDays(7); } }

        // The queue file is a local, human-readable artifact: indented exactly like the old
        // Json.NET Formatting.Indented output, property names unchanged, names matched leniently.
        private static readonly JsonSerializerOptions _FileOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public sealed class SOutboxEntry
        {
            public string Id { get; set; }
            public string Endpoint { get; set; }
            public string Payload { get; set; }
            public DateTime CreatedAt { get; set; }
            public int Attempts { get; set; }
            public DateTime? LastAttempt { get; set; }

            public SOutboxEntry Clone()
            {
                return new SOutboxEntry
                {
                    Id = Id,
                    Endpoint = Endpoint,
                    Payload = Payload,
                    CreatedAt = CreatedAt,
                    Attempts = Attempts,
                    LastAttempt = LastAttempt
                };
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(CSettings.DataFolder, "cloud_outbox.json"); }
        }

        #region public stuff

        public static int Count
        {
            get
            {
                EnsureLoaded();
                lock (_Lock)
                    return _Entries.Count;
            }
        }

        public static void Enqueue(string endpoint, string payload, string id)
        {
            if (string.IsNullOrEmpty(id))
                return;

            EnsureLoaded();
            lock (_Lock)
            {
                if (_Entries.Exists(e => e.Id == id))
                    return; // already queued - avoid a duplicate.

                _Entries.Add(new SOutboxEntry
                {
                    Id = id,
                    Endpoint = endpoint,
                    Payload = payload,
                    CreatedAt = DateTime.Now
                });
                SaveLocked();
            }
        }

        // Returns a copy of the oldest pending record, or null when the queue is empty.
        public static SOutboxEntry Oldest()
        {
            EnsureLoaded();
            lock (_Lock)
            {
                SOutboxEntry oldest = null;
                DateTime earliest = DateTime.MaxValue;
                foreach (SOutboxEntry e in _Entries)
                {
                    if (e.CreatedAt <= earliest)
                    {
                        earliest = e.CreatedAt;
                        oldest = e;
                    }
                }
                return oldest != null ? oldest.Clone() : null;
            }
        }

        // Record delivered successfully (or permanently failed) - remove it either way.
        public static void Remove(string id)
        {
            EnsureLoaded();
            lock (_Lock)
            {
                var e = _Entries.Find(x => x.Id == id);
                if (e == null)
                    return;
                _Entries.Remove(e);
                SaveLocked();
            }
        }

        // Give up on this record (too many attempts / too old) - remove and persist.
        public static void Drop(string id)
        {
            Remove(id);
        }

        // A flushed record failed for a retryable reason - remember that we tried it once more.
        public static void BumpAttempt(string id)
        {
            EnsureLoaded();
            lock (_Lock)
            {
                var e = _Entries.Find(x => x.Id == id);
                if (e == null)
                    return;
                e.Attempts++;
                e.LastAttempt = DateTime.Now;
                SaveLocked();
            }
        }
        #endregion public stuff

        #region private stuff

        private static void EnsureLoaded()
        {
            lock (_Lock)
            {
                if (_Loaded)
                    return;

                _Loaded = true;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        string json = File.ReadAllText(FilePath);
                        List<SOutboxEntry> loaded = JsonSerializer.Deserialize<List<SOutboxEntry>>(json, _FileOptions);
                        if (loaded != null)
                            _Entries.AddRange(loaded);
                    }
                }
                catch (Exception e)
                {
                    CLog.CCloudLog.Error(e, "Could not load the cloud upload outbox: {msg}", CLog.Params(e.Message));
                }
            }
        }

        // Writes the queue atomically (write to a temp file, then move it into place) so a crash
        // in the middle of a save does not leave a truncated file behind. Caller must hold _Lock.
        private static void SaveLocked()
        {
            try
            {
                string dir = CSettings.DataFolder;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(_Entries, _FileOptions);
                string tmp = FilePath + ".tmp";

                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                CLog.CCloudLog.Error(e, "Could not save the cloud upload outbox: {msg}", CLog.Params(e.Message));
            }
        }
        #endregion private stuff
    }
}