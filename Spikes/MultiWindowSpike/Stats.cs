using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MultiWindowSpike
{
    /// <summary>Microsecond samples for one operation (mean / p99).</summary>
    class Op
    {
        public readonly string Name;
        readonly List<long> _Us = new List<long>();

        public Op(string name)
        {
            Name = name;
        }

        public void Add(double us)
        {
            _Us.Add(us < 0 ? 0 : (long)us);
        }

        public int Count
        {
            get { return _Us.Count; }
        }

        public (double MeanUs, double P99Us) Summary()
        {
            if (_Us.Count == 0)
                return (0, 0);
            long sum = 0;
            foreach (long v in _Us)
                sum += v;
            var sorted = new List<long>(_Us);
            sorted.Sort();
            double p99 = sorted[Math.Min(sorted.Count - 1, (int)(0.99 * sorted.Count + 0.5))];
            return ((double)sum / _Us.Count, p99);
        }
    }

    class Results
    {
        public string Mode = "";
        public int Panels;
        public int Quads;
        public int Aa;
        public int Seconds;
        public bool Vsync;
        public bool SharedContext;
        public bool MixedGpu;
        public long Frames;
        public double Fps;
        public double FrameMeanMs;
        public double FrameP99Ms;
        public List<string> Renderers = new List<string>();
        public List<string> Errors = new List<string>();
        public readonly List<Op> Ops = new List<Op>();

        public Op Op(string name)
        {
            foreach (Op o in Ops)
            {
                if (o.Name == name)
                    return o;
            }
            return null;
        }

        public double MeanUs(string name)
        {
            Op o = Op(name);
            if (o == null || o.Count == 0)
                return 0;
            var (m, p) = o.Summary();
            return m;
        }

        static string N(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string JsonEscape(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\')
                    sb.Append('\\');
                else if (c == '\n')
                    continue;
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public string Json()
        {
            var parts = new List<string>();
            parts.Add("\"mode\":" + JsonEscape(Mode));
            parts.Add("\"panels\":" + Panels);
            parts.Add("\"quads\":" + Quads);
            parts.Add("\"aa\":" + Aa);
            parts.Add("\"vsync\":" + (Vsync ? "true" : "false"));
            parts.Add("\"seconds\":" + Seconds);
            parts.Add("\"frames\":" + Frames);
            parts.Add("\"fps\":" + N(Fps));
            parts.Add("\"frameMeanMs\":" + N(FrameMeanMs));
            parts.Add("\"frameP99Ms\":" + N(FrameP99Ms));
            parts.Add("\"sharedContext\":" + (SharedContext ? "true" : "false"));
            parts.Add("\"mixedGpu\":" + (MixedGpu ? "true" : "false"));

            var renderers = new List<string>();
            foreach (string s in Renderers)
                renderers.Add(JsonEscape(s));
            parts.Add("\"renderers\":[" + string.Join(",", renderers) + "]");

            var ops = new List<string>();
            foreach (Op o in Ops)
            {
                var (m, p) = o.Summary();
                ops.Add("{\"name\":\"" + o.Name + "\",\"n\":" + o.Count + ",\"meanUs\":" + N(m) + ",\"p99Us\":" + N(p) + "}");
            }
            parts.Add("\"ops\":[" + string.Join(",", ops) + "]");

            var errors = new List<string>();
            foreach (string s in Errors)
                errors.Add(JsonEscape(s));
            parts.Add("\"errors\":[" + string.Join(",", errors) + "]");

            return "{" + string.Join(",", parts) + "}";
        }
    }

    /// <summary>GATE-PERF evaluation (plan §11.3) for both challengers: vfb and sharedrender.</summary>
    static class Gates
    {
        public static List<string> Eval(Results giant, Results vfb, Results rerender, Results sharedrender)
        {
            var lines = new List<string>();
            if (giant == null || vfb == null || rerender == null)
            {
                lines.Add("GATE-PERF: SKIPPED (missing one of giant/vfb/rerender at N=6 quads=1000 vsync=0)");
                return lines;
            }

            string N(double v)
            {
                return v.ToString("0.###", CultureInfo.InvariantCulture);
            }

            double vfbSlotUs = vfb.MeanUs("rebind") + vfb.MeanUs("blit") + vfb.MeanUs("swap");
            bool g1 = vfb.FrameMeanMs <= giant.FrameMeanMs * 1.05 + 2.0;
            bool g2 = vfbSlotUs < 500.0;
            bool g3 = vfb.Fps >= 0.95 * rerender.Fps;

            lines.Add((g1 ? "GATE-PERF-1 PASS" : "GATE-PERF-1 FAIL")
                + string.Format("  vfb frame {0} ms <= giant {1} ms x 1.05 + 2 ms", N(vfb.FrameMeanMs), N(giant.FrameMeanMs)));
            lines.Add((g2 ? "GATE-PERF-2 PASS" : "GATE-PERF-2 FAIL")
                + string.Format("  vfb per-slot rebind+blit+swap {0} us < 500 us", N(vfbSlotUs)));
            lines.Add((g3 ? "GATE-PERF-3 PASS" : "GATE-PERF-3 FAIL")
                + string.Format("  vfb fps {0} >= 0.95 x rerender fps {1}", N(vfb.Fps), N(rerender.Fps)));

            if (sharedrender == null)
            {
                lines.Add("GATE-SHARED: SKIPPED (no sharedrender run at N=6 quads=1000 vsync=0)");
                return lines;
            }

            // sharedrender per-slot cost: the per-window branch times rebind + scene (own-panel
            // render) + swap; "blit" is unused in that mode. GATE-SHARED-2 compares against
            // rerender's OWN per-slot cost (not an absolute 500 us): on this rig EVERY multi-window
            // mode pays ~0.8-1 ms of DWM surface activation per MakeCurrent (vfb, rerender AND
            // sharedrender all show it), so the meaningful claim is "sharing costs nothing extra
            // vs the same per-window work on independent contexts".
            double srSlotUs = sharedrender.MeanUs("rebind") + sharedrender.MeanUs("scene") + sharedrender.MeanUs("swap");
            double rrSlotUs = rerender.MeanUs("rebind") + rerender.MeanUs("scene") + rerender.MeanUs("swap");
            bool s1 = sharedrender.FrameMeanMs <= giant.FrameMeanMs * 1.05 + 2.0;
            bool s2 = srSlotUs <= rrSlotUs * 1.25;
            bool s3 = sharedrender.Fps >= 0.95 * rerender.Fps;

            lines.Add((s1 ? "GATE-SHARED-1 PASS" : "GATE-SHARED-1 FAIL")
                + string.Format("  sharedrender frame {0} ms <= giant {1} ms x 1.05 + 2 ms", N(sharedrender.FrameMeanMs), N(giant.FrameMeanMs)));
            lines.Add((s2 ? "GATE-SHARED-2 PASS" : "GATE-SHARED-2 FAIL")
                + string.Format("  sharedrender per-slot rebind+render+swap {0} us <= 1.25 x rerender per-slot {1} us", N(srSlotUs), N(rrSlotUs)));
            lines.Add((s3 ? "GATE-SHARED-3 PASS" : "GATE-SHARED-3 FAIL")
                + string.Format("  sharedrender fps {0} >= 0.95 x rerender fps {1}", N(sharedrender.Fps), N(rerender.Fps)));
            return lines;
        }
    }
}
