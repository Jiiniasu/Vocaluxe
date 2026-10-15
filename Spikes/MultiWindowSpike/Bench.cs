using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using GLX = OpenTK.Graphics.OpenGL;

namespace MultiWindowSpike
{
    /// <summary>
    ///     Probe (GATE-API) and bench (GATE-SMOKE/PERF) drivers. All four bench architectures
    ///     draw the SAME scene (Scene.DrawPanel); the differences — one shared-context VFB render
    ///     + N slice blits, one giant window, N independent re-renders, or a shared context with
    ///     each window re-rendering its own panel (sharedrender) — are precisely the architectural
    ///     deltas under test (plan §11).
    /// </summary>
    static class Bench
    {
        static long _T0;

        static long Now()
        {
            if (_T0 == 0)
                _T0 = Stopwatch.GetTimestamp();
            return Stopwatch.GetTimestamp();
        }

        static double Us(long a, long b)
        {
            return (b - a) * 1000000.0 / Stopwatch.Frequency;
        }

        static string ResultsDir()
        {
            string dir = Path.Combine(Util.RepoRoot(), "Spikes", "results");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Save(Results r)
        {
            string file = Path.Combine(
                ResultsDir(),
                string.Format("{0}_n{1}_q{2}_aa{3}_vs{4}.json", r.Mode, r.Panels, r.Quads, r.Aa, r.Vsync ? 1 : 0));
            File.WriteAllText(file, r.Json());
            Console.WriteLine("  saved " + file);
        }

        static void Dump(Results r)
        {
            var inv = CultureInfo.InvariantCulture;
            Console.WriteLine(string.Format(
                "  {0} n={1} q={2} aa={3} vs={4} shared={5}: fps={6} frame={7}ms (p99 {8}ms) frames={9}",
                r.Mode, r.Panels, r.Quads, r.Aa, r.Vsync ? 1 : 0, r.SharedContext,
                r.Fps.ToString("0.##", inv),
                r.FrameMeanMs.ToString("0.###", inv),
                r.FrameP99Ms.ToString("0###", inv),
                r.Frames));
            foreach (Op op in r.Ops)
            {
                if (op.Count == 0)
                    continue;
                var (m, p) = op.Summary();
                Console.WriteLine(string.Format("    {0,-8} mean={1}us p99={2}us (n={3})",
                    op.Name, m.ToString("0.#", inv), p.ToString("0.#", inv), op.Count));
            }
        }

        /// <summary>
        ///     Viewport/projection setup for "draw `panels` canvas panels starting at `panel`".
        ///     VFB/giant paths pass the whole canvas; rerender paths pass exactly one panel
        ///     (the _PanelIndex restrictor equivalent, plan §8).
        /// </summary>
        static void BeginScene(Slot s, int panel, int panels)
        {
            s.Win.MakeCurrent();
            GL.Viewport(0, 0, s.FbW, s.FbH);
            GL.MatrixMode(GLX.MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(panel * 1280f, (panel + panels) * 1280f, 0f, 720f, -1f, 1f);
            GL.MatrixMode(GLX.MatrixMode.Modelview);
            GL.LoadIdentity();
            GL.Disable(GLX.EnableCap.DepthTest);
            GL.Enable(GLX.EnableCap.Texture2D);
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(GLX.ClearBufferMask.ColorBufferBit);
        }

        /// <summary>Reads one pixel from the current window's backbuffer (before swap).</summary>
        public static unsafe void ReadPixel(int x, int y, out byte r, out byte g, out byte b)
        {
            byte[] buf = new byte[4];
            fixed (byte* p = buf)
            {
                GL.ReadPixels(x, y, 1, 1, GLX.PixelFormat.Bgra, GLX.PixelType.UnsignedByte, new IntPtr(p));
            }
            b = buf[0];
            g = buf[1];
            r = buf[2];
        }

        /// <summary>One full VFB-arch frame: scene once into the VFB, then blit + swap each slot.</summary>
        static void DrawFrame(List<Slot> slots, int N, int quads)
        {
            Slot primary = slots[0];
            primary.Win.MakeCurrent();
            Vfb.BeginRender();
            for (int i = 0; i < N; i++)
                Scene.DrawPanel(i, quads, 0.5, i + 1);
            Vfb.EndRender();
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                s.Win.MakeCurrent();
                Vfb.BlitPanel(s, i);
                s.Win.Context.SwapBuffers();
            }
            NativeWindow.ProcessWindowEvents(false);
        }

        /// <summary>Teardown contract (plan §7): GL objects first (shared namespace), then windows.</summary>
        static void Cleanup(List<Slot> slots, string mode)
        {
            try
            {
                if (mode == "vfb")
                {
                    slots[0].Win.MakeCurrent();
                    Vfb.Delete();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("  vfb cleanup failed: " + e.Message);
            }
            Rig.Close(slots);
        }

        // ------------------------------------------------------------------ benchmark pass

        public static Results RunOne(Args a)
        {
            int N = a.Panels;
            bool shared = a.Mode == "vfb" || a.Mode == "sharedrender";
            int count = a.Mode == "giant" ? 1 : N;
            int ww = a.Mode == "giant" ? 1280 * N : 1280;
            Console.WriteLine();
            Console.WriteLine(string.Format("mode={0} panels={1} quads={2} aa={3} vsync={4} windows={5} size={6}x720 shared={7}",
                a.Mode, N, a.Quads, a.Aa, a.Vsync ? 1 : 0, count, ww, shared ? "yes" : "no"));

            try
            {
                List<Slot> slots = Rig.Create(count, ww, 720, shared, a.Vsync, a.Aa);

                var r = new Results
                {
                    Mode = a.Mode,
                    Panels = N,
                    Quads = a.Quads,
                    Aa = a.Aa,
                    Vsync = a.Vsync,
                    Seconds = a.Seconds,
                    SharedContext = shared
                };
                foreach (Slot s in slots)
                    r.Renderers.Add(s.Renderer);
                r.MixedGpu = new HashSet<string>(r.Renderers, StringComparer.OrdinalIgnoreCase).Count > 1;
                int ri = 0;
                foreach (Slot s in slots)
                {
                    ri++;
                    Console.WriteLine(string.Format("  slot{0}: shared={1} renderer={2} monitor={3}",
                        ri - 1, s.Shared ? "yes" : "no", s.Renderer,
                        s.Monitor == null ? "<none>" : s.Monitor.ClientArea.Size.X + "x" + s.Monitor.ClientArea.Size.Y));
                }

                if (a.Mode == "vfb")
                {
                    slots[0].Win.MakeCurrent();
                    Vfb.Create(1280 * N, 720);
                    Console.WriteLine(string.Format("  VFB texture {0}x{1} (canvas {2}x{3})",
                        Vfb.TexW, Vfb.TexH, Vfb.CanvasW, Vfb.CanvasH));
                }

                // Shared-namespace textures for the whole run (created once on slot 0).
                slots[0].Win.MakeCurrent();
                Scene.EnsureTextures();

                Rig.PlaceWindowed(slots);
                foreach (Slot s in slots)
                    s.Win.IsVisible = true;
                NativeWindow.ProcessWindowEvents(true);

                var opScene = new Op("scene");
                var opRebind = new Op("rebind");
                var opBlit = new Op("blit");
                var opSwap = new Op("swap");
                r.Ops.Add(opScene);
                r.Ops.Add(opRebind);
                r.Ops.Add(opBlit);
                r.Ops.Add(opSwap);

                var frameMs = new List<double>();
                int frame = 0;
                bool run = true;
                long runStart = Now();
                double elapsedSec = 0;

                while (run)
                {
                    long f0 = Now();
                    frame++;
                    double tSec = (Now() - runStart) * 1.0 / Stopwatch.Frequency;

                    if (a.Mode == "giant")
                    {
                        // Baseline: today's single giant window, direct render, one swap.
                        Slot w = slots[0];
                        BeginScene(w, 0, N);
                        long s0 = Now();
                        for (int i = 0; i < N; i++)
                            Scene.DrawPanel(i, a.Quads, tSec, frame);
                        opScene.Add(Us(s0, Now()));
                        long sp0 = Now();
                        w.Win.Context.SwapBuffers();
                        opSwap.Add(Us(sp0, Now()));
                    }
                    else if (a.Mode == "vfb")
                    {
                        // Primary path: shared context, scene once into the VFB, N slice blits.
                        Slot primary = slots[0];
                        primary.Win.MakeCurrent();
                        Vfb.BeginRender();
                        long s0 = Now();
                        for (int i = 0; i < N; i++)
                            Scene.DrawPanel(i, a.Quads, tSec, frame);
                        opScene.Add(Us(s0, Now()));
                        Vfb.EndRender();
                        for (int i = 0; i < slots.Count; i++)
                        {
                            Slot s = slots[i];
                            long r0 = Now();
                            s.Win.MakeCurrent();
                            long r1 = Now();
                            opRebind.Add(Us(r0, r1));
                            long b0 = Now();
                            Vfb.BlitPanel(s, i);
                            long b1 = Now();
                            opBlit.Add(Us(b0, b1));
                            long sp0 = Now();
                            s.Win.Context.SwapBuffers();
                            long sp1 = Now();
                            opSwap.Add(Us(sp0, sp1));
                        }
                    }
                    else // per-window: rerender (independent contexts) or sharedrender (shared context);
                    //      each window re-renders its OWN panel directly into its backbuffer.
                    {
                        for (int i = 0; i < slots.Count; i++)
                        {
                            Slot s = slots[i];
                            long r0 = Now();
                            s.Win.MakeCurrent();
                            long r1 = Now();
                            opRebind.Add(Us(r0, r1));
                            BeginScene(s, i, 1);
                            long s0 = Now();
                            Scene.DrawPanel(i, a.Quads, tSec, frame);
                            opScene.Add(Us(s0, Now()));
                            long sp0 = Now();
                            s.Win.Context.SwapBuffers();
                            long sp1 = Now();
                            opSwap.Add(Us(sp0, sp1));
                        }
                    }

                    NativeWindow.ProcessWindowEvents(false);
                    foreach (Slot s in slots)
                    {
                        if (s.Win.IsExiting)
                            run = false;
                    }

                    frameMs.Add(Us(f0, Now()) / 1000.0);
                    elapsedSec = (Now() - runStart) * 1.0 / Stopwatch.Frequency;
                    if (elapsedSec >= a.Seconds)
                        run = false;
                }

                // Content check for the shared-context per-window mode: prove each slot really
                // renders AND presents its own panel (probe already proved shared-texture visibility).
                if (a.Mode == "sharedrender")
                {
                    bool pixOk = true;
                    string pixDet = null;
                    for (int i = 0; i < slots.Count && pixOk; i++)
                    {
                        Slot s = slots[i];
                        s.Win.MakeCurrent();
                        BeginScene(s, i, 1);
                        Scene.DrawPanel(i, 0, 1.0, 7);
                        int cx = s.FbW / 2, cy = s.FbH / 2;
                        ReadPixel(cx, cy, out byte rr, out byte gg, out byte bb);
                        var (er, eg, eb) = Scene.PanelHue(i);
                        if (Math.Abs((int)rr - (int)Math.Round(er * 255)) > 4
                            || Math.Abs((int)gg - (int)Math.Round(eg * 255)) > 4
                            || Math.Abs((int)bb - (int)Math.Round(eb * 255)) > 4)
                        {
                            pixOk = false;
                            pixDet = string.Format("slot{0}: got {1},{2},{3} want ~{4},{5},{6}",
                                i, rr, gg, bb, (int)Math.Round(er * 255),
                                (int)Math.Round(eg * 255), (int)Math.Round(eb * 255));
                        }
                        s.Win.Context.SwapBuffers();
                    }
                    Console.WriteLine((pixOk ? "  CONTENT PASS " : "  CONTENT FAIL ")
                        + "each slot renders + presents its own panel (shared ctx) ["
                        + (pixDet ?? "all slots correct") + "]");
                    if (!pixOk)
                        r.Errors.Add("content check: " + pixDet);
                }

                r.Frames = frame;
                r.Fps = elapsedSec > 0 ? frame / elapsedSec : 0;
                double sum = 0;
                foreach (double v in frameMs)
                    sum += v;
                var sorted = new List<double>(frameMs);
                sorted.Sort();
                r.FrameMeanMs = frameMs.Count > 0 ? sum / frameMs.Count : 0;
                r.FrameP99Ms = sorted.Count > 0 ? sorted[Math.Min(sorted.Count - 1, (int)(0.99 * sorted.Count + 0.5))] : 0;

                Save(r);
                Dump(r);
                Cleanup(slots, a.Mode);
                return r;
            }
            catch (Exception e)
            {
                Console.WriteLine("  RUN FAILED: " + e);
                return null;
            }
        }

        // ------------------------------------------------------------------ probe (GATE-API)

        public static bool RunProbe(Args a)
        {
            int N = a.Panels;
            var checks = new List<string>();
            bool fail = false;

            void Check(string name, bool ok, string detail)
            {
                checks.Add((ok ? "PASS  " : "FAIL  ") + name + (detail == null ? "" : "   [" + detail + "]"));
                if (!ok)
                    fail = true;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("probe: {0} shared-context windows, 1280x720, aa=0", N));

            List<Slot> slots;
            try
            {
                slots = Rig.Create(N, 1280, 720, true, false, 0);
            }
            catch (Exception e)
            {
                Console.WriteLine("  window creation failed: " + e);
                return false;
            }
            Check("N windows created (shared context on slots 1..N-1)", slots.Count == N, N + " slots");

            int idx = 0;
            foreach (Slot s in slots)
            {
                idx++;
                Console.WriteLine(string.Format("  slot{0}: shared={1} renderer={2} monitor={3}",
                    idx - 1, s.Shared ? "yes" : "no", s.Renderer,
                    s.Monitor == null ? "<none>" : s.Monitor.ClientArea.Size.X + "x" + s.Monitor.ClientArea.Size.Y
                        + " @" + s.Monitor.ClientArea.Min.X + "," + s.Monitor.ClientArea.Min.Y));
            }

            var mons = Rig.MonitorsList();
            int mi = 0;
            foreach (MonitorInfo m in mons)
            {
                mi++;
                Console.WriteLine(string.Format("    monitor{0}: {1}x{2} @ {3},{4}",
                    mi - 1, m.ClientArea.Size.X, m.ClientArea.Size.Y, m.ClientArea.Min.X, m.ClientArea.Min.Y));
            }
            Check("GATE-API(c) Monitors.GetMonitors() works", mons.Count > 0, mons.Count + " monitors");

            // GATE-API(a): a texture created on slot 0 must be visible from every other slot.
            slots[0].Win.MakeCurrent();
            int probeTex = 0;
            try
            {
                probeTex = GL.GenTexture();
                GL.BindTexture(GLX.TextureTarget.Texture2D, probeTex);
                GL.TexImage2D(GLX.TextureTarget.Texture2D, 0, GLX.PixelInternalFormat.Rgba, 16, 16, 0,
                    GLX.PixelFormat.Bgra, GLX.PixelType.UnsignedByte, new byte[16 * 16 * 4]);
                GL.BindTexture(GLX.TextureTarget.Texture2D, 0);
            }
            catch (Exception e)
            {
                Check("GATE-API(a) create texture on slot 0", false, e.Message);
            }
            bool texOk = probeTex != 0;
            if (texOk)
            {
                foreach (Slot s in slots)
                {
                    s.Win.MakeCurrent();
                    if (!GL.IsTexture(probeTex))
                        texOk = false;
                }
            }
            Check("GATE-API(a) slot-0 texture visible from every slot (GL.IsTexture)", texOk, N + " slots");

            // VFB + shared white texture for the pixel-verification paths below.
            slots[0].Win.MakeCurrent();
            try
            {
                Scene.EnsureTextures();
                Vfb.Create(1280 * N, 720);
                Check("VFB FBO complete (POT texture, no mips)", true, Vfb.TexW + "x" + Vfb.TexH);
            }
            catch (Exception e)
            {
                Check("VFB FBO complete (POT texture, no mips)", false, e.Message);
                FinishProbe(slots, checks, "vfb");
                return false;
            }

            Rig.PlaceWindowed(slots);
            foreach (Slot s in slots)
                s.Win.IsVisible = true;
            NativeWindow.ProcessWindowEvents(true);

            // Fullscreen in/out ×2 doubles as the exclusive-present freeze check (GATE-SMOKE part):
            // after each toggle the newly drawn content must be readable back before swap.
            bool stateOk = true;
            string stateDetail = null;
            bool curFs = false;
            int step = 0;
            foreach (bool wantFs in new bool[] { true, false, true, false, true, false })
            {
                step++;
                if (wantFs != curFs)
                {
                    try
                    {
                        if (wantFs)
                            Rig.EnterFullscreen(slots, out _);
                        else
                            Rig.ExitFullscreen(slots);
                        NativeWindow.ProcessWindowEvents(true);
                    }
                    catch (Exception e)
                    {
                        stateOk = false;
                        stateDetail = "step" + step + " toggle: " + e.Message;
                        break;
                    }
                    curFs = wantFs;
                }
                int c = 20 + (step - 1) * 40;   // distinct expected color per step (all exact uint8)
                try
                {
                    foreach (Slot s in slots)
                    {
                        s.Win.MakeCurrent();
                        GL.Viewport(0, 0, s.FbW, s.FbH);
                        GL.MatrixMode(GLX.MatrixMode.Projection);
                        GL.LoadIdentity();
                        GL.Ortho(0f, 1f, 0f, 1f, -1f, 1f);
                        GL.MatrixMode(GLX.MatrixMode.Modelview);
                        GL.LoadIdentity();
                        GL.Disable(GLX.EnableCap.Texture2D);
                        GL.Disable(GLX.EnableCap.DepthTest);
                        GL.ClearColor(c / 255f, (255 - c) / 255f, 0.5f, 1f);
                        GL.Clear(GLX.ClearBufferMask.ColorBufferBit);
                        ReadPixel(5, 5, out byte rr, out byte gg, out byte bb);
                        if (Math.Abs(rr - (byte)c) > 3 || Math.Abs(gg - (byte)(255 - c)) > 3)
                        {
                            stateOk = false;
                            stateDetail = "step" + step + (wantFs ? " (fullscreen)" : " (windowed)")
                                + " pixel got " + rr + "," + gg + " want " + c + "," + (255 - c);
                        }
                        s.Win.Context.SwapBuffers();
                    }
                    NativeWindow.ProcessWindowEvents(true);
                }
                catch (Exception e)
                {
                    stateOk = false;
                    stateDetail = "step" + step + " draw: " + e.Message;
                    break;
                }
            }
            Check("fullscreen toggle x3 round-trips, content still presented (no freeze)", stateOk, stateDetail);

            // Minimize/restore ×2 (Alt-Tab proxy) + one more frame afterwards.
            bool minOk = true;
            string minDetail = null;
            try
            {
                Slot target = slots[Math.Min(1, slots.Count - 1)];
                for (int k = 0; k < 2 && minOk; k++)
                {
                    target.Win.WindowState = WindowState.Minimized;
                    NativeWindow.ProcessWindowEvents(true);
                    target.Win.WindowState = WindowState.Normal;
                    NativeWindow.ProcessWindowEvents(true);
                    DrawFrame(slots, N, 25);
                    if (slots.Any(s => s.Win.IsExiting))
                        minOk = false;
                }
            }
            catch (Exception e)
            {
                minOk = false;
                minDetail = e.Message;
            }
            Check("minimize/restore x2 (Alt-Tab proxy) then still presents", minOk, minDetail);

            // GATE-API(b): render the N-panel scene (quads=0 => clean center pixel) into the VFB,
            // blit each slot to ITS panel, verify the center pixel matches that panel's hue.
            // This proves slots 1..N-1 actually read slot 0's texture (content share, not just
            // IsTexture) and that the blit quad's UVs/rect map the right slice.
            bool pixOk = true;
            string pixDetail = null;
            try
            {
                Slot primary = slots[0];
                primary.Win.MakeCurrent();
                Vfb.BeginRender();
                for (int i = 0; i < N; i++)
                    Scene.DrawPanel(i, 0, 1.0, 7);
                // Read the FBO back while it is still the current draw framebuffer: this proves
                // the SCENE pass wrote the right panel at the right place before any blit.
                var vfbPts = new (int x, int y, int panel, string tag)[]
                {
                    (160, 360, 0, "p0@160"),
                    (640, 360, 0, "p0center"),
                    (1200, 360, 0, "p0@1200"),
                    (1280 * 5 + 640, 360, 5, "p5center")
                };
                bool vfbOk = true;
                string vfbDet = null;
                foreach (var pt in vfbPts)
                {
                    ReadPixel(pt.x, pt.y, out byte vr, out byte vg, out byte vb);
                    var (er, eg, eb) = Scene.PanelHue(pt.panel);
                    bool ok = Math.Abs((int)vr - (int)Math.Round(er * 255)) <= 4
                        && Math.Abs((int)vg - (int)Math.Round(eg * 255)) <= 4
                        && Math.Abs((int)vb - (int)Math.Round(eb * 255)) <= 4;
                    if (!ok)
                    {
                        vfbOk = false;
                        vfbDet = pt.tag + " got " + vr + "," + vg + "," + vb
                            + " want hue" + pt.panel + " ~" + (int)Math.Round(er * 255) + ","
                            + (int)Math.Round(eg * 255) + "," + (int)Math.Round(eb * 255);
                        break;
                    }
                }
                Check("VFB scene content at 4 probe points (scene pass, pre-blit)", vfbOk, vfbDet);
                Vfb.EndRender();
                for (int i = 0; i < slots.Count; i++)
                {
                    Slot s = slots[i];
                    s.Win.MakeCurrent();
                    Vfb.BlitPanel(s, i);
                    int cx = s.FbW / 2, cy = s.FbH / 2;
                    // Spatial diagnostic: 5 points to distinguish a shifted blit rect from a
                    // uniform content error (printed, not part of the verdict).
                    ReadPixel(cx, 3, out byte cr1, out byte cg1, out byte cb1);
                    ReadPixel(cx, cy, out byte cr2, out byte cg2, out byte cb2);
                    ReadPixel(cx, s.FbH - 3, out byte cr3, out byte cg3, out byte cb3);
                    ReadPixel(3, cy, out byte cr4, out byte cg4, out byte cb4);
                    ReadPixel(s.FbW - 3, cy, out byte cr5, out byte cg5, out byte cb5);
                    Console.WriteLine(string.Format(
                        "    slot{0} {1}x{2}: top=({3},{4},{5}) center=({6},{7},{8}) bottom=({9},{10},{11}) left=({12},{13},{14}) right=({15},{16},{17})",
                        i, s.FbW, s.FbH,
                        cr1, cg1, cb1, cr2, cg2, cb2, cr3, cg3, cb3, cr4, cg4, cb4, cr5, cg5, cb5));
                    ReadPixel(cx, cy, out byte rr, out byte gg, out byte bb);
                    var (er, eg, eb) = Scene.PanelHue(i);
                    bool ok = Math.Abs((int)rr - (int)Math.Round(er * 255)) <= 4
                        && Math.Abs((int)gg - (int)Math.Round(eg * 255)) <= 4
                        && Math.Abs((int)bb - (int)Math.Round(eb * 255)) <= 4;
                    if (!ok)
                    {
                        pixOk = false;
                        pixDetail = string.Format("slot{0}: got {1},{2},{3} want ~{4},{5},{6}",
                            i, rr, gg, bb, (int)Math.Round(er * 255), (int)Math.Round(eg * 255), (int)Math.Round(eb * 255));
                    }
                    s.Win.Context.SwapBuffers();
                }
            }
            catch (Exception e)
            {
                pixOk = false;
                pixDetail = e.Message;
            }
            Check("GATE-API(b) each slot presents its own panel slice of the shared VFB", pixOk, pixDetail);

            FinishProbe(slots, checks, "vfb");
            return !fail;
        }

        static void FinishProbe(List<Slot> slots, List<string> checks, string mode)
        {
            try
            {
                if (mode == "vfb")
                {
                    slots[0].Win.MakeCurrent();
                    if (Scene.White != 0)
                        GL.DeleteTexture(Scene.White);
                    Vfb.Delete();
                }
            }
            catch
            {
                // best-effort destruction; window close follows anyway
            }
            Rig.Close(slots);

            Console.WriteLine();
            Console.WriteLine("=== probe results ===");
            foreach (string c in checks)
                Console.WriteLine("  " + c);
            bool anyFail = false;
            foreach (string c in checks)
            {
                if (c.StartsWith("FAIL"))
                    anyFail = true;
            }
            Console.WriteLine(anyFail ? "RESULT: FAIL" : "RESULT: PASS");
        }

        // ------------------------------------------------------------------ matrix

        public static bool RunMatrix()
        {
            (string mode, int n, int q, int vs, int aa, int secs)[] runs =
            {
                // GATE-PERF matrix (plan §11.2/11.3), vsync off for raw GPU cost:
                ("giant", 1, 250, 0, 0, 15),
                ("vfb", 1, 250, 0, 0, 15),
                ("rerender", 1, 250, 0, 0, 15),
                ("giant", 2, 250, 0, 0, 15),
                ("vfb", 2, 250, 0, 0, 15),
                ("rerender", 2, 250, 0, 0, 15),
                ("giant", 3, 250, 0, 0, 15),
                ("vfb", 3, 250, 0, 0, 15),
                ("rerender", 3, 250, 0, 0, 15),
                ("giant", 6, 250, 0, 0, 15),
                ("vfb", 6, 250, 0, 0, 15),
                ("rerender", 6, 250, 0, 0, 15),
                ("giant", 6, 1000, 0, 0, 20),
                ("vfb", 6, 1000, 0, 0, 20),
                ("rerender", 6, 1000, 0, 0, 20),
                // vsync on: real present path; the 60s vfb run doubles as the shortened
                // GATE-SMOKE endurance check (the 300s form runs in the in-app matrix).
                ("giant", 6, 1000, 1, 0, 30),
                ("vfb", 6, 1000, 1, 0, 60),
                ("rerender", 6, 1000, 1, 0, 30),
                // sharedrender (hybrid: shared context + per-window own-panel render),
                // same cells as vfb so it is directly comparable against giant/rerender:
                ("sharedrender", 1, 250, 0, 0, 15),
                ("sharedrender", 2, 250, 0, 0, 15),
                ("sharedrender", 3, 250, 0, 0, 15),
                ("sharedrender", 6, 250, 0, 0, 15),
                ("sharedrender", 6, 1000, 0, 0, 20),
                // 60 s vsync-on run doubles as the sharedrender endurance check:
                ("sharedrender", 6, 1000, 1, 0, 60)
            };

            var byKey = new Dictionary<string, Results>();
            var summary = new List<string>();
            bool allOk = true;
            long t0 = Now();

            foreach (var run in runs)
            {
                Console.WriteLine();
                Console.WriteLine("########## " + run.mode + " n=" + run.n + " q=" + run.q
                    + " vs=" + run.vs + " (" + run.secs + " s) ##########");
                var a = new Args
                {
                    Mode = run.mode,
                    Panels = run.n,
                    Quads = run.q,
                    Vsync = run.vs == 1,
                    Aa = run.aa,
                    Seconds = run.secs
                };
                Results r = null;
                try
                {
                    r = RunOne(a);
                }
                catch (Exception e)
                {
                    Console.WriteLine("  RUN FAILED: " + e);
                }
                bool ok = r != null;
                allOk &= ok;
                byKey[run.mode + "_" + run.n + "_" + run.q + "_" + run.vs] = r;
                summary.Add(string.Format("{0,-9} n={1} q={2,-4} vs={3} -> {4}",
                    run.mode, run.n, run.q, run.vs, ok ? "ok" : "FAILED"));
            }

            Console.WriteLine();
            Console.WriteLine("########## GATE-PERF (N=6 quads=1000 vsync=0) ##########");
            var gateLines = Gates.Eval(
                byKey.ContainsKey("giant_6_1000_0") ? byKey["giant_6_1000_0"] : null,
                byKey.ContainsKey("vfb_6_1000_0") ? byKey["vfb_6_1000_0"] : null,
                byKey.ContainsKey("rerender_6_1000_0") ? byKey["rerender_6_1000_0"] : null,
                byKey.ContainsKey("sharedrender_6_1000_0") ? byKey["sharedrender_6_1000_0"] : null);
            bool gateOk = true;
            foreach (string line in gateLines)
            {
                Console.WriteLine("  " + line);
                if (line.Contains("FAIL"))
                    gateOk = false;
            }

            double totalMin = (Now() - t0) * 1.0 / Stopwatch.Frequency / 60.0;
            Console.WriteLine();
            Console.WriteLine("=== matrix summary (" + totalMin.ToString("0.#", CultureInfo.InvariantCulture) + " min) ===");
            foreach (string s in summary)
                Console.WriteLine("  " + s);
            Console.WriteLine(gateOk ? "GATES: PASS" : "GATES: FAIL (see lines above)");

            File.WriteAllText(Path.Combine(ResultsDir(), "summary.txt"),
                string.Join(Environment.NewLine, summary.Concat(gateLines)));

            return allOk && gateOk;
        }
    }
}
