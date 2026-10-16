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
        public readonly SRectF Rect;
        private readonly SColorF _PlayerColor;
        private readonly SColorF _NoteLinesColor;
        private readonly SColorF _NoteBaseColor;
        public float Alpha = 1;
        private float ToneHelperAlpha;
        private readonly CSongLine[] _Lines;
        private CText _CurrentToneText = new CText(0, 0, 0, 14, 50, EAlignment.Right, EStyle.Bold, "Outline", new SColorF(Color.White), String.Empty);

        /// <summary>
        ///     Base (home) span of the pitch window in semi-tones
        /// </summary>
        private float _HomeSpan;
        /// <summary>
        ///     Base (home) center of the pitch window in semi-tones
        /// </summary>
        private float _HomeCenter;
        /// <summary>
        ///     Planned transitions of the pitch window, in beat order (empty list = static window)
        /// </summary>
        private readonly List<SViewTransition> _ViewPlan = new List<SViewTransition>();

        private struct SViewTransition
        {
            public float BeatFrom;
            public float BeatTo;
            public float SpanFrom;
            public float SpanTo;
            public float CenterFrom;
            public float CenterTo;
        }

        private struct SLineWindow
        {
            public float StartBeat;
            public float EndBeat;
            public float MinTone;
            public float MaxTone;
        }

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
        private readonly Stopwatch _TrailSpawnTimer = new Stopwatch();
        private readonly List<SRectF> _VoiceTrail = new List<SRectF>();
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
            float refToneHeight = Rect.H / CBase.Settings.GetNumNoteLines();
            _NoteWidth = refToneHeight * 2f * CBase.Config.GetNoteSpeed();

            // Home window: the whole voice if the song fits within two octaves,
            // otherwise the preferred two-octave window (never under one octave)
            _HomeSpan = Math.Max(12f, Math.Min(24f, _RangeSemiToneMax - _RangeSemiToneMin));
            _HomeCenter = (_RangeSemiToneMin + _RangeSemiToneMax) / 2f;

            //Songs spanning more than two octaves get a plan of gentle
            //transitions following the sections of the song
            if (_RangeSemiToneMax - _RangeSemiToneMin > 24f)
                _BuildViewPlan();

            if (playerData.ToneHelperText == EOffOn.TR_CONFIG_ON)
            {
                _ShowToneHelperText = true;
                _CurrentToneText.Z = Rect.Z;
                _CurrentToneText.AllMonitors = false;
            }
        }

        public void SetLine(int line)
        {
            if (_CurrentLine == line)
                return;
            _CurrentLine = line;

        }

        public void Draw()
        {
            _ApplyView(CBase.Game.GetCurrentBeatF());

            _DrawJudgeLine(new SColorF(_NoteLinesColor, _NoteLinesColor.A));

            if (_CurrentLine == -1 || _CurrentLine >= _Lines.Length)
                return;

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

            if (CBase.Config.GetDrawToneHelper() == EOffOn.TR_CONFIG_ON)
            {
                for (int i = 0; i < _VoiceTrail.Count; i++)
                {
                    SRectF voice = _VoiceTrail[i];
                    float currentBeatF = CBase.Game.GetCurrentBeatF();
                    voice.X -= (currentBeatF - _LastBeatF) * _NoteWidth;
                    _VoiceTrail[i] = voice;
                    CBase.Drawing.DrawTexture(CBase.Themes.GetSkinTexture(_Theme.SkinGoldenStar, _PartyModeID), _VoiceTrail[i], new SColorF(Color.White, 0.6f + (CBase.Game.GetRandom(800) / 1000)), Rect, false, false);
                }
                _DrawToneHelper(line);
            }

            if (sungLines.Count > 0 && sungLines[sungLines.Count - 1].PerfectLine)
            {
                _AddPerfectLine();
                sungLines[sungLines.Count - 1].PerfectLine = false;
            }

            _Flares.RemoveAll(el => !el.IsAlive);
            _PerfectNoteEffect.RemoveAll(el => !el.IsAlive);
            _PerfectLineTwinkle.RemoveAll(el => !el.IsAlive);
            _GoldenStars.RemoveAll(el => !el.IsAlive);
            _VoiceTrail.RemoveAll(el => el.Right < Rect.X);

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
                perfnote.X -= (CBase.Game.GetCurrentBeatF() - _LastBeatF) * _NoteWidth;
                perfnote.Draw();
            }

            _LastBeatF = CBase.Game.GetCurrentBeatF();
        }

        /// <summary>
        ///     Plans the pitch window of a song that spans more than two octaves:
        ///     the window follows the sections of the song (1-2 octaves at a time),
        ///     moves gently when the register changes, and expands only when the
        ///     material on the lane itself spans more than two octaves
        /// </summary>
        private void _BuildViewPlan()
        {
            const float BandMinSemi = 12f;       //floor: one octave
            const float BandMaxSemi = 24f;       //soft ceiling: two octaves
            const float MoveSemi = 4f;           //dead zone: the window stays still for smaller changes
            const float GlideSemiPerSec = 4f;    //glide speed of transitions (~2 note lines per second)
            const float GlideMinSec = 0.5f;
            const float GlideMaxSec = 4f;
            const float HeadroomSemi = 4f;       //2 semis of breathing room on each side

            float voiceSpan = Math.Max(_RangeSemiToneMax - _RangeSemiToneMin, 2f);
            float voiceCenter = (_RangeSemiToneMin + _RangeSemiToneMax) / 2f;
            float bpm = Math.Max(CBase.Game.GetSong().BPM, 1f);
            float horizon = Rect.W / _NoteWidth;         //beats from the play line to the far edge
            float behind = _JudgementLine / _NoteWidth;  //beats a note stays on the lane after being played

            List<SLineWindow> lines = new List<SLineWindow>();
            foreach (CSongLine line in _Lines)
            {
                if (line.NoteCount == 0 || line.MinNote == int.MaxValue)
                    continue;
                lines.Add(new SLineWindow
                {
                    StartBeat = line.StartBeat,
                    EndBeat = line.EndBeat,
                    MinTone = line.MinNote,
                    MaxTone = line.MaxNote
                });
            }

            if (lines.Count == 0)
                return;

            //Events: a line entering (needing room) or leaving (freeing room) the lane
            var events = new List<KeyValuePair<float, int>>();
            for (int i = 0; i < lines.Count; i++)
            {
                events.Add(new KeyValuePair<float, int>(lines[i].StartBeat - horizon, i << 1));
                events.Add(new KeyValuePair<float, int>(lines[i].EndBeat + behind, (i << 1) | 1));
            }
            events.Sort((a, b) => a.Key.CompareTo(b.Key));

            //Sweep and sample what is on the lane at every event
            bool[] active = new bool[lines.Count];
            int activeCount = 0;
            List<float> times = new List<float>();
            List<float> los = new List<float>();
            List<float> his = new List<float>();
            List<bool> entryFlags = new List<bool>();
            for (int i = 0; i < events.Count;)
            {
                float beat = events[i].Key;
                int j = i;
                bool hasEntry = false;
                while (j < events.Count && events[j].Key == beat)
                {
                    int idx = events[j].Value >> 1;
                    if ((events[j].Value & 1) == 0)
                    {
                        hasEntry = true;
                        if (!active[idx])
                        {
                            active[idx] = true;
                            activeCount++;
                        }
                    }
                    else
                    {
                        if (active[idx])
                        {
                            active[idx] = false;
                            activeCount--;
                        }
                    }
                    j++;
                }
                i = j;

                if (activeCount == 0)
                    continue; //the lane is empty: nothing to frame

                float lo = float.MaxValue;
                float hi = float.MinValue;
                for (int k = 0; k < lines.Count; k++)
                {
                    if (!active[k])
                        continue;
                    if (lines[k].MinTone < lo)
                        lo = lines[k].MinTone;
                    if (lines[k].MaxTone > hi)
                        hi = lines[k].MaxTone;
                }
                times.Add(beat);
                los.Add(lo);
                his.Add(hi);
                entryFlags.Add(hasEntry);
            }

            if (times.Count == 0)
                return;

            //Plan a transition for every material change of the active span
            float curSpan = _HomeSpan;
            float curCenter = _HomeCenter;
            float prevEnd = float.NegativeInfinity;

            for (int i = 0; i < times.Count; i++)
            {
                float raw = his[i] - los[i] + HeadroomSemi;
                float span = (raw <= BandMaxSemi)
                    ? Math.Max(BandMinSemi, raw)
                    : Math.Min(raw, voiceSpan);
                float center = (los[i] + his[i]) / 2f;
                if (span >= voiceSpan)
                    center = voiceCenter;

                //The dead zone keeps the window still for small changes, but a hold
                //is only legal while the current window still covers the active notes
                //(otherwise content could sit past the window edge)
                float curBottom = curCenter - curSpan / 2f;
                float curTop = curCenter + curSpan / 2f;
                if (los[i] >= curBottom && his[i] <= curTop
                    && Math.Abs(span - curSpan) < MoveSemi && Math.Abs(center - curCenter) < MoveSemi)
                    continue;

                float distance = Math.Abs(span - curSpan) + Math.Abs(center - curCenter);
                float duration = Math.Min(Math.Max(distance / GlideSemiPerSec, GlideMinSec), GlideMaxSec);
                float durationBeats = duration * bpm / 60f;

                float start;
                if (entryFlags[i])
                {
                    //New notes need the room: the window must be ready when they appear
                    start = times[i] - durationBeats;
                    if (start < prevEnd)
                        start = prevEnd;
                    if (start >= times[i])
                        start = times[i]; //no room to glide: settle instantly
                    float end = times[i];
                    _ViewPlan.Add(new SViewTransition
                    {
                        BeatFrom = start,
                        BeatTo = end,
                        SpanFrom = curSpan,
                        SpanTo = span,
                        CenterFrom = curCenter,
                        CenterTo = center
                    });
                    prevEnd = end;
                }
                else
                {
                    //The material just left the lane: relax once it is gone
                    start = Math.Max(times[i], prevEnd);
                    float end = start + durationBeats;
                    _ViewPlan.Add(new SViewTransition
                    {
                        BeatFrom = start,
                        BeatTo = end,
                        SpanFrom = curSpan,
                        SpanTo = span,
                        CenterFrom = curCenter,
                        CenterTo = center
                    });
                    prevEnd = end;
                }

                curSpan = span;
                curCenter = center;
            }
        }

        /// <summary>
        ///     Applies the planned pitch window for the current beat (home window,
        ///     or one of the planned transitions)
        /// </summary>
        private void _ApplyView(float beat)
        {
            float span = _HomeSpan;
            float center = _HomeCenter;

            foreach (SViewTransition transition in _ViewPlan)
            {
                if (beat <= transition.BeatFrom)
                    break;
                float progress = (transition.BeatTo - transition.BeatFrom) > 0.001f
                    ? (beat - transition.BeatFrom) / (transition.BeatTo - transition.BeatFrom)
                    : 1f;
                if (progress < 0f)
                    progress = 0f;
                if (progress > 1f)
                    progress = 1f;
                //Cosine ease: zero velocity at both ends, smooth middle
                float ease = 0.5f * (1f - (float)Math.Cos(Math.PI * progress));
                span = transition.SpanFrom + (transition.SpanTo - transition.SpanFrom) * ease;
                center = transition.CenterFrom + (transition.CenterTo - transition.CenterFrom) * ease;
                if (beat < transition.BeatTo)
                    break;
            }

            _ViewTop = center + span / 2f;
            _ViewBottom = center - span / 2f;
            _SemiToneHeight = Rect.H / span;
            _ToneHeight = _SemiToneHeight * 2f;
            _AddNoteHeight = _ToneHeight / 2f * (2f - _Difficulty);
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
            int absTonePlayer = CBase.Record.GetToneAbs(_Player) + 24;
            int tonePlayer = absTonePlayer % 12;
            int octavePlayer = absTonePlayer / 12;

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
            float toneHeight = Rect.H / CBase.Settings.GetNumNoteLines();

            float toneHelperHeight = toneHeight * 2;

            int note = line.FindPreviousNote(CBase.Game.GetCurrentBeat());

            if (note < 0)
                note = 0;

            int tone = line.Notes[note].Tone;

            // Bring player tone within an octave to the target note

            while (absTonePlayer - tone < -6)
                absTonePlayer += 12;

            while (absTonePlayer - tone > 6)
                absTonePlayer -= 12;

            // Shift player tone an octave if out of range

            if (absTonePlayer > _RangeSemiToneMax)
                absTonePlayer -= 12;

            if (absTonePlayer < _RangeSemiToneMin)
                absTonePlayer += 12;

            CTextureRef toneHelper = CBase.Themes.GetSkinTexture(_Theme.SkinToneHelper, _PartyModeID);
            toneHelper.Rect.W = toneHelper.Rect.W * ((toneHelperHeight) / toneHelper.Rect.H);
            toneHelper.Rect.H = toneHelperHeight;

            var drawRect = new SRectF(
                Rect.X + _JudgementLine - toneHelper.Rect.W,
                Rect.Y + (_SemiToneHeight * (_ViewTop - absTonePlayer + 1)) - (toneHelper.Rect.H / 2),
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

            if (!_TrailSpawnTimer.IsRunning)
                _TrailSpawnTimer.Start();

            if (CBase.Record.ToneValid(_Player) && _TrailSpawnTimer.ElapsedMilliseconds > 10)
            {
                var size = toneHeight - 4 + (CBase.Game.GetRandom(800) / 100);
                var heightvariance = toneHeight / 6;
                var voiceTrailRect = new SRectF(
                    Rect.X + _JudgementLine - toneHeight / 2,
                    (Rect.Y + (_SemiToneHeight * (_ViewTop - absTonePlayer + 1)) - (size / 2)) + (-heightvariance + (CBase.Game.GetRandom((int)heightvariance * 200) / 100)),
                    size,
                    size,
                    Rect.Z);
                _VoiceTrail.Add(voiceTrailRect);
                _TrailSpawnTimer.Reset();
            }
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