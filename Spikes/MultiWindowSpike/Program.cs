using System;

namespace MultiWindowSpike
{
    /// <summary>
    ///     Arguments (plan §11.2 CLI). --mode selects the architecture under test:
    ///     giant    = replica of today's single giant window (1280N×720, direct render), the true baseline
    ///     vfb      = primary path: shared context, one VFB render, N slice blits
    ///     rerender = fallback-style: independent contexts, each window re-renders its own panel
    ///     sharedrender = hybrid: shared context (textures live once in VRAM) + each window
    ///                 re-renders its own panel directly (no VFB, no blit)
    ///     probe    = GATE-API validation (shared-texture visibility, N-window present, monitors, fullscreen)
    ///     matrix   = full §11.2 bench matrix + 60 s endurance + GATE-PERF evaluation
    /// </summary>
    class Args
    {
        public string Mode = "bench";
        public int Panels = 6;
        public int Quads = 250;
        public bool Vsync;
        public int Aa;
        public int Seconds = 30;

        public static string Usage()
        {
            return
                "MultiWindowSpike\n" +
                "  --probe                          GATE-API validation + fullscreen/minimize + pixel checks\n" +
                "  --mode giant|vfb|rerender|sharedrender\n" +
                "  --matrix                         full bench matrix + endurance + GATE-PERF\n" +
                "  --panels N      (default 6, clamped 1..6)\n" +
                "  --quads Q       (per-panel moving quads, default 250)\n" +
                "  --vsync 0|1     (default 0)\n" +
                "  --aa 0|2|4      (default 0)\n" +
                "  --seconds S     (default 30)\n";
        }

        public static Args Parse(string[] raw)
        {
            var a = new Args();
            for (int i = 0; i < raw.Length; i++)
            {
                string k = raw[i];
                if (k == "--probe" || k == "--matrix")
                {
                    a.Mode = k.Substring(2);
                    continue;
                }
                string v = i + 1 < raw.Length ? raw[++i] : "";
                switch (k)
                {
                    case "--mode": a.Mode = v.ToLowerInvariant(); break;
                    case "--panels": a.Panels = int.Parse(v); break;
                    case "--quads": a.Quads = int.Parse(v); break;
                    case "--vsync": a.Vsync = (v == "1" || v.ToLowerInvariant() == "on"); break;
                    case "--aa": a.Aa = int.Parse(v); break;
                    case "--seconds": a.Seconds = int.Parse(v); break;
                    case "-h":
                    case "--help":
                        Console.WriteLine(Usage());
                        Environment.Exit(0);
                        break;
                    default:
                        throw new ArgumentException("Unknown argument: " + k + "\n" + Usage());
                }
            }
            a.Panels = Math.Max(1, Math.Min(6, a.Panels));
            a.Quads = Math.Max(0, a.Quads);
            a.Seconds = Math.Max(3, a.Seconds);
            return a;
        }
    }

    static class Program
    {
        static int Main(string[] raw)
        {
            try
            {
                Args a = Args.Parse(raw);
                Console.WriteLine("=== MultiWindowSpike ===");
                Console.WriteLine("platform: " + Environment.OSVersion + " (" + Environment.MachineName + ")");

                if (a.Mode == "probe")
                    return Bench.RunProbe(a) ? 0 : 1;
                if (a.Mode == "matrix")
                    return Bench.RunMatrix() ? 0 : 1;
                if (a.Mode == "giant" || a.Mode == "vfb" || a.Mode == "rerender" || a.Mode == "sharedrender")
                    return Bench.RunOne(a) != null ? 0 : 1;
                throw new ArgumentException("Missing mode (probe|matrix|giant|vfb|rerender|sharedrender).\n" + Args.Usage());
            }
            catch (Exception e)
            {
                Console.WriteLine("FATAL: " + e);
                return 2;
            }
        }
    }
}
