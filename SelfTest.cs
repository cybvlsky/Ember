using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Ember;

/// `Ember.exe --selftest`: protocol packing, colour conversion, the diff sender against a virtual panel, every theme.
/// `Ember.exe --render <dir>`: PNG of each theme with demo data plus how much bandwidth it needs while animating.
public static class SelfTest
{
    public static int Run()
    {
        var log = new List<string>();
        int fails = 0;
        void Check(bool ok, string what) { log.Add((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; }

        Span<byte> b = stackalloc byte[6];
        TuringLcd.Pack(b, 0, 0, 479, 319, 197);
        Check(b.SequenceEqual(new byte[] { 0, 0, 7, 125, 63, 197 }), "pack full screen rect");
        TuringLcd.Pack(b, 123, 45, 200, 99, 197);
        Check(b.SequenceEqual(new byte[] { 30, 194, 211, 32, 99, 197 }), "pack arbitrary rect");

        using (var bmp = new SKBitmap(FrameDiff.W, FrameDiff.H, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            var px = new ushort[FrameDiff.W * FrameDiff.H];
            bmp.Erase(SKColors.White); FrameDiff.To565(bmp, px);
            Check(px.All(p => p == 0xFFFF), "565 white");
            bmp.Erase(SKColors.Black); FrameDiff.To565(bmp, px);
            Check(px.All(p => p == 0), "565 black");
            bmp.Erase(new SKColor(255, 0, 0)); FrameDiff.To565(bmp, px);
            Check(px.All(p => p == 0xF800), "565 red");
            bmp.Erase(new SKColor(0, 0, 255)); FrameDiff.To565(bmp, px);
            Check(px.All(p => p == 0x001F), "565 blue");
            bmp.Erase(new SKColor(100, 100, 100)); FrameDiff.To565(bmp, px);
            double avg = px.Average(p => (p >> 11) * 255.0 / 31);
            Check(Math.Abs(avg - 100) < 4, $"565 dither keeps mean grey ({avg:0.0})");
        }

        {
            var rnd = new Random(7);
            var panel = new ushort[FrameDiff.W * FrameDiff.H];
            long sent = 0;
            BlitFn blit = (x, y, w, h, data) =>
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(data);
                for (int r = 0; r < h; r++) src.Slice(r * w, w).CopyTo(panel.AsSpan((y + r) * FrameDiff.W + x, w));
                sent += data.Length;
            };
            var diff = new FrameDiff();
            var next = new ushort[panel.Length];
            for (int i = 0; i < next.Length; i++) next[i] = (ushort)rnd.Next(65536);
            int rounds = Drain(diff, next, blit);
            Check(panel.AsSpan().SequenceEqual(next), $"full frame converges ({rounds} rounds)");
            Check(sent == next.Length * 2, $"full frame sent once ({sent} B)");
            bool all = true;
            for (int round = 0; round < 60; round++)
            {
                int k = rnd.Next(1, 6);
                for (int j = 0; j < k; j++)
                {
                    int w = rnd.Next(1, 90), h = rnd.Next(1, 70), x = rnd.Next(0, FrameDiff.W - w), y = rnd.Next(0, FrameDiff.H - h);
                    ushort c = (ushort)rnd.Next(65536);
                    for (int yy = y; yy < y + h; yy++) next.AsSpan(yy * FrameDiff.W + x, w).Fill(c);
                }
                Drain(diff, next, blit);
                all &= panel.AsSpan().SequenceEqual(next);
            }
            Check(all, "60 rounds of random partial updates match the panel");
            sent = 0;
            next[200 * FrameDiff.W + 300] ^= 0x1234;
            Drain(diff, next, blit);
            Check(sent == 2, $"single pixel change costs 2 B ({sent} B)");
            diff.Invalidate();
            sent = 0;
            Drain(diff, next, blit);
            Check(sent == next.Length * 2 && panel.AsSpan().SequenceEqual(next), "invalidate resends everything");
        }

        Check(Renderer.Format(double.NaN, null) == "--", "format NaN");
        Check(Sensors.PcieHook != null, "LHM NVIDIA PCIe throughput hook present");
        L.Lang = "pl";
        Check(Renderer.Format(12.6e6, "rate") == "12,0 MB/s", $"format rate, Polish ({Renderer.Format(12.6e6, "rate")})");
        Check(Renderer.Format(5280, "ghz") == "5,28", "format ghz, Polish");
        L.Lang = "en";
        Check(Renderer.Format(12.6e6, "rate") == "12.0 MB/s", $"format rate ({Renderer.Format(12.6e6, "rate")})");
        Check(Renderer.Format(500_000, "rate") == "488 KB/s", $"format rate KB ({Renderer.Format(500_000, "rate")})");
        Check(Renderer.Format(5280, "ghz") == "5.28", "format ghz");
        // A Polish string that lost or renamed a {0} or {cpu.temp:0} would throw in string.Format or show the wrong value.
        static string Holes(string t) => string.Join(" ", System.Text.RegularExpressions.Regex.Matches(t, @"\{[^}]*\}").Select(m => m.Value).Order());
        var broken = L.Polish.Where(kv => Holes(kv.Key) != Holes(kv.Value)).Select(kv => kv.Key).ToList();
        Check(broken.Count == 0, $"Polish strings keep their placeholders ({string.Join(" | ", broken)})");
        Check(Themes.NewId("Mój Motyw ąę", []) == "moj-motyw-ae", $"theme id ({Themes.NewId("Mój Motyw ąę", [])})");
        Check(Renderer.ParseHex("#FF8000") == new SKColor(255, 128, 0) && Renderer.ParseHex("#FF800080") == new SKColor(255, 128, 0, 128), "hex colours");

        var themes = Themes.LoadAll();
        Check(themes.Count(t => t.BuiltIn) >= 6, $"built-in themes load ({themes.Count(t => t.BuiltIn)})");
        int before = Renderer.Errors;
        foreach (var t in themes)
        {
            foreach (var lang in L.Languages)
            {
                L.Lang = lang;
                using var bmp = Renderer.Still(t, Snapshot.Demo(100));
                using var empty = Renderer.Still(t, Snapshot.Empty);
            }
            L.Lang = "en";
            var back = Theme.Parse(t.ToJson());
            Check(back.Widgets.Count == t.Widgets.Count && back.ToJson() == t.ToJson(), $"theme {t.Id} JSON round trip");
        }
        using (var intro = Icons.Intro()) Check(intro.Width == FrameDiff.W, "start-up card renders");
        Check(Renderer.Errors == before, $"themes render in both languages without errors ({Renderer.Errors - before})");

        log.Add(fails == 0 ? "ALL OK" : $"{fails} FAILED");
        File.WriteAllLines(Path.Combine(App.DataDir, "selftest.txt"), log);
        foreach (var l in log) Console.WriteLine(l);
        return fails;
    }

    static int Drain(FrameDiff diff, ushort[] next, BlitFn blit)
    {
        int rounds = 0;
        double t = 0;
        while (true)
        {
            rounds++;
            t += 1 / 30.0;
            int n = diff.Send(next, t, 5200, blit, out bool pending);
            if (n == 0 && !pending) return rounds;
            if (rounds > 10000) throw new Exception("diff does not converge");
        }
    }

    /// `--render <dir> [pl]`: a PNG per theme, its bandwidth heat map, the start-up card, the logo, and 4 s of
    /// frames from the default theme (the README animation is made from those).
    public static int Render(string dir)
    {
        Directory.CreateDirectory(dir);
        using (var intro = Icons.Intro()) using (var f = File.Create(Path.Combine(dir, "intro.png"))) intro.Encode(f, SKEncodedImageFormat.Png, 100);
        foreach (int size in new[] { 512, 64 })
            using (var logo = Icons.Logo(size)) using (var f = File.Create(Path.Combine(dir, $"logo-{size}.png"))) logo.Encode(f, SKEncodedImageFormat.Png, 100);
        var frames = Path.Combine(dir, "frames");
        Directory.CreateDirectory(frames);
        using (var r = new Renderer(Themes.LoadAll().First(t => t.Id == "studio")))
        using (var frame = new SKBitmap(FrameDiff.W, FrameDiff.H, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (var c = new SKCanvas(frame))
            for (int i = 0; i < 150; i++)
            {
                double now = 200 + i / 30.0;
                r.Render(c, Snapshot.Demo(Math.Floor(now)), i == 0 ? 0 : 1 / 30.0, now);
                if (i < 30) continue;
                using var f = File.Create(Path.Combine(frames, $"{i - 30:000}.png"));
                frame.Encode(f, SKEncodedImageFormat.Png, 100);
            }
        var lines = new List<string>();
        foreach (var t in Themes.LoadAll())
        {
            using (var bmp = Renderer.Still(t, Snapshot.Demo(100)))
            using (var f = File.Create(Path.Combine(dir, t.Id + ".png")))
                bmp.Encode(f, SKEncodedImageFormat.Png, 100);

            // Ten simulated seconds at 30 fps with a new sample every second: what the panel would have to receive.
            using var r = new Renderer(t);
            using var frame = new SKBitmap(FrameDiff.W, FrameDiff.H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var c = new SKCanvas(frame);
            var diff = new FrameDiff();
            var next = new ushort[FrameDiff.W * FrameDiff.H];
            long bytes = 0, peak = 0;
            double renderMs = 0;
            var heat = new int[FrameDiff.W * FrameDiff.H];
            bool counting = false;
            BlitFn count = (x, y, w, h, _) =>
            {
                bytes += w * h * 2;
                if (counting) for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) heat[yy * FrameDiff.W + xx]++;
            };
            for (int i = 0; i < 330; i++)
            {
                double now = 100 + i / 30.0;
                var s = Snapshot.Demo(Math.Floor(now));
                var sw = Stopwatch.StartNew();
                r.Render(c, s, 1 / 30.0, now);
                if (i >= 30) renderMs += sw.Elapsed.TotalMilliseconds;
                FrameDiff.To565(frame, next);
                long before = bytes;
                diff.Send(next, now, double.MaxValue, count, out _);
                if (i == 29) { bytes = 0; counting = true; }
                if (i >= 30) peak = Math.Max(peak, bytes - before);
            }
            using (var still = Renderer.Still(t, Snapshot.Demo(100)))
            {
                // Bandwidth heat map: how many of the 300 frames resent each pixel.
                using var hc = new SKCanvas(still);
                using var p = new SKPaint();
                for (int y = 0; y < FrameDiff.H; y++)
                    for (int x = 0; x < FrameDiff.W; x++)
                        if (heat[y * FrameDiff.W + x] is int hv and > 0)
                        {
                            p.Color = new SKColor(255, 40, 160, (byte)Math.Min(230, 40 + hv * 2));
                            hc.DrawPoint(x, y, p);
                        }
                using var hf = File.Create(Path.Combine(dir, t.Id + ".heat.png"));
                still.Encode(hf, SKEncodedImageFormat.Png, 100);
            }
            lines.Add($"{t.Id,-16} {bytes / 10.0 / 1024,7:0.0} KB/s ({bytes / 10.0 / FrameDiff.Bandwidth * 100,4:0}% of the link)  peak frame {peak / 1024.0,6:0.0} KB  render {renderMs / 300,5:0.00} ms");
        }
        File.WriteAllLines(Path.Combine(dir, "bandwidth.txt"), lines);
        foreach (var l in lines) Console.WriteLine(l);
        return 0;
    }
}
