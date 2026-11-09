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
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using VocaluxeLib.Draw;
using VocaluxeLib.Songs;

namespace VocaluxeLib.Menu.SingNotes
{
    public class CNoteBars
    {
        private readonly SThemeSingBar _Theme;
        private readonly int _PartyModeID;
        private readonly int _Player;
        /// <summary>
        ///     Fixed pixel height of a tone row, independent of the pitch window
        /// </summary>
        private readonly float _ToneHeightFixed;
        public readonly SRectF Rect;
        private readonly SColorF _PlayerColor;
        private readonly SColorF _NoteLinesColor;
        private readonly SColorF _NoteBaseColor;
        public float Alpha = 1;
        private float ToneHelperAlpha;
        private readonly CSongLine[] _Lines;
        private CText _CurrentToneText = new CText(0, 0, 0, 14, 50, EAlignment.Right, EStyle.Bold, "Outline", new SColorF(Color.White), String.Empty);

        /// <summary>
        ///     Comfortable base span of the pitch window in semi-tones. The window rests here and only widens beyond it
        ///     when the on-lane material is taller than it, then eases back down (never shrinks below it)
        /// </summary>
        private readonly float _BaseSpan;
        /// <summary>
        ///     Bottom (lowest semi-tone) of the currently applied pitch window
        /// </summary>
        private float _AppliedViewBottom;
        /// <summary>
        ///     Span of the currently applied pitch window, in semi-tones
        /// </summary>
        private float _AppliedSpan;
        /// <summary>
        ///     The span requirement a tall phrase leaves behind: it rises instantly when a taller requirement
        ///     appears and otherwise decays toward the current requirement at <see cref="SpanHoldRate"/>, so a
        ///     section that alternates low and high phrases keeps the window wide instead of pumping the zoom
        /// </summary>
        private float _SpanHold;

        /// <summary>
        ///     Breathing room at each window edge, in semi-tones (how far inside the bar the outermost note body sits)
        /// </summary>
        private const float WindowPad = 2f;
        /// <summary>
        ///     Largest span the fitter may use (eight octaves, well beyond any single phrase's height)
        /// </summary>
        private const float MaxFitSpan = 96f;
        /// <summary>
        ///     How quickly the window settles toward its target, in inverse seconds (higher is snappier, lower
        ///     is a slower glide - pair with <see cref="SettleHorizonSec"/> so the settle still has time to finish)
        /// </summary>
        private const float SettleRate = 2f;
        /// <summary>
        ///     How far ahead (in seconds of song time) the fitter looks so the window can start its glide before
        ///     the material it has to frame arrives, and be done settling by the time it's needed (keep this at
        ///     or above ~3/<see cref="SettleRate"/> so the slow settle still has room to finish)
        /// </summary>
        private const float SettleHorizonSec = 2.5f;
        /// <summary>
        ///     How fast the remembered span requirement (<see cref="_SpanHold"/>) decays toward the current one,
        ///     in inverse seconds. A tall phrase therefore keeps the window wide for a few seconds after it has
        ///     passed instead of vanishing at once - that is what stops a section that alternates low and high
        ///     phrases (e.g. Killswitch Engage - My Curse) from pumping the zoom in and out on every phrase
        /// </summary>
        private const float SpanHoldRate = 1f / 3f;

        /// <summary>
        ///     Additional height of a note according to selected difficulty
        /// </summary>
        private readonly int _Difficulty;

        /// <summary>
        ///     Top (highest semi-tone) of the pitch window
        /// </summary>
        private float _ViewTop;
        /// <summary>
        ///     Bottom (lowest semi-tone) of the pitch window
        /// </summary>
        private float _ViewBottom;
        /// <summary>
        ///     Pixels per semi-tone of the pitch window
        /// </summary>
        private float _SemiToneHeight;
        /// <summary>
        ///     Pixels per tone (2 semi-tones) of the pitch window
        /// </summary>
        private float _ToneHeight;
        /// <summary>
        ///     Additional height of a note according to selected difficulty
        /// </summary>
        private float _AddNoteHeight;

        private readonly float _NoteWidth;

        private readonly int _RangeSemiToneCount;
        private readonly int _RangeSemiToneMax = CBase.Settings.GetToneMin();
        private readonly int _RangeSemiToneMin = CBase.Settings.GetToneMax();

        private readonly List<int> _AccidentalNotes = new List<int> { 1, 3, 6, 8, 10 };

        private int _CurrentLine = -1;
        private float _LastBeatF;

        private readonly int _JudgementLine = CBase.Config.GetJudgementDistance();

        private readonly Stopwatch _Timer = new Stopwatch();
        private readonly Stopwatch _ToneHelperTimer = new Stopwatch();
        private readonly CVoiceTrail _VoiceTrail;
        private readonly List<CParticleEffect> _GoldenStars = new List<CParticleEffect>();
        private readonly List<CParticleEffect> _Flares = new List<CParticleEffect>();
        private readonly List<CParticleEffect> _PerfectNoteEffect = new List<CParticleEffect>();
        private readonly List<CParticleEffect> _PerfectLineTwinkle = new List<CParticleEffect>();

        private bool _ShowToneHelperText;

        private readonly List<string> _Tone = new List<string>()
        {
            "C",
            "C♯",
            "D",
            "D♯",
            "E",
            "F",
            "F♯",
            "G",
            "G♯",
            "A",
            "A♯",
            "B"
        };

        private readonly List<string> _Octave = new List<string>()
        {
            "₀",
            "₁",
            "₂",
            "₃",
            "₄",
            "₅",
            "₆",
            "₇",
            "₈",
            "₉"
        };

        public CNoteBars(int partyModeID, int player, SRectF rect, SThemeSingBar theme)
        {
            _Player = player;
            _Theme = theme;
            _PartyModeID = partyModeID;
            Rect = rect;

            _PlayerColor = CBase.Themes.GetPlayerColor(player + 1);

            if (!CBase.Themes.GetColor("NoteLinesColor", _PartyModeID, out _NoteLinesColor))
                _NoteLinesColor = new SColorF(Color.Gray, 0.5f);

            if (!CBase.Themes.GetColor("NoteBaseColor", _PartyModeID, out _NoteBaseColor))
                _NoteBaseColor = new SColorF(Color.White);

            SPlayer playerData = CBase.Game.GetPlayers()[player];
            CSong Song = CBase.Game.GetSong();

            _Lines = Song.Notes.GetVoice(playerData.VoiceNr).Lines;

            _RangeSemiToneMin = _Lines.Where(line => line.MinNote != int.MinValue)
                                      .Select(line => line.MinNote)
                                      .DefaultIfEmpty(0)
                                      .Min() - 2;
            _RangeSemiToneMax = _Lines.Where(line => line.MaxNote != int.MaxValue)
                                      .Select(line => line.MaxNote)
                                      .DefaultIfEmpty(0)
                                      .Max() + 2;
            _RangeSemiToneCount = _RangeSemiToneMax - _RangeSemiToneMin;

            _Difficulty = (int)playerData.Difficulty;

            // Fixed scroll speed (pixels per beat), independent of the voice range of the song.
            // The reference is the tone height of the default 11 note-lines bar.
            _ToneHeightFixed = Rect.H / CBase.Settings.GetNumNoteLines();
            _NoteWidth = _ToneHeightFixed * 2f * CBase.Config.GetNoteSpeed();

            // The comfortable span the window rests at: the whole voice if it fits within two
            // octaves, otherwise two octaves. The per-frame fitter never dips below this and only
            // widens past it while the on-lane material is genuinely taller than the window
            _BaseSpan = Math.Max(12f, Math.Min(24f, _RangeSemiToneMax - _RangeSemiToneMin));

            // Seed the applied window so the opening bar is already sensible: rest on the first
            // content that will be on screen (or the voice centre), the fitter keeps it correct after
            float centre = (_RangeSemiToneMin + _RangeSemiToneMax) / 2f;
            if (_Lines.Length > 0 && _Lines[0].NoteCount > 0 && _Lines[0].MinNote != int.MaxValue)
                centre = (_Lines[0].MinNote + _Lines[0].MaxNote) / 2f;
            _AppliedSpan = _BaseSpan;
            _AppliedViewBottom = centre - _BaseSpan / 2f;

            if (playerData.ToneHelperText == EOffOn.TR_CONFIG_ON)
            {
                _ShowToneHelperText = true;
                _CurrentToneText.Z = Rect.Z;
                _CurrentToneText.AllMonitors = false;
            }

            // Dots of the voice trail roughly overlap so the trail looks continuous
            _VoiceTrail = new CVoiceTrail(Math.Max(8f, _ToneHeightFixed * 0.5f));
        }

        public void SetLine(int line)
        {
            if (_CurrentLine == line)
                return;
            _CurrentLine = line;

        }

        public void Draw()
        {
            float currentBeatF = CBase.Game.GetCurrentBeatF();
            float beatDiff = currentBeatF - _LastBeatF;
            _LastBeatF = currentBeatF;

            // The song can be null here: during the end-of-song transition CGame has already advanced to
            // the next round, but the (fading) sing screen is drawn one last time. _ResolveView clamps
            // dtSec to [0, 0.25], so a 1-BPM fallback just yields that frame's maximum step.
            float dtSec = beatDiff * 60f / Math.Max(1f, CBase.Game.GetSong()?.BPM ?? 1f);
            _ResolveView(currentBeatF, dtSec);

            _DrawJudgeLine(new SColorF(_NoteLinesColor, _NoteLinesColor.A));

            bool toneHelper = CBase.Config.GetDrawToneHelper() == EOffOn.TR_CONFIG_ON;
            float trailSpawnX = Rect.X + _JudgementLine;

            if (_CurrentLine == -1 || _CurrentLine >= _Lines.Length)
            {
                // No note line on the screen: the voice trail still scrolls and fades
                if (toneHelper)
                    _DrawVoiceTrail(currentBeatF, trailSpawnX);
                else
                    _VoiceTrail.Cull(Rect.X, trailSpawnX, _NoteWidth, currentBeatF);
                return;
            }

            CSongLine line = _Lines[_CurrentLine];

            var color = new SColorF(_PlayerColor, _PlayerColor.A * Alpha);

            if (CBase.Config.GetDrawNoteLines() == EOffOn.TR_CONFIG_ON)
            {
                _DrawOctaveLines(new SColorF(0, 0, 0, 0.1f * Alpha));
                _DrawScaleLines(new SColorF(0, 0, 0, 0.6f * Alpha));
            }

            _DrawLineSeparators(new SColorF(_NoteLinesColor, _NoteLinesColor.A));

            _DrawNotes(color);

            List<CSungLine> sungLines = CBase.Game.GetPlayers()[_Player].SungLines;

            float beats = line.LastNoteBeat - line.FirstNoteBeat + 1;

            for (int i = _CurrentLine > 0 ? _CurrentLine - 1 : 0; i < sungLines.Count; i++)
            {
                foreach (CSungNote note in sungLines[i].Notes)
                {
                    while (note.Tone < _RangeSemiToneMin && note.Tone + 12 < _RangeSemiToneMin + _RangeSemiToneCount)
                    {
                        note.Tone += 12;
                    }
                    while (note.Tone > _RangeSemiToneMin + _RangeSemiToneCount && note.Tone - 12 > _RangeSemiToneMin)
                    {
                        note.Tone -= 12;
                    }

                    SRectF rect = _GetNoteRect(note);
                    if ((rect.Right > Rect.X && rect.Right < Rect.Right))
                    {
                        if (note.EndBeat == CBase.Game.GetRecordedBeat())
                            rect.W -= (1 - (CBase.Game.GetMidRecordedBeat() - CBase.Game.GetRecordedBeat())) * Rect.W / beats;

                        float factor = (note.Hit) ? 1f : 0.6f;

                        _DrawNoteFill(rect, color, factor);

                        if (note.Perfect && !note.PerfectDrawn && note.EndBeat < CBase.Game.GetRecordedBeat())
                        {
                            _AddPerfectNote(rect);
                            note.PerfectDrawn = true;
                        }
                    }
                }
            }

            if (toneHelper)
            {
                _DrawVoiceTrail(currentBeatF, trailSpawnX);
                _DrawToneHelper(line);
            }
            else
                _VoiceTrail.Cull(Rect.X, trailSpawnX, _NoteWidth, currentBeatF);

            if (sungLines.Count > 0 && sungLines[sungLines.Count - 1].PerfectLine)
            {
                _AddPerfectLine();
                sungLines[sungLines.Count - 1].PerfectLine = false;
            }

            _Flares.RemoveAll(el => !el.IsAlive);
            _PerfectNoteEffect.RemoveAll(el => !el.IsAlive);
            _PerfectLineTwinkle.RemoveAll(el => !el.IsAlive);
            _GoldenStars.RemoveAll(el => !el.IsAlive);

            foreach (CParticleEffect perfline in _PerfectLineTwinkle)
                perfline.Draw();

            foreach (CParticleEffect stars in _GoldenStars)
            {
                stars.Alpha = Alpha;
                stars.Draw();
            }

            foreach (CParticleEffect flare in _Flares)
                flare.Draw();

            foreach (CParticleEffect perfnote in _PerfectNoteEffect)
            {
                perfnote.X -= beatDiff * _NoteWidth;
                perfnote.Draw();
            }
        }

        /// <summary>
        ///     The song material the pitch window must contain, expressed as pitch-space edges of the note
        ///     bodies (each element is drawn one semi-tone below its nominal tone and scaled with the window,
        ///     so its extent is a fixed number of semi-tones - see <see cref="_Accumulate"/>). The sung
        ///     pitch (tone helper, voice trail) deliberately does not appear here: out-of-range singing must
        ///     not move the window, it is clamped/faded at draw time instead
        /// </summary>
        private struct SFit
        {
            public bool Any;
            public float NoteMax;     //highest note-body top edge, in semi-tones
            public float NoteMin;     //lowest note-body bottom edge, in semi-tones

            public bool Fits(float span)
            {
                if (!Any)
                    return true;
                //The whole band, plus the breathing room at each edge, must fit inside the span
                return span - (NoteMax - NoteMin) >= 2f * WindowPad;
            }

            public void ViewBottomRange(float span, out float lo, out float hi)
            {
                lo = NoteMax - span + WindowPad;   //top edge clears the top by WindowPad
                hi = NoteMin - WindowPad;          //bottom edge clears the bottom by WindowPad
            }
        }

        /// <summary>
        ///     Resolves the pitch window for the current frame. The window rests on the song's base span and
        ///     only widens once the on-lane material is taller than it (then eases back down); it slides the
        ///     centre as little as possible as the material changes; and, every frame, it guarantees that all
        ///     the on-lane notes stay inside the bar with <see cref="WindowPad"/> of breathing room. Only the
        ///     song's notes drive the window: the sung pitch (tone helper, voice trail) is clamped or faded
        ///     at draw time, so singing out of range never shifts or zooms the view. The glide target looks
        ///     <see cref="SettleHorizonSec"/> ahead of the on-lane material so the window is in place by the
        ///     time it is needed, while the per-frame invariant only covers what is visible - that is what
        ///     keeps the widening a smooth glide instead of a snap (demanding the full-horizon span instantly
        ///     used to zoom the window the moment a tall phrase entered the horizon). The span target also
        ///     remembers the tallest recent requirement (<see cref="_SpanHold"/>), decaying toward the current
        ///     one at <see cref="SpanHoldRate"/>, so a section that alternates low and high phrases does not
        ///     pump the zoom in and out on every phrase
        /// </summary>
        private void _ResolveView(float beat, float dtSec)
        {
            if (dtSec < 0f)
                dtSec = 0f;
            if (dtSec > 0.25f)
                dtSec = 0.25f;
            float setf = 1f - (float)Math.Exp(-dtSec * SettleRate);    //how far to settle this frame

            float noteHalf = (4f - _Difficulty) / 2f;                  //half a note body, in semi-tones (see _GetNoteRect)

            //--- gather what has to fit, on two horizons:
            //---  fit    : the on-lane notes plus SettleHorizonSec ahead of them -> the glide target
            //---  fitNow : the on-lane notes only -> the hard invariant (what is visible must fit right now)
            SFit fit = new SFit();
            SFit fitNow = new SFit();
            int startLine = _CurrentLine > 0 ? _CurrentLine - 1 : 0;
            float beatsPerSec = Math.Max(1f, CBase.Game.GetSong()?.BPM ?? 1f) / 60f;
            float rightCullPx = Rect.W + _NoteWidth * beatsPerSec * SettleHorizonSec;
            for (int i = startLine; i < _Lines.Length; i++)
            {
                CSongLine line = _Lines[i];
                float rightPx = (line.StartBeat - beat) * _NoteWidth;
                if (rightPx > rightCullPx)
                    break;                                              //off the right edge plus the settle horizon
                if ((line.EndBeat - beat) * _NoteWidth + _JudgementLine < 0)
                    continue;                                           //entirely off the left edge (same cull as _DrawNotes)
                int lineLo = line.MinNote, lineHi = line.MaxNote;
                if (lineLo == int.MaxValue || lineHi == int.MinValue)
                    continue;                                           //freestyle-only: nothing to frame by pitch
                _Accumulate(ref fit, lineLo, lineHi, noteHalf);
                if (rightPx <= Rect.W)
                    _Accumulate(ref fitNow, lineLo, lineHi, noteHalf); //on the lane: must fit right now
            }

            //--- settle the window toward a position that fits the material, and keep it legal ---
            float lo, hi;
            if (fit.Any)
            {
                if (!fitNow.Fits(MaxFitSpan))
                {
                    //Pathologically tall phrase on the lane: snap to the cap so this frame still stays inside
                    //instead of overflowing
                    _AppliedSpan = MaxFitSpan;
                    fitNow.ViewBottomRange(MaxFitSpan, out lo, out hi);
                    _AppliedViewBottom = lo + (hi - lo) * 0.5f;
                    PublishWindow();
                    return;
                }

                float spanReq = _SmallestFeasibleSpan(fit);
                //A tall phrase keeps the window wide a little after it has passed (see SpanHoldRate):
                //without this, a section that alternates low and high phrases flips the target on every
                //phrase and the glide - faithfully chasing it - pumps the zoom in and out
                if (spanReq > _SpanHold)
                    _SpanHold = spanReq;
                else
                    _SpanHold += (spanReq - _SpanHold) * (1f - (float)Math.Exp(-dtSec * SpanHoldRate));
                float desiredSpan = Math.Max(_BaseSpan, _SpanHold);

                fit.ViewBottomRange(desiredSpan, out lo, out hi);
                float desiredBottom = Math.Min(Math.Max(_AppliedViewBottom, lo), hi);   //stay put, or slide just as far as needed

                //Glide toward the target; the settle horizon above gives the glide room to finish before the
                //material arrives...
                _AppliedSpan += (desiredSpan - _AppliedSpan) * setf;
                _AppliedViewBottom += (desiredBottom - _AppliedViewBottom) * setf;

                //...and the hard invariant for what is visible right now. Every visible note has been part of
                //the glide target for the whole horizon, so this only trims a sub-percent residual - it can no
                //longer be the main motion (expanding here to the full-horizon requirement is what snapped)
                if (fitNow.Any)
                {
                    _AppliedSpan = Math.Max(_AppliedSpan, _SmallestFeasibleSpan(fitNow));
                    fitNow.ViewBottomRange(_AppliedSpan, out lo, out hi);
                    if (_AppliedViewBottom < lo)
                        _AppliedViewBottom = lo;
                    if (_AppliedViewBottom > hi)
                        _AppliedViewBottom = hi;
                }
            }
            else
            {
                //Nothing on the lane: rest at the base span, hold the current centre
                if (_SpanHold > 0f)
                    _SpanHold += (0f - _SpanHold) * (1f - (float)Math.Exp(-dtSec * SpanHoldRate));
                _AppliedSpan += (_BaseSpan - _AppliedSpan) * setf;
            }

            PublishWindow();
        }

        /// <summary>
        ///     Mirrors the applied window into the per-note draw variables
        /// </summary>
        private void PublishWindow()
        {
            _ViewTop = _AppliedViewBottom + _AppliedSpan;
            _ViewBottom = _AppliedViewBottom;
            _SemiToneHeight = Rect.H / _AppliedSpan;
            _ToneHeight = _SemiToneHeight * 2f;
            _AddNoteHeight = _ToneHeight / 2f * (2f - _Difficulty);
        }

        /// <summary>
        ///     Folds one line's pitch range into the fit. Each element is drawn one semitone (row) below its
        ///     nominal tone, so a note at pitch p sits centred on p - 1; that offset is baked in here so the
        ///     breathing room lands exactly on the edge
        /// </summary>
        private static void _Accumulate(ref SFit fit, int lineLo, int lineHi, float noteHalf)
        {
            if (!fit.Any)
            {
                fit.Any = true;
                fit.NoteMin = lineLo - 1 - noteHalf;
                fit.NoteMax = lineHi - 1 + noteHalf;
                return;
            }
            if (lineLo - 1 - noteHalf < fit.NoteMin)
                fit.NoteMin = lineLo - 1 - noteHalf;
            if (lineHi - 1 + noteHalf > fit.NoteMax)
                fit.NoteMax = lineHi - 1 + noteHalf;
        }

        /// <summary>
        ///     The smallest span from which the material fits with the breathing room (the fitter never goes below it)
        /// </summary>
        private static float _SmallestFeasibleSpan(SFit fit)
        {
            //With the note-body extent a fixed number of semi-tones this is a straight line (the binary search
            //it used to be around only mattered for the span-dependent helper/trail sizes, which no longer
            //constrain the window)
            return fit.NoteMax - fit.NoteMin + 2f * WindowPad;
        }

        /// <summary>
        ///     The semi-tone the tone helper is drawn at for the given line: the sung tone brought into the octave
        ///     of the current target note, the same octave mapping the voice trail uses. If the result falls
        ///     outside the current window (out-of-range singing) the helper is clamped to the bar edge when drawn
        /// </summary>
        private float _HelperTone(CSongLine line)
        {
            float exactTone = CBase.Record.GetToneExact(_Player);
            float tone = (exactTone >= 0f) ? exactTone + 24f : CBase.Record.GetToneAbs(_Player) + 24f;

            int idx = line.FindPreviousNote(CBase.Game.GetCurrentBeat());
            if (idx < 0)
                idx = 0;
            int target = line.Notes[idx].Tone;

            while (tone - target < -6)
                tone += 12;
            while (tone - target > 6)
                tone -= 12;
            if (tone > _RangeSemiToneMax)
                tone -= 12;
            if (tone < _RangeSemiToneMin)
                tone += 12;
            return tone;
        }

        private void _DrawNotes(SColorF color)
        {
            for (int i = _CurrentLine > 0 ? _CurrentLine - 1 : 0; i < _Lines.Count(); i++)
            {
                if ((_Lines[i].EndBeat - CBase.Game.GetCurrentBeatF()) * _NoteWidth + _JudgementLine < 0)
                    continue;
                if ((_Lines[i].StartBeat - CBase.Game.GetCurrentBeatF()) * _NoteWidth > Rect.W)
                    break;
                foreach (CSongNote note in _Lines[i].Notes)
                {
                    switch (note.Type)
                    {
                        case ENoteType.Normal:
                            _DrawNormalNote(note, color);
                            break;
                        case ENoteType.Golden:
                            _DrawGoldenNote(note, color);
                            break;
                        case ENoteType.Freestyle:
                            _DrawFreeStyleNote(note, color);
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        private SRectF _GetNoteRect(CBaseNote note)
        {
            float width = note.Duration * _NoteWidth;

            var noteRect = new SRectF(
                Rect.X + (note.StartBeat - CBase.Game.GetCurrentBeatF()) * _NoteWidth + _JudgementLine,
                Rect.Y + (_ViewTop - note.Tone) * _SemiToneHeight - (_AddNoteHeight / 2),
                width,
                _ToneHeight + _AddNoteHeight,
                Rect.Z
                );
            return noteRect;
        }

        private void _DrawJudgeLine(SColorF color)
        {
            SRectF judgeRect = Rect;
            judgeRect.W = 2f;
            judgeRect.X = Rect.X + _JudgementLine;
            CBase.Drawing.DrawRect(color, judgeRect, false);
        }

        private void _DrawLineSeparators(SColorF color)
        {
            SRectF separatorRect = Rect;
            separatorRect.W = 2f;
            separatorRect.X = Rect.X + _JudgementLine;

            for (int i = 0; i < _Lines.Count(); i++)
            {
                separatorRect.X = Rect.X - ((CBase.Game.GetCurrentBeatF() - _Lines[i].StartBeat) * _NoteWidth) + _JudgementLine;
                if (separatorRect.X > Rect.X && separatorRect.X < Rect.X + Rect.W)
                    CBase.Drawing.DrawRect(color, separatorRect, false);
            }
        }

        private void _DrawOctaveLines(SColorF octaveColor)
        {
            int note = (int)Math.Floor(_ViewBottom);

            note += 11 - (note % 12);

            SRectF octaveRect = Rect;
            octaveRect.Y = Rect.Y + (_SemiToneHeight * (_ViewTop - note + 1)) - (_SemiToneHeight / 2);

            do
            {
                octaveRect.H = _SemiToneHeight * 12;
                if (octaveRect.Bottom > Rect.Bottom)
                {
                    octaveRect.H -= octaveRect.Bottom - Rect.Bottom;
                }
                if (octaveRect.Y < Rect.Y)
                {
                    octaveRect.H -= Rect.Y - octaveRect.Y;
                    octaveRect.Y = Rect.Y;
                }
                if (octaveRect.Y < Rect.Bottom)
                {
                    CBase.Drawing.DrawRect(octaveColor, octaveRect, false);
                }
                octaveRect.Y = octaveRect.Y - 2 * _SemiToneHeight * 12;
            }
            while (octaveRect.Bottom > Rect.Y);
        }

        private void _DrawScaleLines(SColorF accidentalsColor)
        {
            int note = (int)Math.Floor(_ViewBottom);

            SRectF accidentalsRect = Rect;
            accidentalsRect.H = _SemiToneHeight;
            accidentalsRect.Y = Rect.Y + (_SemiToneHeight * (_ViewTop - note + 1)) - (_SemiToneHeight / 2);

            do
            {
                if (_AccidentalNotes.IndexOf((note % 12 + 12) % 12) != -1)
                {
                    CTextureRef noteTexture = CBase.Themes.GetSkinTexture(_Theme.SkinFreeStyle, _PartyModeID);
                    CBase.Drawing.DrawTexture(noteTexture, accidentalsRect, accidentalsColor, Rect, false, false);
                }
                accidentalsRect.Y -= accidentalsRect.H;
                note++;
            }
            while (accidentalsRect.Y + accidentalsRect.H > Rect.Y);
        }

        private void _DrawToneHelper(CSongLine line)
        {
            float fadetime = 200;
            // Sub-cent exact tone where available, the last whole tone as fallback for the on-screen note name
            //(e.g. while fading out, the exact tone is no longer valid). The helper is drawn at the octave-mapped
            //tone, clamped to the bar: out-of-range singing pins it to the edge instead of moving the window.
            float rawTone = (CBase.Record.GetToneExact(_Player) >= 0f) ? CBase.Record.GetToneExact(_Player) + 24f : CBase.Record.GetToneAbs(_Player) + 24f;
            int tonePlayer = (int)(rawTone % 12);
            int octavePlayer = (int)(rawTone / 12);

            if (!CBase.Record.ToneValid(_Player))
            {
                if (ToneHelperAlpha == 0)
                    return;

                if (!_ToneHelperTimer.IsRunning)
                    _ToneHelperTimer.Start();

                if (_ToneHelperTimer.ElapsedMilliseconds > fadetime)
                {
                    ToneHelperAlpha = 0;
                    _ToneHelperTimer.Reset();
                }
                else
                {
                    ToneHelperAlpha = 1 - (_ToneHelperTimer.ElapsedMilliseconds / fadetime);
                }
            }
            else
            {
                ToneHelperAlpha = 1;
                _ToneHelperTimer.Reset();
            }

            //The tone helper has a fixed size and must not scale with the window
            float toneHeight = _ToneHeightFixed;

            float toneHelperHeight = toneHeight * 2;

            // The semi-tone to draw at: the sung tone in the target note's octave
            float absTonePlayer = _HelperTone(line);

            CTextureRef toneHelper = CBase.Themes.GetSkinTexture(_Theme.SkinToneHelper, _PartyModeID);
            toneHelper.Rect.W = toneHelper.Rect.W * ((toneHelperHeight) / toneHelper.Rect.H);
            toneHelper.Rect.H = toneHelperHeight;

            float centerY = Rect.Y + _SemiToneHeight * (_ViewTop - absTonePlayer + 1f);
            //The sung pitch does not drive the window, so an out-of-range tone would draw the helper outside
            //the bar: clamp it to the edge so it reads as "pinned at the edge" instead of escaping the panel
            float halfH = toneHelper.Rect.H / 2f;
            if (centerY < Rect.Y + halfH)
                centerY = Rect.Y + halfH;
            if (centerY > Rect.Bottom - halfH)
                centerY = Rect.Bottom - halfH;

            var drawRect = new SRectF(
                Rect.X + _JudgementLine - toneHelper.Rect.W,
                centerY - halfH,
                toneHelper.Rect.W,
                toneHelper.Rect.H,
                Rect.Z
                );

            var color = new SColorF(1, 1, 1, ToneHelperAlpha);

            CBase.Drawing.DrawTexture(toneHelper, drawRect, color, false);

            if (_ShowToneHelperText)
            {
                _CurrentToneText.X = drawRect.X - 3;
                _CurrentToneText.Y = drawRect.Y;
                _CurrentToneText.Color = color;
                _CurrentToneText.Text = _Tone[tonePlayer] + _Octave[octavePlayer];

                _CurrentToneText.Draw();
            }
        }

        /// <summary>
        ///     Spawns the next dot of the voice trail while tone input is still present
        ///     and draws the trail
        /// </summary>
        private void _DrawVoiceTrail(float beat, float trailSpawnX)
        {
            if (CBase.Record.ToneValid(_Player))
            {
                float tone = CBase.Record.GetToneExact(_Player);

                if (tone >= 0f)
                {
                    // The octave context of the current target note (if any), like the tone helper
                    float? targetTone = null;
                    if (_CurrentLine != -1 && _CurrentLine < _Lines.Length)
                    {
                        CSongLine line = _Lines[_CurrentLine];
                        int note = line.FindPreviousNote(CBase.Game.GetCurrentBeat());
                        if (note < 0)
                            note = 0;
                        targetTone = line.Notes[note].Tone;
                    }

                    float size = _ToneHeightFixed - 4 + CBase.Game.GetRandom(800) / 100f;
                    float heightvariance = _ToneHeightFixed / 6;
                    float jitter = -heightvariance + CBase.Game.GetRandom((int)heightvariance * 200) / 100f;
                    float phase = (float)(CBase.Game.GetRandomDouble() * 2f * Math.PI);

                    _VoiceTrail.TrySpawn(beat, _NoteWidth, _MapTrailTone(tone, targetTone), size, jitter, phase);
                }
            }

            _VoiceTrail.Draw(CBase.Themes.GetSkinTexture(_Theme.SkinGoldenStar, _PartyModeID), Rect, trailSpawnX, _NoteWidth, beat, _ViewTop, _SemiToneHeight);
        }

        /// <summary>
        ///     Maps the exact sung tone into the octave context of the current target note (same as the tone
        ///     helper) so the trail dot appears at a musically sensible pitch. The dot is then stored by tone
        ///     and projected onto the current window each draw, so it can drift out of the bar (out-of-range
        ///     singing, or the window having moved on) and is faded by its overlap with the bar instead of
        ///     being carried along with it (see CVoiceTrail.Draw)
        /// </summary>
        private float _MapTrailTone(float tone, float? targetTone)
        {
            if (targetTone.HasValue)
            {
                float target = targetTone.Value;
                while (tone - target > 6)
                    tone -= 12;
                while (tone - target < -6)
                    tone += 12;
            }

            //No clamping into the current window: the dot is drawn where it really is (in the target's
            //octave) and faded by its overlap with the bar, so out-of-range singing reads as the trail
            //fading away at the edge instead of a dot jumping an octave
            return tone;
        }

        private void _DrawNote(SRectF rect, SColorF color, CTextureRef noteBegin, CTextureRef noteMiddle, CTextureRef noteEnd, float factor)
        {
            if (factor <= 0 || rect.X > Rect.X + Rect.W || rect.X + rect.W < Rect.X)
                return;

            //Width-related variables rounded and then floored to prevent 1px gaps in notes
            rect.X = (float)Math.Round(rect.X);
            rect.W = (float)Math.Round(rect.W);

            int dh = (int)((1f - factor) * rect.H / 2);
            int dw = (int)Math.Min(dh, rect.W / 2);

            var noteRect = new SRectF(rect.X + dw, rect.Y + dh, rect.W - 2 * dw, rect.H - 2 * dh, rect.Z);
            var noteBoundary = noteRect;
            if (noteBoundary.X < Rect.X)
            {
                noteBoundary.X = Rect.X;
                noteBoundary.W -= Rect.X - noteBoundary.X;
            }
            if (noteBoundary.X + noteBoundary.W > Rect.X + Rect.W)
            {
                noteBoundary.W -= (noteBoundary.X + noteBoundary.W) - (Rect.X + Rect.W);
            }

            //Width of each of the ends (round parts)
            //Need 2 of them so use minimum
            int endsW = (int)Math.Min(noteRect.H * noteBegin.OrigAspect, noteRect.W / 2);

            CBase.Drawing.DrawTexture(noteBegin, new SRectF(noteRect.X, noteRect.Y, endsW, noteRect.H, noteRect.Z), color, noteBoundary, false, false);

            SRectF middleRect = new SRectF(noteRect.X + endsW, noteRect.Y, noteRect.W - 2 * endsW, noteRect.H, noteRect.Z);

            int midW = (int)Math.Round(noteRect.H * noteMiddle.OrigAspect);

            int midCount = (int)middleRect.W / midW;

            for (int i = 0; i < midCount; ++i)
            {
                CBase.Drawing.DrawTexture(noteMiddle, new SRectF(middleRect.X + (i * midW), noteRect.Y, midW, noteRect.H, noteRect.Z), color, noteBoundary, false, false);
            }

            SRectF lastMidRect = new SRectF(middleRect.X + midCount * midW, noteRect.Y, middleRect.W - (midCount * midW), noteRect.H, noteRect.Z);
            if (lastMidRect.X + lastMidRect.W > Rect.X + Rect.W)
            {
                lastMidRect.W -= (lastMidRect.X + lastMidRect.W) - (Rect.X + Rect.W);
            }
            else if (lastMidRect.X < Rect.X)
            {
                lastMidRect.X = Rect.X;
                lastMidRect.W -= Rect.X - lastMidRect.X;
            }

            CBase.Drawing.DrawTexture(noteMiddle, new SRectF(middleRect.X + (midCount * midW), middleRect.Y, midW, middleRect.H, middleRect.Z), color, lastMidRect, false, false);

            CBase.Drawing.DrawTexture(noteEnd, new SRectF(noteRect.X + noteRect.W - endsW, noteRect.Y, endsW, noteRect.H, noteRect.Z), color, noteBoundary, false, false);
        }

        private void _DrawNoteBase(SRectF rect, SColorF color, float factor)
        {
            CTextureRef noteBegin = CBase.Themes.GetSkinTexture(_Theme.SkinLeft, _PartyModeID);
            CTextureRef noteMiddle = CBase.Themes.GetSkinTexture(_Theme.SkinMiddle, _PartyModeID);
            CTextureRef noteEnd = CBase.Themes.GetSkinTexture(_Theme.SkinRight, _PartyModeID);

            _DrawNote(rect, color, noteBegin, noteMiddle, noteEnd, factor);
        }

        private void _DrawNoteFill(SRectF rect, SColorF color, float factor)
        {

            CTextureRef noteBegin = CBase.Themes.GetSkinTexture(_Theme.SkinFillLeft, _PartyModeID);
            CTextureRef noteMiddle = CBase.Themes.GetSkinTexture(_Theme.SkinFillMiddle, _PartyModeID);
            CTextureRef noteEnd = CBase.Themes.GetSkinTexture(_Theme.SkinFillRight, _PartyModeID);

            _DrawNote(rect, color, noteBegin, noteMiddle, noteEnd, factor);
        }

        private void _DrawNoteBG(SRectF rect, SColorF color)
        {
            const float period = 1500; //[ms]

            if (!_Timer.IsRunning)
                _Timer.Start();

            if (_Timer.ElapsedMilliseconds > period)
                _Timer.Restart();

            float alpha = (float)(Math.Cos(_Timer.ElapsedMilliseconds / period * Math.PI * 2) + 1) / 4 + 0.5f;

            var col = new SColorF(color, color.A * alpha);

            CTextureRef noteBegin = CBase.Themes.GetSkinTexture(_Theme.SkinBackgroundLeft, _PartyModeID);
            CTextureRef noteMiddle = CBase.Themes.GetSkinTexture(_Theme.SkinBackgroundMiddle, _PartyModeID);
            CTextureRef noteEnd = CBase.Themes.GetSkinTexture(_Theme.SkinBackgroundRight, _PartyModeID);

            _DrawNote(rect, col, noteBegin, noteMiddle, noteEnd, 1f);
        }

        private void _DrawFreeStyleNote(CSongNote note, SColorF color)
        {
            SRectF noteRect = _GetNoteRect(note);
            SRectF noteBoundaryRect = _GetNoteBoundary(note);
            noteRect.H = noteBoundaryRect.H = Rect.H;
            noteRect.Y = noteBoundaryRect.Y = Rect.Y;
            CTextureRef noteTexture = CBase.Themes.GetSkinTexture(_Theme.SkinFreeStyle, _PartyModeID);
            CBase.Drawing.DrawTexture(noteTexture, noteRect, color, noteBoundaryRect, false, false);
        }

        private void _DrawNormalNote(CSongNote note, SColorF color)
        {
            SRectF rect = _GetNoteRect(note);
            _DrawNoteBG(rect, color);
            _DrawNoteBase(rect, new SColorF(_NoteBaseColor, _NoteBaseColor.A * Alpha), 1f);
        }

        private void _DrawGoldenNote(CSongNote note, SColorF color)
        {
            SRectF rect = _GetNoteRect(note);
            SColorF goldColor = new SColorF(1, 0.84f, 0, color.A);
            SColorF whiteColor = new SColorF(1, 1, 1, color.A);
            _DrawNoteBG(rect, whiteColor);
            _DrawNoteBase(rect, new SColorF(goldColor, _NoteBaseColor.A * Alpha), 1f);
            _AddGoldenNote(rect);
        }

        private SRectF _GetNoteBoundary(CSongNote note)
        {
            SRectF noteBoundary = _GetNoteRect(note);
            if (noteBoundary.X < Rect.X)
            {
                noteBoundary.X = Rect.X;
                noteBoundary.W -= Rect.X - noteBoundary.X;
            }
            if (noteBoundary.Right > Rect.Right)
            {
                noteBoundary.W -= noteBoundary.Right - Rect.Right;
            }
            return noteBoundary;
        }

        private void _AddGoldenNote(SRectF noteRect)
        {
            var numstars = 1;
            var stars = new CParticleEffect(_PartyModeID, numstars, new SColorF(Color.Yellow), noteRect, Rect, _Theme.SkinGoldenStar, 12, EParticleType.GoldenNoteStar);
            stars.AllMonitors = false;
            _GoldenStars.Add(stars);
        }

        private void _AddFlare(SRectF noteRect)
        {
            var rect = new SRectF(noteRect.Right, noteRect.Y, 0f, noteRect.H, noteRect.Z);

            var flares = new CParticleEffect(_PartyModeID, 15, new SColorF(Color.White), rect, _Theme.SkinGoldenStar, 20, EParticleType.Flare);
            flares.AllMonitors = false;
            _Flares.Add(flares);
        }

        private void _AddPerfectNote(SRectF noteRect)
        {
            CTextureRef noteBegin = CBase.Themes.GetSkinTexture(_Theme.SkinRight, _PartyModeID);
            float dx = noteRect.H * noteBegin.OrigAspect;
            if (2 * dx > noteRect.W)
                dx = noteRect.W / 2;

            SRectF r = new SRectF(noteRect.Right - dx, noteRect.Y, dx * 0.5f, dx * 0.2f, noteRect.Z);

            var stars = new CParticleEffect(_PartyModeID, CBase.Game.GetRandom(2) + 1, new SColorF(Color.White), r, Rect, _Theme.SkinPerfectNoteStart, 35,
                                            EParticleType.PerfNoteStar);
            stars.AllMonitors = false;
            _PerfectNoteEffect.Add(stars);
        }

        private void _AddPerfectLine()
        {
            var twinkle = new CParticleEffect(_PartyModeID, 200, _PlayerColor, Rect, _Theme.SkinGoldenStar, 15, EParticleType.Twinkle);
            twinkle.AllMonitors = false;
            _PerfectLineTwinkle.Add(twinkle);
        }
    }
}