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

using NUnit.Framework;
using System;
using VocaluxeLib.Menu.SingNotes;

namespace Tests.VocaluxeLib.Menu.SingNotes
{
    [TestFixture]
    public class CNoteBarsTest
    {
        private const float _NoteWidth = 40f;

        [Test]
        public void FillWidthTracksPlayheadInsideNote()
        {
            // Note of duration 11, beats 100..110.
            Assert.AreEqual(0f, CNoteBars.GetSungNoteFillWidth(100, 110, 100f, _NoteWidth));
            Assert.AreEqual(2.5f * _NoteWidth, CNoteBars.GetSungNoteFillWidth(100, 110, 102.5f, _NoteWidth));
            Assert.AreEqual(10f * _NoteWidth, CNoteBars.GetSungNoteFillWidth(100, 110, 110f, _NoteWidth));
        }

        [Test]
        public void FillWidthIsFullOncePlayheadPassedNoteEnd()
        {
            // Note of duration 11, beats 100..110.
            Assert.AreEqual(11f * _NoteWidth, CNoteBars.GetSungNoteFillWidth(100, 110, 111f, _NoteWidth));
            Assert.AreEqual(11f * _NoteWidth, CNoteBars.GetSungNoteFillWidth(100, 110, 500f, _NoteWidth));
        }

        [Test]
        public void FillWidthIsZeroBeforeNoteStarts()
        {
            Assert.AreEqual(0f, CNoteBars.GetSungNoteFillWidth(100, 110, 99.9f, _NoteWidth));
            Assert.AreEqual(0f, CNoteBars.GetSungNoteFillWidth(100, 110, -10f, _NoteWidth));
        }

        [Test]
        public void FillWidthNeverMovesBackwardsWhileNoteIsSustained()
        {
            // Simulate successive frames while a sustained note is being sung: the
            // playhead only ever advances and the sung duration grows in whole beats as
            // the playhead crosses beat boundaries (see CGame.UpdatePoints), up to 16
            // beats. The fill width must never recede, otherwise the tip flickers.
            int startBeat = 100;
            float lastWidth = -1f;
            for (float beat = startBeat; beat <= startBeat + 17f; beat += 0.25f)
            {
                int duration = Math.Min(16, (int)(beat - startBeat) + 1);
                int endBeat = startBeat + duration - 1;
                float width = CNoteBars.GetSungNoteFillWidth(startBeat, endBeat, beat, _NoteWidth);
                Assert.GreaterOrEqual(width, lastWidth, $"width moved backwards at beat {beat} while duration was {duration}");
                lastWidth = width;
            }
        }
    }
}