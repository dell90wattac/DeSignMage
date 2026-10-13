using System.Globalization;
using System.IO.Compression;
using System.Text;
using DeSignMage.Core.Model;
using SkiaSharp;

namespace DeSignMage.Files.Tests;

/// <summary>
/// A tiny PostScript interpreter for exactly what the EPS writer emits (paths, fills, strokes, clips, colors, matrices, axial/radial shadings, flate+ASCII85
/// pictures with a color key), drawing with Skia. It exists so the tests can look at what an EPS file draws: no PostScript interpreter is installed here.
/// Anything it does not know is recorded in <see cref="Unknown"/>, which tests assert is empty.
/// </summary>
internal sealed class MiniPostScript
{
    private readonly record struct Name(string Text);
    private sealed class Mark(string kind) { public string Kind { get; } = kind; }
    private sealed class SaveMarker;
    private sealed class CurrentFile;
    private sealed class FilterSource;

    private sealed class State
    {
        public SKMatrix Ctm;
        public SKColor Color = SKColors.Black;
        public double LineWidth = 1, MiterLimit = 10;
        public SKStrokeJoin Join = SKStrokeJoin.Miter;
        public SKStrokeCap Cap = SKStrokeCap.Butt;
        public State Clone() => (State)MemberwiseClone();
    }

    private readonly List<object> _stack = [];
    private readonly Stack<State> _saved = new();
    private readonly Stack<SKPath> _savedPaths = new();   // gsave keeps the current path too (the "gsave fill grestore stroke" idiom relies on it)
    private State _state = new();
    private SKPath _path = new();
    private SKCanvas _canvas = null!;
    private string _text = "";
    private int _pos;

    public List<string> Unknown { get; } = [];
    public int PicturesDrawn { get; private set; }
    public int ShadingsDrawn { get; private set; }
    public int Fills { get; private set; }
    public int Strokes { get; private set; }

    /// <summary>The bounding box as written (integer corners).</summary>
    public (int W, int H) BoundingBox { get; private set; }

    public SKBitmap Render(string eps, double scale = 1)
    {
        _text = eps;
        _pos = 0;
        var bb = System.Text.RegularExpressions.Regex.Match(eps, @"%%BoundingBox:\s*(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)");
        if (!bb.Success) throw new InvalidDataException("no BoundingBox");
        int llx = int.Parse(bb.Groups[1].Value), lly = int.Parse(bb.Groups[2].Value), urx = int.Parse(bb.Groups[3].Value), ury = int.Parse(bb.Groups[4].Value);
        BoundingBox = (urx - llx, ury - lly);
        int w = (int)Math.Ceiling((urx - llx) * scale), h = (int)Math.Ceiling((ury - lly) * scale);
        var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        bmp.Erase(SKColors.Transparent);
        _canvas = new SKCanvas(bmp);
        // PostScript's page is y up with its origin at the lower left; the bitmap is y down.
        _state.Ctm = SKMatrix.CreateScale((float)scale, (float)-scale).PostConcat(SKMatrix.CreateTranslation(0, h)).PreConcat(SKMatrix.CreateTranslation(-llx, -lly));
        Run();
        _canvas.Dispose();
        return bmp;
    }

    // ---------------------------------------------------------------- tokens

    private object? Next()
    {
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (char.IsWhiteSpace(c)) { _pos++; continue; }
            if (c == '%') { while (_pos < _text.Length && _text[_pos] != '\n') _pos++; continue; }
            break;
        }
        if (_pos >= _text.Length) return null;
        char ch = _text[_pos];
        if (ch == '<' && _pos + 1 < _text.Length && _text[_pos + 1] == '<') { _pos += 2; return "<<"; }
        if (ch == '>' && _pos + 1 < _text.Length && _text[_pos + 1] == '>') { _pos += 2; return ">>"; }
        if (ch == '<')
        {
            int end = _text.IndexOf('>', _pos);
            var hex = new string(_text[(_pos + 1)..end].Where(Uri.IsHexDigit).ToArray());
            _pos = end + 1;
            return Convert.FromHexString(hex);
        }
        if (ch is '[' or ']') { _pos++; return ch.ToString(); }
        int start = _pos;
        if (ch == '/') _pos++;
        while (_pos < _text.Length && !char.IsWhiteSpace(_text[_pos]) && _text[_pos] is not ('[' or ']' or '<' or '>' or '%')) _pos++;
        var word = _text[start.._pos];
        if (word.StartsWith('/')) return new Name(word[1..]);
        if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        return word;
    }

    private void Run()
    {
        for (var token = Next(); token is not null; token = Next())
        {
            if (token is string word) Execute(word);
            else _stack.Add(token);
        }
    }

    private double PopNumber() { var v = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return (double)v; }
    private object Pop() { var v = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return v; }

    private void Execute(string op)
    {
        switch (op)
        {
            case "[": _stack.Add(new Mark("[")); break;
            case "<<": _stack.Add(new Mark("<<")); break;
            case "]":
            {
                int m = _stack.FindLastIndex(o => o is Mark { Kind: "[" });
                var items = _stack.Skip(m + 1).ToList();
                _stack.RemoveRange(m, _stack.Count - m);
                _stack.Add(items);
                break;
            }
            case ">>":
            {
                int m = _stack.FindLastIndex(o => o is Mark { Kind: "<<" });
                var items = _stack.Skip(m + 1).ToList();
                _stack.RemoveRange(m, _stack.Count - m);
                var dict = new Dictionary<string, object>();
                for (int i = 0; i + 1 < items.Count; i += 2) dict[((Name)items[i]).Text] = items[i + 1];
                _stack.Add(dict);
                break;
            }
            case "true": _stack.Add(true); break;
            case "false": _stack.Add(false); break;
            case "currentfile": _stack.Add(new CurrentFile()); break;
            case "filter": Pop(); Pop(); _stack.Add(new FilterSource()); break;
            case "save": PushState(); _stack.Add(new SaveMarker()); break;
            case "restore": Pop(); PopState(); break;
            case "gsave": PushState(); break;
            case "grestore": PopState(); break;
            case "newpath": _path.Dispose(); _path = new SKPath(); break;
            case "moveto": { double y = PopNumber(), x = PopNumber(); _path.MoveTo(Device(x, y)); break; }
            case "lineto": { double y = PopNumber(), x = PopNumber(); _path.LineTo(Device(x, y)); break; }
            case "curveto":
            {
                double y3 = PopNumber(), x3 = PopNumber(), y2 = PopNumber(), x2 = PopNumber(), y1 = PopNumber(), x1 = PopNumber();
                _path.CubicTo(Device(x1, y1), Device(x2, y2), Device(x3, y3));
                break;
            }
            case "closepath": _path.Close(); break;
            case "fill": case "eofill":
                _path.FillType = op == "eofill" ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
                using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = _state.Color }) _canvas.DrawPath(_path, paint);
                Fills++;
                Execute("newpath");
                break;
            case "stroke":
                using (var paint = new SKPaint
                {
                    IsAntialias = true, Style = SKPaintStyle.Stroke, Color = _state.Color,
                    StrokeWidth = (float)(_state.LineWidth * Math.Sqrt(Math.Abs(_state.Ctm.ScaleX * _state.Ctm.ScaleY - _state.Ctm.SkewX * _state.Ctm.SkewY))),
                    StrokeJoin = _state.Join, StrokeCap = _state.Cap, StrokeMiter = (float)_state.MiterLimit,
                }) _canvas.DrawPath(_path, paint);
                Strokes++;
                Execute("newpath");
                break;
            case "clip": case "eoclip":
                _path.FillType = op == "eoclip" ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
                _canvas.ClipPath(_path, SKClipOperation.Intersect, true);
                break;
            case "setrgbcolor": { double b = PopNumber(), g = PopNumber(), r = PopNumber(); _state.Color = new SKColor(B(r), B(g), B(b)); break; }
            case "setcmykcolor":
            {
                double k = PopNumber(), y = PopNumber(), m = PopNumber(), c = PopNumber();
                var (r, g, b) = ColorMath.CmykToRgb(c * 100, m * 100, y * 100, k * 100);
                _state.Color = new SKColor(r, g, b);
                break;
            }
            case "setlinewidth": _state.LineWidth = PopNumber(); break;
            case "setmiterlimit": _state.MiterLimit = PopNumber(); break;
            case "setlinejoin": _state.Join = (int)PopNumber() switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter }; break;
            case "setlinecap": _state.Cap = (int)PopNumber() switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt }; break;
            case "setcolorspace": Pop(); break;
            case "translate": { double ty = PopNumber(), tx = PopNumber(); _state.Ctm = _state.Ctm.PreConcat(SKMatrix.CreateTranslation((float)tx, (float)ty)); break; }
            case "scale": { double sy = PopNumber(), sx = PopNumber(); _state.Ctm = _state.Ctm.PreConcat(SKMatrix.CreateScale((float)sx, (float)sy)); break; }
            case "concat": { _state.Ctm = _state.Ctm.PreConcat(Matrix((List<object>)Pop())); break; }
            case "shfill": Shade((Dictionary<string, object>)Pop()); break;
            case "image": Picture((Dictionary<string, object>)Pop()); break;
            default: Unknown.Add(op); break;
        }
    }

    private void PushState()
    {
        _saved.Push(_state.Clone());
        _savedPaths.Push(new SKPath(_path));
        _canvas.Save();
    }

    private void PopState()
    {
        _state = _saved.Pop();
        _path.Dispose();
        _path = _savedPaths.Pop();
        _canvas.Restore();
    }

    private static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private SKPoint Device(double x, double y) => _state.Ctm.MapPoint((float)x, (float)y);

    /// <summary>PostScript [a b c d e f] (x' = ax + cy + e, y' = bx + dy + f) as a Skia matrix.</summary>
    private static SKMatrix Matrix(List<object> m)
    {
        double a = (double)m[0], b = (double)m[1], c = (double)m[2], d = (double)m[3], e = (double)m[4], f = (double)m[5];
        return new SKMatrix((float)a, (float)c, (float)e, (float)b, (float)d, (float)f, 0, 0, 1);
    }

    // ---------------------------------------------------------------- shading

    private void Shade(Dictionary<string, object> d)
    {
        int type = (int)(double)d["ShadingType"];
        var coords = ((List<object>)d["Coords"]).Select(o => (float)(double)o).ToArray();
        var function = (Dictionary<string, object>)d["Function"];
        var data = (byte[])function["DataSource"];
        int n = data.Length / 3;
        var colors = new SKColor[n];
        var positions = new float[n];
        for (int i = 0; i < n; i++)
        {
            colors[i] = new SKColor(data[3 * i], data[3 * i + 1], data[3 * i + 2]);
            positions[i] = i / (float)(n - 1);
        }
        using var shader = type == 2
            ? SKShader.CreateLinearGradient(new SKPoint(coords[0], coords[1]), new SKPoint(coords[2], coords[3]), colors, positions, SKShaderTileMode.Clamp, _state.Ctm)
            : SKShader.CreateRadialGradient(new SKPoint(coords[3], coords[4]), coords[5], colors, positions, SKShaderTileMode.Clamp, _state.Ctm);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        _canvas.DrawPaint(paint);
        ShadingsDrawn++;
    }

    // ---------------------------------------------------------------- pictures

    private void Picture(Dictionary<string, object> d)
    {
        int w = (int)(double)d["Width"], h = (int)(double)d["Height"];
        var matrix = Matrix((List<object>)d["ImageMatrix"]);
        (byte R, byte G, byte B)? key = d.TryGetValue("MaskColor", out var mc) && mc is List<object> k
            ? ((byte)(double)k[0], (byte)(double)k[1], (byte)(double)k[2]) : null;

        // the data follows the "image" operator, ASCII85 text ending with ~>
        int end = _text.IndexOf("~>", _pos, StringComparison.Ordinal);
        var raw = Ascii85Decode(_text[_pos..end]);
        _pos = end + 2;
        using var inflated = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        var rgb = new byte[w * h * 3];
        inflated.ReadExactly(rgb);

        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = 3 * (y * w + x);
                bool masked = key is { } kk && rgb[p] == kk.R && rgb[p + 1] == kk.G && rgb[p + 2] == kk.B;
                bmp.SetPixel(x, y, masked ? SKColors.Transparent : new SKColor(rgb[p], rgb[p + 1], rgb[p + 2]));
            }
        // pixel -> user space is the inverse of the image matrix, then the current matrix carries it to the bitmap
        matrix.TryInvert(out var inverse);
        _canvas.Save();
        _canvas.Concat(_state.Ctm.PreConcat(inverse));
        _canvas.DrawBitmap(bmp, 0, 0);
        _canvas.Restore();
        PicturesDrawn++;
    }

    private static byte[] Ascii85Decode(string text)
    {
        var output = new List<byte>();
        var group = new List<int>();
        void Flush(int count)
        {
            uint v = 0;
            for (int i = 0; i < 5; i++) v = v * 85 + (uint)(i < group.Count ? group[i] : 84);
            for (int i = 0; i < count - 1; i++) output.Add((byte)(v >> (24 - 8 * i)));
            group.Clear();
        }
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c == 'z' && group.Count == 0) { output.AddRange(new byte[4]); continue; }
            group.Add(c - '!');
            if (group.Count == 5) Flush(5);
        }
        if (group.Count > 1) Flush(group.Count);
        return output.ToArray();
    }
}
