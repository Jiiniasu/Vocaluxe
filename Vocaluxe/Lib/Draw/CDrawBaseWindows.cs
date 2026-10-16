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

using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.ComponentModel;
using System.Diagnostics;
using Vocaluxe.Base;
using VocaluxeLib.Log;
using GlfwKeys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;
using VKeys = VocaluxeLib.Keys;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     Cross-platform window host built on an OpenTK 4 <see cref="NativeWindow" /> (GLFW backend).
    ///     Replaces the former WinForms host so the OpenGL renderer runs natively on Windows, Linux and macOS.
    ///     Concrete drivers (e.g. COpenGL) create <see cref="_Window" /> with the desired GL context settings;
    ///     this base wires window/input events into the game's <c>_Keys</c>/<c>_Mouse</c> queues and owns fullscreen.
    ///     In multi-window mode (<see cref="_Windows" /> != null, plan v3 §4) the same wiring is applied to every
    ///     per-monitor window; single-window mode (<see cref="_Windows" /> == null) behaves exactly as before.
    /// </summary>
    abstract class CDrawBaseGlfw<TTextureType> : CDrawBase<TTextureType> where TTextureType : CTextureBase, IDisposable
    {
        protected NativeWindow _Window;

        // Single-window-mode fullscreen restore state (multi-window mode keeps its own per-slot copies in
        // CWindowManager.Slot).
        private Vector2i _RestoreLocation;
        private Vector2i _RestoreSize;
        private WindowBorder _RestoreBorder;

        /// <summary>
        ///     The multi-window manager (plan v3 §4): one window per panel. Null in single-window mode,
        ///     where <see cref="_Window" /> is the one giant window of today.
        /// </summary>
        protected CWindowManager _Windows;

        public override void Close()
        {
            base.Close();
            try
            {
                if (_MultiWindow())
                    _Windows.CloseAll();
                else
                    _Window?.Close();
            }
            catch { }
        }

        protected override void _EnterFullScreen()
        {
            Debug.Assert(!_Fullscreen);
            _Fullscreen = true;

            if (_MultiWindow())
            {
                _Windows.EnterFullScreen();
                _DoResize();
                return;
            }

            _RestoreLocation = _Window.Location;
            _RestoreSize = _Window.ClientSize;
            _RestoreBorder = _Window.WindowBorder;

#if WIN
            // Windows: any window that EXACTLY covers a monitor (exclusive GLFW WindowState.Fullscreen
            // OR a borderless window sized to the monitor) gets promoted by Windows to fullscreen-
            // exclusive present, whose path freezes our manually-driven render loop — SwapBuffers keeps
            // succeeding but the visible front buffer never updates (app looks stuck on the first
            // frame). Work around it with a borderless window sized 1px taller than the monitor: it
            // still looks fullscreen (the extra row is off-screen) but stays on the composited (DWM)
            // present path, which works. Linux/macOS keep native fullscreen.
            try
            {
                MonitorInfo monitor = Monitors.GetMonitorFromWindow(_Window);
                Vector2i size = monitor.ClientArea.Size;
                _Window.WindowBorder = WindowBorder.Hidden;
                _Window.WindowState = WindowState.Normal;
                _Window.Location = monitor.ClientArea.Min;
                _Window.ClientSize = new Vector2i(size.X, size.Y + 1);
                CLog.Information("Windows borderless fullscreen: " + size.X + "x" + size.Y + " (+1px to stay composited)");
            }
            catch (Exception e)
            {
                // Never let fullscreen setup kill startup — fall back to a normal maximized window.
                CLog.Error("Borderless fullscreen setup failed, falling back to windowed: " + e);
                _Window.WindowBorder = _RestoreBorder;
                _Window.WindowState = WindowState.Maximized;
                _Fullscreen = false;
            }
#else
            _Window.WindowState = WindowState.Fullscreen;
#endif
            _DoResize();
        }

        protected override void _LeaveFullScreen()
        {
            Debug.Assert(_Fullscreen);
            _Fullscreen = false;

            if (_MultiWindow())
            {
                _Windows.LeaveFullScreen();
                _DoResize();
                return;
            }

            _Window.WindowState = WindowState.Normal;
            _Window.WindowBorder = _RestoreBorder;
            _Window.ClientSize = _RestoreSize;
            _Window.Location = _RestoreLocation;
        }

        #region multi-window plumbing (plan v3 §4)
        protected override int _GetPanelCount()
        {
            return _Windows == null ? 1 : _Windows.Count;
        }

        protected override bool _MultiWindow()
        {
            return _Windows != null;
        }

        /// <summary>
        ///     Single-window mode letterboxes to the whole canvas (RenderW*NumScreens); multi-window mode
        ///     letterboxes each window to one panel (RenderW x RenderH).
        /// </summary>
        protected override float _WindowAspect()
        {
            return _MultiWindow() ? CSettings.RenderW / (float)CSettings.RenderH : CSettings.GetRenderAspect();
        }

        protected override bool _WantRelayout()
        {
            return _Windows != null && _Windows.RebuildPending;
        }

        protected override void _ApplyRelayout()
        {
            if (_Windows == null)
                return;
            _Windows.ApplyLayout(_Fullscreen);
            _DoResize(); // recompute the per-slot letterbox from the new window sizes
        }

        protected override void _ProcessEvents()
        {
            NativeWindow.ProcessWindowEvents(false);

            // Reliably end the main loop when the user closes any window. The Closing event alone is not
            // dependable with a manually-driven OpenTK 4 NativeWindow, so also poll the GLFW "should close"
            // flag every frame. Once _Run is false, CDraw.MainLoop returns and Program._CloseProgram shuts
            // everything down (Environment.Exit).
            if (_MultiWindow())
            {
                foreach (CWindowManager.Slot s in _Windows.Slots)
                    if (s.Window.IsExiting)
                        _Run = false;
                return;
            }

            if (_Window.IsExiting)
                _Run = false;
        }
        #endregion

        #region window/input event handlers
        private void _OnClosing(CancelEventArgs e)
        {
            _Run = false;
        }

        protected virtual void _OnResize(ResizeEventArgs e)
        {
            _DoResize();
        }

        private void _OnMouseMove(NativeWindow win, MouseMoveEventArgs e)
        {
            int x, y;
            _MapPosition(win, e.X, e.Y, out x, out y);
            _Mouse.MouseMove(x, y,
                             win.IsMouseButtonDown(MouseButton.Left),
                             win.IsMouseButtonDown(MouseButton.Right),
                             win.IsMouseButtonDown(MouseButton.Middle),
                             _Shift(win), _Alt(win), _Ctrl(win));
        }

        private void _OnMouseWheel(NativeWindow win, MouseWheelEventArgs e)
        {
            Vector2 p = win.MousePosition;
            int x, y;
            _MapPosition(win, p.X, p.Y, out x, out y);
            // OpenTK reports the wheel in notches; CMouse expects WinForms-style 120-per-notch deltas.
            _Mouse.MouseWheel(x, y, (int)(e.OffsetY * 120), _Shift(win), _Alt(win), _Ctrl(win));
        }

        private void _OnMouseDown(NativeWindow win, MouseButtonEventArgs e)
        {
            Vector2 p = win.MousePosition;
            int x, y;
            _MapPosition(win, p.X, p.Y, out x, out y);
            _Mouse.MouseDown(x, y,
                             e.Button == MouseButton.Left,
                             e.Button == MouseButton.Right,
                             e.Button == MouseButton.Middle,
                             _Shift(win), _Alt(win), _Ctrl(win));
        }

        private void _OnMouseUp(NativeWindow win, MouseButtonEventArgs e)
        {
            Vector2 p = win.MousePosition;
            int x, y;
            _MapPosition(win, p.X, p.Y, out x, out y);
            _Mouse.MouseUp(x, y,
                           e.Button == MouseButton.Left,
                           e.Button == MouseButton.Right,
                           e.Button == MouseButton.Middle,
                           _Shift(win), _Alt(win), _Ctrl(win));
        }

        private void _OnKeyDown(KeyboardKeyEventArgs e)
        {
            _Keys.KeyDown(_MapKey(e.Key), e.Shift, e.Alt, e.Control);
        }

        private void _OnKeyUp(KeyboardKeyEventArgs e)
        {
            _Keys.KeyUp(_MapKey(e.Key), e.Shift, e.Alt, e.Control);
        }

        private void _OnTextInput(NativeWindow win, TextInputEventArgs e)
        {
            _Keys.KeyPress((char)e.Unicode, _Shift(win), _Alt(win), _Ctrl(win));
        }

        private void _OnLostFocus(NativeWindow win, FocusedChangedEventArgs e)
        {
            if (e.IsFocused || _Windows == null)
                return;
            VKeys lost = _Windows.ConsumeLostFocusKey(win);
            if (lost != VKeys.None)
            {
                // Focus was lost while a key was still down: flush it so it cannot stick (plan v3 §6).
                _Keys.KeyUp(lost, false, false, false);
            }
        }

        /// <summary>
        ///     Maps window-local mouse coordinates into canvas coordinates (plan v3 §6): single-window mode
        ///     passes the raw window pixels through (CUtility._Add scales them to the canvas exactly as
        ///     before); multi-window mode maps through the slot's letterbox rect into panel-local
        ///     coordinates (0..RenderW), where CUtility._Add's scaling becomes an identity.
        /// </summary>
        private void _MapPosition(NativeWindow win, float lx, float ly, out int x, out int y)
        {
            x = (int)lx;
            y = (int)ly;
            if (_Windows == null)
                return;
            foreach (CWindowManager.Slot s in _Windows.Slots)
            {
                if (!ReferenceEquals(s.Window, win))
                    continue;
                x = Unletterbox(x, s.LetterX, s.LetterW, CSettings.RenderW);
                y = Unletterbox(y, s.LetterY, s.LetterH, CSettings.RenderH);
                return;
            }
        }

        /// <summary>
        ///     Letterbox-undo, pure part (plan v3 §6): window-local pixel into panel-local canvas coordinates
        ///     through the slot's letterbox rect (identity when the letterbox is degenerate, which in
        ///     single-window mode is then scaled to the canvas by <c>CMouse</c> exactly as before).
        ///     Extracted so the mapping is unit-testable without windows.
        /// </summary>
        public static int Unletterbox(int pixel, int letterOrigin, int letterSize, int renderSize)
        {
            return letterSize > 0
                ? (int)((pixel - letterOrigin) * (float)renderSize / letterSize)
                : pixel;
        }

        private bool _Shift(NativeWindow win)
        {
            return win.IsKeyDown(GlfwKeys.LeftShift) || win.IsKeyDown(GlfwKeys.RightShift);
        }

        private bool _Alt(NativeWindow win)
        {
            return win.IsKeyDown(GlfwKeys.LeftAlt) || win.IsKeyDown(GlfwKeys.RightAlt);
        }

        private bool _Ctrl(NativeWindow win)
        {
            return win.IsKeyDown(GlfwKeys.LeftControl) || win.IsKeyDown(GlfwKeys.RightControl);
        }
        #endregion

        /// <summary>
        ///     Maps a GLFW key code to the platform-independent VocaluxeLib.Keys enum.
        ///     Letters and top-row digits share ASCII values with our enum, so they are cast directly.
        /// </summary>
        private static VKeys _MapKey(GlfwKeys key)
        {
            if (key >= GlfwKeys.A && key <= GlfwKeys.Z)
                return (VKeys)(int)key; // A..Z == 65..90 in both enums
            if (key >= GlfwKeys.D0 && key <= GlfwKeys.D9)
                return (VKeys)(int)key; // D0..D9 == 48..57 in both enums

            switch (key)
            {
                case GlfwKeys.Left: return VKeys.Left;
                case GlfwKeys.Right: return VKeys.Right;
                case GlfwKeys.Up: return VKeys.Up;
                case GlfwKeys.Down: return VKeys.Down;
                case GlfwKeys.Enter:
                case GlfwKeys.KeyPadEnter: return VKeys.Enter;
                case GlfwKeys.Escape: return VKeys.Escape;
                case GlfwKeys.Backspace: return VKeys.Back;
                case GlfwKeys.Tab: return VKeys.Tab;
                case GlfwKeys.Space: return VKeys.Space;
                case GlfwKeys.Delete: return VKeys.Delete;
                case GlfwKeys.Home: return VKeys.Home;
                case GlfwKeys.End: return VKeys.End;
                case GlfwKeys.PageUp: return VKeys.PageUp;
                case GlfwKeys.PageDown: return VKeys.PageDown;
                case GlfwKeys.KeyPad0: return VKeys.NumPad0;
                case GlfwKeys.KeyPad1: return VKeys.NumPad1;
                case GlfwKeys.KeyPad2: return VKeys.NumPad2;
                case GlfwKeys.KeyPad3: return VKeys.NumPad3;
                case GlfwKeys.KeyPad4: return VKeys.NumPad4;
                case GlfwKeys.KeyPad5: return VKeys.NumPad5;
                case GlfwKeys.KeyPad6: return VKeys.NumPad6;
                case GlfwKeys.KeyPad7: return VKeys.NumPad7;
                case GlfwKeys.KeyPad8: return VKeys.NumPad8;
                case GlfwKeys.KeyPad9: return VKeys.NumPad9;
                case GlfwKeys.KeyPadAdd: return VKeys.Add;
                case GlfwKeys.KeyPadSubtract: return VKeys.Subtract;
                case GlfwKeys.F1: return VKeys.F1;
                case GlfwKeys.F2: return VKeys.F2;
                case GlfwKeys.F3: return VKeys.F3;
                case GlfwKeys.F4: return VKeys.F4;
                case GlfwKeys.F5: return VKeys.F5;
                case GlfwKeys.F6: return VKeys.F6;
                case GlfwKeys.F7: return VKeys.F7;
                case GlfwKeys.F8: return VKeys.F8;
                case GlfwKeys.F9: return VKeys.F9;
                case GlfwKeys.F10: return VKeys.F10;
                case GlfwKeys.F11: return VKeys.F11;
                case GlfwKeys.F12: return VKeys.F12;
                default: return VKeys.None;
            }
        }

        public override bool Init()
        {
            if (!base.Init())
                return false;

            // Application icon, the standard way: on every platform set OpenTK's NativeWindow.Icon from
            // the multi-size Vocaluxe.ico (or a Vocaluxe.png fallback) shipped next to the exe
            // (WM_SETICON on Windows, _NET_WM_ICON on X11). Best effort: a missing/undecodable file is a
            // logged no-op, never breaking startup.
            string appDirectory = AppContext.BaseDirectory;
            byte[] iconRgba = null;
            int iconW = 0, iconH = 0;
            try
            {
                // Loaded once, then applied per window below.
                if (!CAppIcon.TryLoadRgba(appDirectory, out iconRgba, out iconW, out iconH))
                    CLog.Warning("CAppIcon: no Vocaluxe.ico/Vocaluxe.png next to the exe; the window keeps its default icon");
            }
            catch (Exception e)
            {
                CLog.Warning(e, "loading the app icon failed; the window keeps its default icon");
                iconRgba = null;
            }

            // Title, icon and event wiring are applied to every window (multi-window primary, plan v3 §6);
            // in single-window mode this is exactly one window, i.e. today's wiring.
            for (int i = 0; i < _GetPanelCount(); i++)
            {
                NativeWindow win = _MultiWindow() ? _Windows.Slots[i].Window : _Window;
                win.Title = CSettings.GetFullVersionText();

                if (iconRgba != null)
                    try
                    {
                        CAppIcon.SetWindowIcon(win, iconRgba, iconW, iconH);
                    }
                    catch (Exception e)
                    {
                        CLog.Warning(e, "setting the window icon failed; the window keeps its default icon");
                    }

                // OpenTK 4.9.4 types these window events as single-arg Action<T> delegates (not
                // (sender, e)), so the lambdas below take one argument.
                win.Closing += _OnClosing;
                win.Resize += _OnResize;
                win.MouseMove += e => _OnMouseMove(win, e);
                win.MouseWheel += e => _OnMouseWheel(win, e);
                win.MouseDown += e => _OnMouseDown(win, e);
                win.MouseUp += e => _OnMouseUp(win, e);
                win.KeyDown += e =>
                {
                    _Windows?.SetLostFocusKey(win, _MapKey(e.Key));
                    _OnKeyDown(e);
                };
                win.KeyUp += e =>
                {
                    _Windows?.SetLostFocusKey(win, VKeys.None);
                    _OnKeyUp(e);
                };
                win.TextInput += e => _OnTextInput(win, e);
                win.FocusedChanged += e => _OnLostFocus(win, e);
            }

#if WIN
            // The Windows task bar reads the window-CLASS icon, which GLFW never sets (it would show the
            // generic IDI_APPLICATION there). Force it from the same asset: CWindowsIcon builds a crisp
            // HICON from the largest image of Vocaluxe.ico (CreateIconFromResourceEx) or - failing that -
            // from the shell's icon for the exe, and sets class + window slots. Best effort, and safe to
            // call while the window is still hidden.
            try
            {
                CWindowsIcon.Apply(_Window.Title, appDirectory);
            }
            catch (Exception e)
            {
                CLog.Warning(e, "setting the Windows task-bar icon failed");
            }
#endif

            if (_MultiWindow())
                _Windows.ApplyLayout(false); // windowed start: centered panel size per assigned monitor
            else
                _Window.CenterWindow();

            return true;
        }

        protected override void _ShowWindow()
        {
            if (_MultiWindow())
                _Windows.ShowAll();
            else
                _Window.IsVisible = true;
        }

        public override void MainLoop()
        {
            if (_MultiWindow())
                _Windows.ShowAll();
            else
                _Window.IsVisible = true;

            base.MainLoop();

            if (_MultiWindow())
                _Windows.HideAll();
            else
                _Window.IsVisible = false;
        }
    }
}