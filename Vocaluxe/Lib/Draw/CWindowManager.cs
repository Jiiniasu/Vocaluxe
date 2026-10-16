#region license
// This file is part of Vocaluxe.
//
// Vocaluxe is free software: you can redistribute it or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Vocaluxe is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See
// the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vocaluxe.Base;
using VKeys = VocaluxeLib.Keys;
using VocaluxeLib.Log;
using GL = OpenTK.Graphics.OpenGL.GL;
using StringName = OpenTK.Graphics.OpenGL.StringName;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     One window per monitor for the multi-window primary path. Slot 0 keeps the context that COpenGL
    ///     created; slots 1..N-1 are added with <see cref="NativeWindowSettings.SharedContext" /> so all N
    ///     windows share a single GL object namespace (textures live once in VRAM) while each window presents
    ///     its own real (MSAA) backbuffer.
    ///     Borderless fullscreen on Windows uses the same +1px workaround as the single-window path
    ///     (CDrawBaseWindows.cs), applied per window.
    ///
    ///     Invariant: the slot list is structurally immutable after the constructor - slots are only added
    ///     there and are never removed or reordered later (teardown closes the windows but keeps the slots).
    ///     All per-slot mutable state lives inside the <see cref="Slot" /> object and is mutated in place.
    ///     This matters because window operations (show, resize, ...) fire <c>Resize</c>/<c>WindowBorder</c>
    ///     events that run re-entrantly into the game's handlers (e.g. <c>COpenGL._DoResize</c>) on the same
    ///     thread; with a structurally stable list those re-entrant updates can never invalidate an in-flight
    ///     enumeration (List{T} throws "Collection was modified" if its version changes mid-enumeration, which
    ///     any indexer write-back of a struct copy would do).
    /// </summary>
    class CWindowManager
    {
        /// <summary>
        ///     One per-monitor window and its bookkeeping. A class (not a struct) so per-slot state (letterbox,
        ///     lost-focus key, restored window state) is mutated in place - never through a list indexer
        ///     write-back (see the class documentation for why that would crash re-entrant enumeration).
        /// </summary>
        public class Slot
        {
            public NativeWindow Window;
            public int PanelIndex;
            public bool SharesPrimaryContext;
            public Vector2i RestoreLocation;
            public Vector2i RestoreSize;
            public WindowBorder RestoreBorder;

            /// <summary>
            ///     Last key pressed on this window; flushed as KeyUp if focus is lost before it is released
            ///     (plan v3 §6 focus-loss flush).
            /// </summary>
            public VKeys LostFocusKey;

            /// <summary>Letterbox viewport rect inside the window (client px), computed by <c>COpenGL._DoResize</c>.</summary>
            public int LetterX;
            public int LetterY;
            public int LetterW;
            public int LetterH;
        }

        private readonly List<Slot> _Slots = new List<Slot>();
        private readonly int _PanelW;
        private readonly int _PanelH;

        public int Count
        {
            get { return _Slots.Count; }
        }

        public NativeWindow Primary
        {
            get { return _Slots[0].Window; }
        }

        public List<Slot> Slots
        {
            get { return _Slots; }
        }

        /// <summary>
        ///     Set by a monitor hotplug event; a layout rebuild is requested and applied at the frame boundary
        ///     (never mid-frame, plan v3 §7).
        /// </summary>
        public bool RebuildPending { get; private set; }

        /// <summary>
        ///     Registers the primary (already created) window as slot 0 and creates N-1 more windows sharing
        ///     its context. <paramref name="panelCount" /> is the number of panels; the window size is forced
        ///     to one panel from <paramref name="template" />.
        /// </summary>
        public CWindowManager(NativeWindow primary, int panelCount, NativeWindowSettings template)
        {
            if (panelCount < 1)
                throw new ArgumentException("panelCount must be >= 1", "panelCount");

            _PanelW = template.ClientSize.X;
            _PanelH = template.ClientSize.Y;

            Slot slot0 = new Slot
            {
                Window = primary,
                PanelIndex = 0,
                SharesPrimaryContext = false
            };
            _Slots.Add(slot0);

            for (int i = 1; i < panelCount; i++)
            {
                NativeWindow win;
                try
                {
                    template.ClientSize = new Vector2i(_PanelW, _PanelH);
                    template.StartVisible = false;
                    template.SharedContext = primary.Context;
                    win = new NativeWindow(template);
                }
                catch
                {
                    // Close the siblings that did get created (no leaking of partially built shared-context
                    // windows) and rethrow.
                    for (int j = _Slots.Count - 1; j >= 1; j--)
                    {
                        try { _Slots[j].Window.Close(); } catch { }
                    }
                    throw;
                }

                _Slots.Add(new Slot
                           {
                               Window = win,
                               PanelIndex = i,
                               SharesPrimaryContext = true
                           });
            }

            // OpenTK 4 raises a single event for both connect and disconnect (MonitorEventArgs.IsConnected
            // tells which); only a layout-rebuild flag is needed - applied at the frame boundary.
            Monitors.OnMonitorConnected += _OnMonitorChanged;
        }

        /// <summary>
        ///     F1 (plan v3 §5), pure part: a shared context cannot span two GPUs, so the session must fall
        ///     back to the single giant window as soon as any two renderer strings differ (compared
        ///     case-folded and trimmed). Extracted so the decision table is unit-testable without contexts.
        /// </summary>
        public static bool RequiresSingleWindow(IEnumerable<string> renderers)
        {
            string known = null;
            foreach (string raw in renderers)
            {
                string renderer = (raw ?? "").Trim().ToLowerInvariant();
                if (known == null)
                {
                    known = renderer;
                    continue;
                }
                if (renderer != known)
                    return true;
            }
            return false;
        }

        /// <summary>
        ///     F1 (plan v3 §5): probes the per-window <see cref="StringName.Renderer" /> strings and returns
        ///     true if the session must fall back to the single giant window.
        /// </summary>
        public bool ProbeRenderers()
        {
            List<string> renderers = new List<string>(_Slots.Count);
            for (int i = 0; i < _Slots.Count; i++)
            {
                Slot s = _Slots[i];
                s.Window.MakeCurrent();
                string renderer = GL.GetString(StringName.Renderer) ?? "";
                renderers.Add(renderer);
                CLog.Information("Multi-window: GL renderer '" + renderer + "' (panel " + s.PanelIndex + ")");
            }

            bool single = RequiresSingleWindow(renderers);
            if (single)
                CLog.Warning("Multi-window: mixed GPUs detected. Falling back to the single giant window for this session.");
            return single;
        }

        /// <summary>
        ///     Monitor assignment, pure part (plan v3 §4.1): slot i wraps round-robin over the monitors, so
        ///     extra panels stack on the monitor of slot i % monitorCount. Returns -1 when no monitors are
        ///     available (the caller centers the window on the work area instead). Extracted so the
        ///     assignment (incl. stacking) is unit-testable without windows.
        /// </summary>
        public static int MonitorForSlot(int slot, int monitorCount)
        {
            return monitorCount > 0 ? slot % monitorCount : -1;
        }

        /// <summary>
        ///     Windowed cascade offset (plan v3 §4.1) for a slot stacked above the first panels of its
        ///     monitor, clamped so the panel stays on the monitor. Zero for unstacked slots or when no
        ///     monitors are available.
        /// </summary>
        public static Vector2i StackOffset(int slot, int monitorCount)
        {
            if (monitorCount <= 0)
                return Vector2i.Zero;
            int stacked = slot / monitorCount;
            return new Vector2i(Math.Min(stacked * 64, 448), Math.Min(stacked * 64, 384));
        }

        /// <summary>
        ///     Windowed placement, pure part (plan v3 §4.1): the panel centered in the monitor's client
        ///     area, shifted by the cascade <paramref name="offset" />, clamped to the client area.
        /// </summary>
        public static Vector2i CenteredPanelLocation(Vector2i clientMin, Vector2i clientSize,
                                                     Vector2i panelSize, Vector2i offset)
        {
            int x = Math.Max(clientMin.X,
                             clientMin.X + (clientSize.X - panelSize.X) / 2 + offset.X);
            int y = Math.Max(clientMin.Y,
                             clientMin.Y + (clientSize.Y - panelSize.Y) / 2 + offset.Y);
            return new Vector2i(x, y);
        }

        /// <summary>
        ///     Places every window: slot i is assigned to monitor i % monitorCount (extra panels cascade on
        ///     the same monitor). Fullscreen (Windows): borderless and 1px taller per monitor client area
        ///     (the existing DWM-freeze workaround, CDrawBaseWindows.cs); elsewhere: native fullscreen.
        ///     Windowed: centered panel size on the assigned monitor (or the work area when headless).
        ///     NOTE: fires Resize/Border events per window; any re-entrant handler must be safe to run
        ///     mid-iteration (slot state is mutated in place, see class documentation).
        /// </summary>
        public void ApplyLayout(bool fullscreen)
        {
            List<MonitorInfo> monitors;
            try
            {
                monitors = Monitors.GetMonitors();
            }
            catch (Exception e)
            {
                // Headless/exotic display servers can fail this query (the spike hit it); fall back to
                // plain centered windows rather than killing startup.
                CLog.Warning("Monitors.GetMonitors failed, centering windows on the work area: " + e);
                monitors = new List<MonitorInfo>();
            }
            RebuildPending = false;

            for (int i = 0; i < _Slots.Count; i++)
            {
                Slot s = _Slots[i];
                int monitorIndex = MonitorForSlot(i, monitors.Count);
                MonitorInfo monitor = (monitorIndex >= 0) ? monitors[monitorIndex] : null;

                if (monitor != null && fullscreen)
                {
#if WIN
                    // Same workaround as the single-window path of CDrawBaseGlfw: borderless and 1px taller
                    // than the client area, so exclusive promotion cannot freeze the window.
                    s.Window.WindowBorder = WindowBorder.Hidden;
                    s.Window.WindowState = WindowState.Normal;
                    s.Window.Location = monitor.ClientArea.Min;
                    s.Window.ClientSize = new Vector2i(monitor.ClientArea.Size.X, monitor.ClientArea.Size.Y + 1);
#else
                    s.Window.WindowState = WindowState.Fullscreen;
#endif
                }
                else if (monitor != null)
                {
                    // Extra panels cascade on the same monitor so they stay visible in windowed mode
                    Vector2i offset = StackOffset(i, monitors.Count);
                    Vector2i location = CenteredPanelLocation(monitor.ClientArea.Min,
                                                              monitor.ClientArea.Size,
                                                              new Vector2i(_PanelW, _PanelH),
                                                              offset);

                    s.Window.WindowBorder = WindowBorder.Resizable;
                    s.Window.WindowState = WindowState.Normal;
                    s.Window.ClientSize = new Vector2i(_PanelW, _PanelH);
                    s.Window.Location = location;
                }
                else
                {
                    s.Window.WindowBorder = WindowBorder.Resizable;
                    s.Window.WindowState = WindowState.Normal;
                    s.Window.ClientSize = new Vector2i(_PanelW, _PanelH);
                    s.Window.CenterWindow();
                }
            }

            CLog.Information("Multi-window layout: " + _Slots.Count + " window(s), " + monitors.Count + " monitor(s)" +
                             (fullscreen ? ", fullscreen" : ", windowed") + ".");
        }

        /// <summary>Enters (per-slot) fullscreen, saving the windowed state for restore.</summary>
        public void EnterFullScreen()
        {
            for (int i = 0; i < _Slots.Count; i++)
            {
                Slot s = _Slots[i];
                s.RestoreLocation = s.Window.Location;
                s.RestoreSize = s.Window.ClientSize;
                s.RestoreBorder = s.Window.WindowBorder;
            }

            ApplyLayout(true);
        }

        /// <summary>Restores the pre-fullscreen per-slot state.</summary>
        public void LeaveFullScreen()
        {
            for (int i = 0; i < _Slots.Count; i++)
            {
                Slot s = _Slots[i];
                s.Window.WindowState = WindowState.Normal;
                s.Window.WindowBorder = s.RestoreBorder;
                s.Window.ClientSize = s.RestoreSize;
                s.Window.Location = s.RestoreLocation;
            }

            CLog.Information("Multi-window: left fullscreen");
        }

        /// <summary>
        ///     Shows every window. Showing a window fires its Resize/Visibility events, which run re-entrant
        ///     handlers - iterate by index (no in-flight list enumerator) and mutate slot state in place only.
        /// </summary>
        public void ShowAll()
        {
            for (int i = 0; i < _Slots.Count; i++)
                _Slots[i].Window.IsVisible = true;
        }

        public void HideAll()
        {
            for (int i = 0; i < _Slots.Count; i++)
                _Slots[i].Window.IsVisible = false;
        }

        /// <summary>
        ///     Closes the windows (children before the primary) and detaches the hotplug hooks. With
        ///     <paramref name="keepPrimary" /> the primary (slot 0) stays open - used by the F1 downgrade,
        ///     which turns it into the single giant window.
        /// </summary>
        public void CloseAll(bool keepPrimary = false)
        {
            Monitors.OnMonitorConnected -= _OnMonitorChanged;

            for (int i = _Slots.Count - 1; i >= (keepPrimary ? 1 : 0); i--)
                try
                {
                    _Slots[i].Window.Close();
                }
                catch (Exception e)
                {
                    CLog.Warning("Closing window " + _Slots[i].PanelIndex + " failed: " + e);
                }
        }

        /// <summary>Remembers the last key pressed on a window (flushed as KeyUp on focus loss, plan v3 §6).</summary>
        public void SetLostFocusKey(NativeWindow window, VKeys key)
        {
            for (int i = 0; i < _Slots.Count; i++)
            {
                if (!ReferenceEquals(_Slots[i].Window, window))
                    continue;
                _Slots[i].LostFocusKey = key;
                return;
            }
        }

        /// <summary>Returns (and clears) the key still held when a window lost focus; None when nothing to flush.</summary>
        public VKeys ConsumeLostFocusKey(NativeWindow window)
        {
            for (int i = 0; i < _Slots.Count; i++)
            {
                if (!ReferenceEquals(_Slots[i].Window, window))
                    continue;
                VKeys key = _Slots[i].LostFocusKey;
                _Slots[i].LostFocusKey = VKeys.None;
                return key;
            }
            return VKeys.None;
        }

        private void _OnMonitorChanged(MonitorEventArgs e)
        {
            // Hotplug (plan v3 §7): only flag; the relayout is applied at the frame boundary by MainLoop so it
            // can never run mid-frame or mid-teardown.
            RebuildPending = true;
        }
    }
}
