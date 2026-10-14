using System.IO.Compression;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Vector.Pdf;

namespace DeSignMage.Formats.Vector.Eps;

/// <summary>A PostScript file object: the program text itself (<c>currentfile</c>) or a decoding filter over another source.</summary>
internal sealed class PsFile(PsInterpreter owner, string? filter = null, object? source = null)
{
    public string? Filter { get; } = filter;
    public object? Source { get; } = source;
    public bool IsDct => Filter is "DCTDecode" || (Source is PsFile f && f.IsDct);

    public byte[] Decode(int need) => owner.DecodeFile(this, need);
}

internal sealed partial class PsInterpreter
{
    internal byte[] DecodeFile(PsFile file, int need)
    {
        switch (file.Filter)
        {
            case null:
            {
                // the program text: the data starts after the end of the line the operator is on
                int p = _scanner.Position;
                var d = _scanner.Data;
                if (p < _scanner.End && d[p] == 13) p++;
                if (p < _scanner.End && d[p] == 10) p++;
                else if (p < _scanner.End && d[p] is 32 or 9) p++;
                int n = Math.Max(0, Math.Min(need, _scanner.End - p));
                var bytes = d[p..(p + n)];
                _scanner.Position = p + n;
                return bytes;
            }
            case "ASCII85Decode":
            {
                if (file.Source is PsFile { Filter: null })
                {
                    int p = _scanner.Position;
                    var bytes = Ascii85.Decode(_scanner.Data, ref p, _scanner.End);
                    _scanner.Position = p;
                    return bytes;
                }
                var raw = SourceBytes(file.Source, int.MaxValue);
                int q = 0;
                return Ascii85.Decode(raw, ref q, raw.Length);
            }
            case "ASCIIHexDecode":
            {
                byte[] raw; int p;
                if (file.Source is PsFile { Filter: null }) { raw = _scanner.Data; p = _scanner.Position; }
                else { raw = SourceBytes(file.Source, int.MaxValue); p = 0; }
                int end = file.Source is PsFile { Filter: null } ? _scanner.End : raw.Length;
                var result = new List<byte>();
                int hi = -1;
                while (p < end)
                {
                    byte c = raw[p++];
                    if (c == '>') break;
                    if (!Uri.IsHexDigit((char)c)) continue;
                    int v = Convert.ToInt32(((char)c).ToString(), 16);
                    if (hi < 0) hi = v; else { result.Add((byte)(hi * 16 + v)); hi = -1; }
                }
                if (hi >= 0) result.Add((byte)(hi * 16));
                if (file.Source is PsFile { Filter: null }) _scanner.Position = p;
                return result.ToArray();
            }
            case "FlateDecode":
            {
                var input = SourceBytes(file.Source, int.MaxValue);
                try
                {
                    using var z = new ZLibStream(new MemoryStream(input), CompressionMode.Decompress);
                    using var ms = new MemoryStream();
                    z.CopyTo(ms);
                    return ms.ToArray();
                }
                catch (InvalidDataException) { throw new PsError("compressed picture data is damaged"); }
            }
            case "RunLengthDecode":
            {
                var input = SourceBytes(file.Source, int.MaxValue);
                var result = new List<byte>();
                for (int i = 0; i < input.Length;)
                {
                    int len = input[i++];
                    if (len == 128) break;
                    if (len < 128) { int n = Math.Min(len + 1, input.Length - i); for (int k = 0; k < n; k++) result.Add(input[i + k]); i += n; }
                    else if (i < input.Length) { byte b = input[i++]; for (int k = 0; k < 257 - len; k++) result.Add(b); }
                }
                return result.ToArray();
            }
            case "DCTDecode":
                return SourceBytes(file.Source, int.MaxValue);
            default:
                throw new PsError($"the {file.Filter} filter is not supported");
        }
    }

    /// <summary>The bytes of a data source: a string, a file, or a procedure that leaves strings on the stack.</summary>
    private byte[] SourceBytes(object? source, int need)
    {
        switch (source)
        {
            case PsStr s: return s.Bytes;
            case PsFile f: return f.Decode(need);
            case PsArr { Executable: true } proc:
            {
                var result = new List<byte>();
                for (int i = 0; i < 1_000_000 && result.Count < need; i++)
                {
                    int mark = _stack.Count;
                    RunProcedure(proc);
                    if (_stack.Count <= mark || _stack[^1] is not PsStr str || str.Bytes.Length == 0) { if (_stack.Count > mark) _stack.RemoveRange(mark, _stack.Count - mark); break; }
                    result.AddRange(str.Bytes);
                    _stack.RemoveRange(mark, _stack.Count - mark);
                }
                return result.ToArray();
            }
            default: return [];
        }
    }

    private bool Filter()
    {
        string name = PopName();
        object? parms = null;
        if (Peek() is PsDict) parms = Pop();
        var source = Pop();
        _ = parms;
        Push(new PsFile(this, name, source));
        return true;
    }

    /// <summary><c>file string readhexstring</c> and friends on the program text.</summary>
    private bool ReadFromFile(string op)
    {
        var buf = Pop() as PsStr ?? throw new PsError("a string was expected");
        var file = Pop();
        int filled = 0;
        var d = _scanner.Data;
        int p = _scanner.Position;
        if (op == "readhexstring")
        {
            int hi = -1;
            while (filled < buf.Bytes.Length && p < _scanner.End)
            {
                byte c = d[p++];
                if (!Uri.IsHexDigit((char)c)) continue;
                int v = Convert.ToInt32(((char)c).ToString(), 16);
                if (hi < 0) hi = v; else { buf.Bytes[filled++] = (byte)(hi * 16 + v); hi = -1; }
            }
        }
        else
        {
            while (filled < buf.Bytes.Length && p < _scanner.End) buf.Bytes[filled++] = d[p++];
        }
        _scanner.Position = p;
        Push(new PsStr(buf.Bytes[..filled]));
        Push(filled == buf.Bytes.Length);
        _ = file;
        return true;
    }

    // ---------------------------------------------------------------- image operators

    private void Image(string op)
    {
        int w, h, bps, ncomp = 1;
        Affine2 matrix;
        object? source;
        bool mask = op == "imagemask", polarity = false;
        double[]? decode = null;
        double[]? colorKey = null;

        if (Peek() is PsDict d)
        {
            Pop();
            int type = d.Map.TryGetValue("ImageType", out var it) && it is double itd ? (int)itd : 1;
            if (type == 3) { _b.Count("{0} masked picture(s) use an interleaved mask, which is not supported; they are shown without it."); }
            w = (int)NumOf(d, "Width"); h = (int)NumOf(d, "Height"); bps = (int)(mask ? 1 : NumOf(d, "BitsPerComponent", 8));
            matrix = d.Map.TryGetValue("ImageMatrix", out var im) && im is PsArr ima && ima.Items.Count >= 6 ? ToMatrix(ima) : new Affine2(w, 0, 0, h, 0, 0);
            source = d.Map.TryGetValue("DataSource", out var ds) ? ds : null;
            decode = d.Map.TryGetValue("Decode", out var dv) && dv is PsArr da ? da.Items.OfType<double>().ToArray() : null;
            colorKey = d.Map.TryGetValue("MaskColor", out var mc) && mc is PsArr mca ? mca.Items.OfType<double>().ToArray() : null;
            if (mask) polarity = decode is { Length: >= 2 } && decode[0] == 1;
            ncomp = mask ? 1 : ComponentsOfColorSpace();
        }
        else if (op == "colorimage")
        {
            ncomp = PopInt();
            bool multi = PopBool();
            if (multi) { _b.Count("{0} picture(s) with separate data per color are not supported."); _stack.Clear(); return; }
            source = Pop(); matrix = PopMatrix(); bps = PopInt(); h = PopInt(); w = PopInt();
        }
        else
        {
            source = Pop(); matrix = PopMatrix();
            if (mask) { polarity = PopBool(); bps = 1; } else bps = PopInt();
            h = PopInt(); w = PopInt();
            ncomp = mask ? 1 : ComponentsOfColorSpace();
        }
        if (w <= 0 || h <= 0 || (long)w * h > 60_000_000 || bps is not (1 or 2 or 4 or 8)) { Problem("a picture has a size or bit depth that is not supported"); return; }

        int rowBytes = (w * ncomp * bps + 7) / 8;
        byte[] data;
        bool dct = source is PsFile { IsDct: true };
        data = SourceBytes(source, rowBytes * h);
        if (!Visible) return;
        if (!matrix.IsInvertible) return;
        var toDoc = matrix.Invert() * _s.Ctm;

        byte[] bytes; string media; int pw = w, ph = h;
        if (dct)
        {
            bytes = data; media = "image/jpeg";
            if (ImageInfo.TryRead(data, out _, out int jw, out int jh) && jw > 0 && jh > 0) { pw = jw; ph = jh; toDoc = Affine2.Scale((double)w / pw, (double)h / ph) * toDoc; }
        }
        else
        {
            if (data.Length < rowBytes * h) { if (data.Length == 0) { Problem("a picture's data is missing"); return; } Array.Resize(ref data, rowBytes * h); }
            var rgba = new byte[w * h * 4];
            var fill = _s.Fill ?? DocColor.Black;
            var space = _s.ColorSpace is PsName n ? n.Value : _s.ColorSpace is PsArr { Items.Count: > 0 } a && a.Items[0] is PsName n0 ? n0.Value : "DeviceGray";
            double maxv = (1 << bps) - 1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    var comps = new double[ncomp];
                    var raw = new int[ncomp];
                    for (int k = 0; k < ncomp; k++)
                    {
                        long bit = ((long)x * ncomp + k) * bps;
                        int bi = y * rowBytes + (int)(bit >> 3);
                        int v = bps == 8 ? data[bi] : (data[bi] >> (8 - bps - (int)(bit & 7))) & ((1 << bps) - 1);
                        raw[k] = v;
                        double dmin = decode is not null && decode.Length >= 2 * k + 2 ? decode[2 * k] : 0, dmax = decode is not null && decode.Length >= 2 * k + 2 ? decode[2 * k + 1] : 1;
                        comps[k] = dmin + v * (dmax - dmin) / maxv;
                    }
                    if (mask)
                    {
                        bool paint = polarity ? raw[0] == 1 : raw[0] == 0;
                        if (paint) { rgba[o] = fill.R; rgba[o + 1] = fill.G; rgba[o + 2] = fill.B; rgba[o + 3] = 255; }
                        continue;
                    }
                    DocColor c = ncomp switch
                    {
                        >= 4 => Cmyk(comps[0], comps[1], comps[2], comps[3]),
                        3 => Rgb(comps[0], comps[1], comps[2]),
                        _ => Gray(comps[0]),
                    };
                    rgba[o] = c.R; rgba[o + 1] = c.G; rgba[o + 2] = c.B; rgba[o + 3] = 255;
                    if (colorKey is { Length: >= 2 } && colorKey.Length >= 2 * ncomp)
                    {
                        bool all = true;
                        for (int k = 0; k < ncomp && all; k++) all = raw[k] >= colorKey[2 * k] && raw[k] <= colorKey[2 * k + 1];
                        if (all) rgba[o + 3] = 0;
                    }
                }
            _ = space;
            bool alpha = mask || colorKey is not null;
            bytes = PdfImages.Png.Encode(rgba, w, h, alpha);
            media = "image/png";
        }
        string id = _b.Assets.Add(bytes, media);
        var img = new ImageObject(id, pw, ph, pw, ph) { Name = _pendingName, Transform = toDoc };
        _b.Add(img);
        if (_s.Clips.Count > 0)
        {
            var bounds = img.GetWorldBounds();
            foreach (var clip in _s.Clips)
            {
                var cb = clip.GetBounds();
                if (cb.Left > bounds.Left + 0.01 || cb.Right < bounds.Right - 0.01 || cb.Top > bounds.Top + 0.01 || cb.Bottom < bounds.Bottom - 0.01)
                {
                    _b.Count("{0} picture(s) are cut by a clipping path in the file; they are shown whole.");
                    break;
                }
            }
        }
    }

    private int ComponentsOfColorSpace()
    {
        string family = _s.ColorSpace switch
        {
            PsName n => n.Value,
            PsArr { Items.Count: > 0 } a when a.Items[0] is PsName n0 => n0.Value,
            _ => "DeviceGray",
        };
        return family switch { "DeviceRGB" or "CalRGB" => 3, "DeviceCMYK" => 4, _ => 1 };
    }

    private static double NumOf(PsDict d, string key, double fallback = 0) => d.Map.TryGetValue(key, out var v) && v is double x ? x : fallback;

    private static Affine2 ToMatrix(PsArr a)
    {
        var v = a.Items.Take(6).Select(i => i is double d ? d : 0).ToArray();
        return new Affine2(v[0], v[1], v[2], v[3], v[4], v[5]);
    }
}
