using System.Diagnostics;
using System.Globalization;
using DeSignMage.Core.Model;
using DeSignMage.Vectorize;
using DeSignMage.Vectorize.Imaging;
using SkiaSharp;

namespace DeSignMage.Vectorize.Cli;

internal static class Program
{
    private const string Usage = """
        dsm-vectorize: trace a bitmap into vector shapes (the engine behind Image > Vectorize).

        Usage:
          dsm-vectorize <image> [options]
          dsm-vectorize --make-samples <folder>

        Output (default: <image>.svg next to the input):
          -o, --svg <file>        write SVG here
          --compare <file.png>    write a side-by-side PNG: original | vectorized (on white) | detail crop

        Options (defaults in brackets):
          --mode color|bw|outline          [color]
          --layering stacked|cutout        [stacked]
          --colors N       2-256 colors    [16]
          --speckle N      noise filter, smallest region in pixels [8]
          --denoise N      0-3 median passes before tracing [0]
          --smooth X       0-1 curve smoothness [0.5]
          --corner DEG     corner angle 10-170 [50]
          --threshold N    1-255 black/white cut-off, 0 = automatic [0]
          --max-size N     trace at most this many pixels on the long side [1400]
          --min-size N     enlarge small images to this many pixels on the long side before tracing [1000]
          --omit-background        drop the region behind everything
          --zoom X,Y [--zoom-size N]       center and size (source pixels) of the detail crop in --compare
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help")) { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
        try
        {
            if (args[0] == "--make-samples")
            {
                if (args.Length < 2) { Console.Error.WriteLine("Give a folder."); return 1; }
                SampleImages.Make(args[1]);
                return 0;
            }
            return Run(args);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        string? input = null, svgPath = null, comparePath = null;
        int? zoomX = null, zoomY = null, zoomSize = 96;
        var o = new VectorizeOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            double D() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "-o": case "--svg": svgPath = Next(); break;
                case "--compare": comparePath = Next(); break;
                case "--zoom": { var xy = Next().Split(","); zoomX = int.Parse(xy[0], CultureInfo.InvariantCulture); zoomY = int.Parse(xy[1], CultureInfo.InvariantCulture); break; }
                case "--zoom-size": zoomSize = (int)D(); break;
                case "--mode": o = o with { Mode = Next().ToLowerInvariant() switch { "color" => VectorizeMode.Color, "bw" => VectorizeMode.BlackAndWhite, "outline" => VectorizeMode.Outline, var m => throw new ArgumentException($"Unknown mode '{m}'.") } }; break;
                case "--layering": o = o with { Layering = Next().ToLowerInvariant() switch { "stacked" => VectorizeLayering.Stacked, "cutout" => VectorizeLayering.CutOut, var m => throw new ArgumentException($"Unknown layering '{m}'.") } }; break;
                case "--colors": o = o with { Colors = (int)D() }; break;
                case "--speckle": o = o with { SpeckleSize = (int)D() }; break;
                case "--denoise": o = o with { Denoise = (int)D() }; break;
                case "--smooth": o = o with { Smoothness = D() }; break;
                case "--corner": o = o with { CornerAngle = D() }; break;
                case "--threshold": o = o with { Threshold = (int)D() }; break;
                case "--max-size": o = o with { MaxWorkingSize = (int)D() }; break;
                case "--min-size": o = o with { MinWorkingSize = (int)D() }; break;
                case "--omit-background": o = o with { OmitBackground = true }; break;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"Unknown option {a}.");
                    input = a;
                    break;
            }
        }
        if (input is null) throw new ArgumentException("No input image given.");
        svgPath ??= Path.ChangeExtension(input, ".svg");

        var watch = Stopwatch.StartNew();
        var image = SkiaImaging.Decode(input);
        long decodeMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var result = Vectorizer.Trace(image, o);
        long traceMs = watch.ElapsedMilliseconds;

        File.WriteAllText(svgPath, SvgWriter.ToSvg(result, o.Mode == VectorizeMode.Color ? null : DocColor.White));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(input)}: {image.Width}x{image.Height}  {result.Shapes.Count} shapes, {result.NodeCount} nodes, {result.Palette.Count} colors  (decode {decodeMs} ms, trace {traceMs} ms)  -> {svgPath}"));

        if (comparePath is not null)
        {
            Compare(image, result, comparePath, zoomX, zoomY, zoomSize ?? 96);
            Console.WriteLine("comparison -> " + comparePath);
        }
        return 0;
    }

    /// <summary>Original | traced result | 4x zoom of the same detail in both.</summary>
    private static void Compare(RasterImage image, VectorizeResult result, string path, int? zx0, int? zy0, int size)
    {
        double scale = Math.Min(1.0, 520.0 / Math.Max(image.Width, image.Height));
        if (scale < 1) scale = Math.Max(scale, 0.25);
        int w = (int)Math.Round(image.Width * scale), h = (int)Math.Round(image.Height * scale);
        const int gap = 12;
        int cropW = Math.Min(image.Width, size), cropH = Math.Min(image.Height, size);
        int cropX = Math.Clamp((zx0 ?? image.Width / 2) - cropW / 2, 0, image.Width - cropW), cropY = Math.Clamp((zy0 ?? image.Height / 2) - cropH / 2, 0, image.Height - cropH);
        int zoom = Math.Max(1, 384 / size);
        int panelH = Math.Max(h, cropH * zoom * 2 + gap);
        int total = w * 2 + cropW * zoom + gap * 4;

        using var surface = new SKBitmap(new SKImageInfo(total, panelH + gap * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(surface);
        canvas.Clear(new SKColor(0xE8, 0xE8, 0xE8));
        using var src = SkiaImaging.ToBitmap(image);
        using var srcImage = SKImage.FromBitmap(src);

        // original
        canvas.DrawImage(srcImage, new SKRect(0, 0, image.Width, image.Height), new SKRect(gap, gap, gap + w, gap + h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        // traced
        canvas.Save();
        canvas.Translate(gap * 2 + w, gap);
        canvas.ClipRect(new SKRect(0, 0, w, h));
        canvas.DrawRect(new SKRect(0, 0, w, h), new SKPaint { Color = SKColors.White });
        SkiaImaging.Draw(canvas, result, scale);
        canvas.Restore();

        // zoomed detail: top = original (nearest neighbor), bottom = traced
        int zx = gap * 3 + w * 2;
                canvas.DrawImage(srcImage, new SKRect(cropX, cropY, cropX + cropW, cropY + cropH), new SKRect(zx, gap, zx + cropW * zoom, gap + cropH * zoom), new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Save();
        canvas.ClipRect(new SKRect(zx, gap * 2 + cropH * zoom, zx + cropW * zoom, gap * 2 + cropH * zoom * 2));
        canvas.Translate(zx, gap * 2 + cropH * zoom);
        canvas.DrawRect(new SKRect(0, 0, cropW * zoom, cropH * zoom), new SKPaint { Color = SKColors.White });
        canvas.Scale(zoom, zoom);
        canvas.Translate(-cropX, -cropY);
        SkiaImaging.Draw(canvas, result, 1);
        canvas.Restore();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, SkiaImaging.EncodePng(surface));
    }
}



