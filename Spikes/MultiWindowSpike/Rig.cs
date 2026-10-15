using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using GlfwErrorCode = OpenTK.Windowing.GraphicsLibraryFramework.ErrorCode;
using OpenTK.Graphics.OpenGL;
using GLX = OpenTK.Graphics.OpenGL;

namespace MultiWindowSpike
{
    /// <summary>One per-monitor window plus the GL facts we track (plan §4.1 Slot).</summary>
    struct Slot
    {
        public NativeWindow Win;
        public MonitorInfo Monitor;
        public bool Shared;
        public string Renderer;
        public int FbW;
        public int FbH;
        public Vector2i RestoreLocation;
        public Vector2i RestoreSize;
    }

    /// <summary>
    ///     NativeWindow management: N per-monitor slots, shared-context creation (OpenTK 4.9.4
    ///     SharedContext, plan §3), monitor assignment, per-monitor fullscreen with the existing
    ///     Windows +1px DWM workaround (mirrors CDrawBaseGlfw._EnterFullScreen, CDrawBaseWindows.cs:55-105).
    /// </summary>
    static class Rig
    {
        static List<MonitorInfo> _Monitors;

        public static List<string> Log = new List<string>();

        static void OnGlfwError(GlfwErrorCode code, string description)
        {
            // Log, don't throw (Vocaluxe COpenGL.cs:108-114 does the same: OpenTK's default error
            // handler turns EVERY GLFW error into an exception, including benign ones).
            Log.Add("glfw " + code + ": " + description);
            Console.WriteLine("  [glfw-error] " + code + " - " + description);
        }

        public static List<Slot> Create(int panels, int winW, int winH, bool shared, bool vsync, int aa)
        {
            GLFWProvider.SetErrorCallback(OnGlfwError);

            var slots = new List<Slot>();
            IGLFWGraphicsContext share = null;

            for (int i = 0; i < panels; i++)
            {
                // Every shared window uses its OWN instance of NativeWindowSettings (OpenTK 4.9.4):
                // the settings object carries the SharedContext reference and per-window state.
                var settings = new NativeWindowSettings
                {
                    ClientSize = new Vector2i(winW, winH),
                    Title = "MultiWindowSpike " + i,
                    // GL 2.1 + ContextProfile.Any -> legacy/compatibility (fixed-function) context,
                    // identical to Vocaluxe COpenGL.cs:97-101. Shared contexts need matching settings.
                    Profile = ContextProfile.Any,
                    APIVersion = new Version(2, 1),
                    Flags = ContextFlags.Default,
                    NumberOfSamples = aa,
                    StartVisible = false,
                    Vsync = vsync ? VSyncMode.On : VSyncMode.Off,
                    SharedContext = shared ? share : null
                };

                if (shared && share == null && i > 0)
                    throw new InvalidOperationException("shared context requested but slot 0 has no shareable context (see log)");

                NativeWindow win = new NativeWindow(settings);
                if (i == 0)
                    share = GetShareContext(win);

                var slot = new Slot
                {
                    Win = win,
                    Shared = shared && i > 0,
                    FbW = win.FramebufferSize.X,
                    FbH = win.FramebufferSize.Y,
                    RestoreLocation = win.Location,
                    RestoreSize = win.ClientSize
                };

                // GL_RENDERER per slot -> GPU grouping for the mixed-GPU fallback (plan §5).
                win.MakeCurrent();
                try
                {
                    slot.Renderer = (GL.GetString(GLX.StringName.Renderer) ?? "<null>").Trim();
                }
                catch (Exception e)
                {
                    slot.Renderer = "<error: " + e.Message + ">";
                }

                // Per-slot handler (the production pattern, plan §6): FocusedChanged anchors the
                // stuck-key flush. OpenTK 4.9.4 types it as a single-arg Action&lt;FocusedChangedEventArgs&gt;.
                win.FocusedChanged += static (e) =>
                {
                    // plan §15: presence check (production uses this for stuck-key flush)
                };

                slots.Add(slot);
            }

            _Monitors = MonitorsList();
            for (int i = 0; i < slots.Count; i++)
            {
                // Slot i -> monitor i % count; extra panels stack on the same monitor (plan §4.1;
                // OpenTK's Monitors return primary first).
                Slot s = slots[i];
                s.Monitor = _Monitors.Count > 0 ? _Monitors[i % _Monitors.Count] : null;
                slots[i] = s;
            }

            // Framebuffer size (DPI-aware physical pixels) drives the blit viewport; it is read
            // live (RefreshFb) whenever a window-state change may have changed it.
            RefreshFb(slots);
            return slots;
        }

        /// <summary>
        ///     NativeWindowSettings.SharedContext expects an IGLFWGraphicsContext; in OpenTK 4.9.4
        ///     NativeWindow.Context is already typed exactly that way, so slot 0's context can be
        ///     handed straight to the settings of slots 1..N-1 (children share its resource space).
        /// </summary>
        static IGLFWGraphicsContext GetShareContext(NativeWindow win)
        {
            return win.Context;
        }

        public static List<MonitorInfo> MonitorsList()
        {
            if (_Monitors != null)
                return _Monitors;
            try
            {
                _Monitors = Monitors.GetMonitors();
                if (_Monitors == null)
                    _Monitors = new List<MonitorInfo>();
            }
            catch (Exception e)
            {
                Console.WriteLine("  Monitors.GetMonitors failed: " + e.Message);
                _Monitors = new List<MonitorInfo>();
            }
            return _Monitors;
        }

        /// <summary>Reads the live DPI-aware framebuffer size into the slot (blit-viewport source of truth).</summary>
        static void RefreshFb(List<Slot> slots)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                s.FbW = s.Win.FramebufferSize.X;
                s.FbH = s.Win.FramebufferSize.Y;
                slots[i] = s;
            }
        }

        public static void PlaceWindowed(List<Slot> slots)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                NativeWindow win = slot.Win;
                win.WindowState = WindowState.Normal;
                win.WindowBorder = WindowBorder.Resizable;
                if (slot.Monitor != null)
                {
                    Box2i area = slot.Monitor.ClientArea;
                    win.Location = new Vector2i(
                        area.Min.X + (int)Math.Max(0, (area.Size.X - win.ClientSize.X) / 2.0) + (i % 5) * 36,
                        area.Min.Y + (int)Math.Max(0, (area.Size.Y - win.ClientSize.Y) / 2.0) + (i / 5) * 36);
                }
                else
                {
                    win.Location = new Vector2i(80 + (i % 6) * 80, 80 + (i / 6) * 80);
                }
                slots[i] = slot;
            }
            RefreshFb(slots);
        }

        public static void EnterFullscreen(List<Slot> slots, out bool usedMaximized)
        {
            usedMaximized = false;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                NativeWindow win = slot.Win;
                slot.RestoreLocation = win.Location;
                slot.RestoreSize = win.ClientSize;

                if (OperatingSystem.IsWindows())
                {
                    if (slot.Monitor == null)
                    {
                        // Never let fullscreen setup kill the run (mirrors CDrawBaseGlfw catch path).
                        win.WindowState = WindowState.Maximized;
                        usedMaximized = true;
                        slots[i] = slot;
                        continue;
                    }
                    // Windows: a window that EXACTLY covers a monitor gets promoted to exclusive
                    // present, whose path freezes a manually driven render loop. One extra pixel
                    // keeps it on the composed (DWM) path — the existing Vocaluxe workaround.
                    Box2i area = slot.Monitor.ClientArea;
                    win.WindowBorder = WindowBorder.Hidden;
                    win.WindowState = WindowState.Normal;
                    win.Location = area.Min;
                    win.ClientSize = new Vector2i(area.Size.X, area.Size.Y + 1);
                }
                else
                {
                    win.WindowState = WindowState.Fullscreen;
                }
                slots[i] = slot;
            }
            RefreshFb(slots);
        }

        public static void ExitFullscreen(List<Slot> slots)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                NativeWindow win = slot.Win;
                win.WindowState = WindowState.Normal;
                win.WindowBorder = WindowBorder.Resizable;
                win.ClientSize = slot.RestoreSize;
                win.Location = slot.RestoreLocation;
                slots[i] = slot;
            }
            RefreshFb(slots);
        }

        public static void Close(List<Slot> slots)
        {
            // Teardown order (plan §7): GL objects are deleted by the caller first (they live in
            // the shared namespace), then windows are closed back-to-front.
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                try
                {
                    slots[i].Win.Close();
                }
                catch (Exception e)
                {
                    Console.WriteLine("  close slot " + i + " failed: " + e.Message);
                }
            }
        }
    }
}
