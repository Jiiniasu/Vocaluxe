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
using VocaluxeLib.Draw;

namespace VocaluxeLib.Menu.SingNotes
{
    /// <summary>
    ///     Sparkly trail of the sung tone for continuous player feedback.
    ///     Dots store the tone of the sung pitch, their Y position is derived
    ///     from the current pitch window each frame, so the trail glides with
    ///     the scale even while the pitch window is transitioning
    /// </summary>
    public sealed class CVoiceTrail
    {
        /// <summary>
        ///     A dot of the trail (a single detected pitch)
        /// </summary>
        private struct SDot
        {
            public float Tone; //sung tone (0 = C2, fractional = cent precision)
            public float Size; //[px]
            public float Jitter; //[px] vertical offset
            public float Phase; //sparkle phase [rad]
            public float SpawnBeat;
        }

        private const int MaxDots = 512;
        private const float MinFadeWidth = 20f; //px
        private const float TwinkleSpeed = 8f; //rad per beat

        private readonly float _Spacing; //minimum distance between two dots [px]
        private readonly List<SDot> _Dots = new List<SDot>();

        /// <param name="spacing">minimum distance between two dots in pixels</param>
        public CVoiceTrail(float spacing)
        {
            _Spacing = spacing;
        }

        /// <summary>
        ///     Adds a dot if the previous one has scrolled past the minimum spacing,
        ///     so the dot density is independent of the frame rate
        /// </summary>
        public bool TrySpawn(float beat, float noteWidth, float tone, float size, float jitter, float phase)
        {
            if (noteWidth <= 0f || _Dots.Count >= MaxDots)
                return false;

            if (_Dots.Count > 0 && (beat - _Dots[_Dots.Count - 1].SpawnBeat) * noteWidth < _Spacing)
                return false;

            _Dots.Add(new SDot
            {
                Tone = tone,
                Size = size,
                Jitter = jitter,
                Phase = phase,
                SpawnBeat = beat
            });
            return true;
        }

        public void Cull(float barLeft, float spawnX, float noteWidth, float beat)
        {
            _Cull(barLeft, spawnX, noteWidth, beat);
        }

        public void Draw(CTextureRef texture, SRectF barRect, float spawnX, float noteWidth, float beat, float viewTop, float semiToneHeight)
        {
            int count = _Cull(barRect.X, spawnX, noteWidth, beat);
            if (count == 0)
                return;

            float fadeWidth = barRect.W * 0.25f;
            if (fadeWidth < MinFadeWidth)
                fadeWidth = MinFadeWidth;

            for (int i = 0; i < count; i++)
            {
                SDot dot = _Dots[i];
                //Center of the dot (spawns centered on the judgement line).
                //The +1 is the class' row convention: a note of tone T is centered one row
                //below its index line (see _GetNoteRect and the tone helper, both of which
                //use (viewTop - tone + 1) * semiToneHeight)
                float x = spawnX - (beat - dot.SpawnBeat) * noteWidth;
                float left = x - dot.Size / 2f;
                float top = barRect.Y + (viewTop - dot.Tone + 1f) * semiToneHeight - dot.Size / 2f + dot.Jitter;

                // Fade out while the dot leaves the bar on the left.
                // Like the tone helper band, the dots are NOT dimmed by the note panel's
                // alpha (player fading etc.): while the tone-helper option is on the trail
                // is fully visible and goes away only by scrolling off the left edge
                float fade = (x - barRect.X) / fadeWidth;
                if (fade < 0f)
                    fade = 0f;
                if (fade > 1f)
                    fade = 1f;
                float a = fade * (0.9f + 0.1f * (float)Math.Sin(dot.Phase + beat * TwinkleSpeed));
                if (a <= 0f)
                    continue;

                CBase.Drawing.DrawTexture(texture, new SRectF(left, top, dot.Size, dot.Size, barRect.Z), new SColorF(1f, 1f, 1f, a), barRect, false, false);
            }
        }

        /// <summary>
        ///     Removes dots that have scrolled out of the bar, returns the number of dots left
        /// </summary>
        private int _Cull(float barLeft, float spawnX, float noteWidth, float beat)
        {
            int write = 0;
            for (int i = 0; i < _Dots.Count; i++)
            {
                SDot dot = _Dots[i];
                //Keep while the right edge of the dot is still inside the bar
                if (spawnX - (beat - dot.SpawnBeat) * noteWidth + dot.Size / 2f > barLeft)
                    _Dots[write++] = dot;
            }
            if (write < _Dots.Count)
                _Dots.RemoveRange(write, _Dots.Count - write);
            return write;
        }
    }
}
