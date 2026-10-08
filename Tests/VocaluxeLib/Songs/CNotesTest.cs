#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using NUnit.Framework;
using VocaluxeLib.Songs;

namespace Tests.VocaluxeLib.Songs
{
    [TestFixture]
    public class CNotesTest
    {
        private static CNotes _NotesWithVoices(int voiceCount)
        {
            CNotes notes = new CNotes();
            for (int v = 0; v < voiceCount; v++)
                notes.AddVoice(new CVoice());
            return notes;
        }

        #region Tests

        [Test]
        public void TestVoiceIndexValidInSoloSong()
        {
            CNotes notes = _NotesWithVoices(1);
            Assert.IsTrue(CNotes.IsVoiceIndexValidIn(notes, 0));
            Assert.IsFalse(CNotes.IsVoiceIndexValidIn(notes, 1));
            Assert.IsFalse(CNotes.IsVoiceIndexValidIn(notes, 2));
        }

        [Test]
        public void TestVoiceIndexValidInDuetSong()
        {
            CNotes notes = _NotesWithVoices(2);
            Assert.IsTrue(CNotes.IsVoiceIndexValidIn(notes, 0));
            Assert.IsTrue(CNotes.IsVoiceIndexValidIn(notes, 1));
            Assert.IsFalse(CNotes.IsVoiceIndexValidIn(notes, 2));
        }

        [Test]
        public void TestVoiceIndexWithNoSongDefaultsToDefaultVoiceOnly()
        {
            // Without a loaded song only the default voice can be considered valid,
            // so a stale voice from a different song that has not loaded yet is
            // rejected instead of being silently scored against the wrong voice.
            Assert.IsTrue(CNotes.IsVoiceIndexValidIn(null, 0));
            Assert.IsFalse(CNotes.IsVoiceIndexValidIn(null, 1));
            Assert.IsFalse(CNotes.IsVoiceIndexValidIn(null, -1));
            Assert.IsFalse(CNotes.IsVoiceIndexValidIn(null, 100));
        }

        #endregion
    }
}