using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

internal sealed partial class PdfInterpreter
{
    // ---------------------------------------------------------------- XObjects

    private void DoXObject(string? name)
    {
        if (name is null || Hidden) return;
        var xobjects = _file.GetDict(_resources, "XObject");
        if (_file.Get(xobjects, name) is not StreamToken stream) return;
        var d = stream.StreamDictionary;
        if (_file.GetRaw(d, "OC") is { } ocRaw && _file.Dict(ocRaw) is { } oc && IsHidden(ocRaw, oc)) return;
        switch (_file.GetName(d, "Subtype"))
        {
            case "Image": DrawImage(stream); break;
            case "Form": DrawForm(stream); break;
        }
    }

    private void DrawForm(StreamToken form)
    {
        if (_depth >= MaxFormDepth) { _b.Warn("Forms are nested too deeply; the innermost ones were left out."); return; }
        var d = form.StreamDictionary;
        var savedState = _s.Clone();
        var savedResources = _resources;
        var savedBase = _streamBase;
        int stackBase = _stack.Count;
        FlushText();

        var m = _file.Numbers(_file.Get(d, "Matrix"));
        if (m is { Length: 6 }) _s.Ctm = new Affine2(m[0], m[1], m[2], m[3], m[4], m[5]) * _s.Ctm;
        if (_file.Numbers(_file.Get(d, "BBox")) is { Length: 4 } bb)
        {
            double x0 = Math.Min(bb[0], bb[2]), x1 = Math.Max(bb[0], bb[2]), y0 = Math.Min(bb[1], bb[3]), y1 = Math.Max(bb[1], bb[3]);
            var clip = new PathBuilder().MoveTo(ToDoc(x0, y0)).LineTo(ToDoc(x1, y0)).LineTo(ToDoc(x1, y1)).LineTo(ToDoc(x0, y1)).Close().Build();
            _s.Clips = [.. _s.Clips, clip];
        }
        if (_file.GetDict(d, "Resources") is { } res) _resources = res;
        _streamBase = _s.Ctm;

        // a path under construction does not belong to the form
        var pathVerbs = _verbs.ToArray(); var pathPoints = _points.ToArray();
        var cur = _current; var start = _subpathStart; bool hasCur = _hasCurrent; var pending = _pendingClip;
        _verbs.Clear(); _points.Clear(); _hasCurrent = false; _pendingClip = null;

        _depth++;
        _b.BeginGroup(null);
        try
        {
            byte[] content;
            try { content = _file.Decode(form, out _); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _b.Warn("A form's content could not be decoded and was left out.");
                content = [];
            }
            Run(content);
            FlushText();
        }
        finally
        {
            _b.EndGroup();
            _depth--;
            while (_stack.Count > stackBase) _stack.Pop();
            _s = savedState;
            _resources = savedResources;
            _streamBase = savedBase;
            _verbs.Clear(); _verbs.AddRange(pathVerbs); _points.Clear(); _points.AddRange(pathPoints);
            _current = cur; _subpathStart = start; _hasCurrent = hasCur; _pendingClip = pending;
        }
    }

    // ---------------------------------------------------------------- pictures

    private void DrawImage(StreamToken stream)
    {
        var fill = _s.FillColor ?? DocColor.Black;
        var result = PdfImages.Build(_file, stream, _resources, fill, _s.FillAlpha, out string? problem);
        if (problem is not null) _b.Warn(char.ToUpperInvariant(problem[0]) + problem[1..] + ".");
        if (result is null) return;

        var ctm = _s.Ctm;
        double fw = Math.Sqrt(ctm.M11 * ctm.M11 + ctm.M12 * ctm.M12), fh = Math.Sqrt(ctm.M21 * ctm.M21 + ctm.M22 * ctm.M22);
        if (!(fw > 1e-9 && fh > 1e-9)) return;
        string id = _b.Assets.Add(result.Data, result.MediaType);
        // the picture's own space is the unit square with the first row at the top (y = 1)
        var unitFromLocal = new Affine2(1 / fw, 0, 0, -1 / fh, 0, 1);
        var img = new ImageObject(id, fw, fh, result.Width, result.Height) { Transform = unitFromLocal * ctm };

        var bounds = img.GetWorldBounds();
        foreach (var clip in _s.Clips)
        {
            var cb = clip.GetBounds();
            if (!cb.Intersects(bounds)) return;   // clipped away completely
            if (cb.Left > bounds.Left + 0.01 || cb.Right < bounds.Right - 0.01 || cb.Top > bounds.Top + 0.01 || cb.Bottom < bounds.Bottom - 0.01)
            {
                _b.Count("{0} picture(s) are cut by a clipping path in the file; they are shown whole.");
                break;
            }
        }
        _b.Add(img);
    }

    private void InlineImage(PdfOp op)
    {
        if (Hidden || op.InlineDict is null || op.InlineData is null) return;
        var map = new Dictionary<NameToken, IToken>();
        foreach (var (key, value) in op.InlineDict)
        {
            string full = key switch
            {
                "BPC" => "BitsPerComponent", "CS" => "ColorSpace", "D" => "Decode", "DP" => "DecodeParms", "F" => "Filter",
                "H" => "Height", "IM" => "ImageMask", "I" => "Interpolate", "W" => "Width", _ => key,
            };
            map[NameToken.Create(full)] = ToToken(value, full);
        }
        DrawImage(new StreamToken(new DictionaryToken(map), op.InlineData));
    }

    private static IToken ToToken(object? v, string key)
    {
        switch (v)
        {
            case double d: return new NumericToken(d);
            case bool b: return b ? BooleanToken.True : BooleanToken.False;
            case PdfName n: return NameToken.Create(Expand(n.Value, key));
            case byte[] bytes: return new StringToken(Encoding.Latin1.GetString(bytes), StringToken.Encoding.Iso88591);
            case List<object?> list: return new ArrayToken(list.Select(x => ToToken(x, key)).ToList());
            case Dictionary<string, object?> dict: return new DictionaryToken(dict.ToDictionary(kv => NameToken.Create(kv.Key), kv => ToToken(kv.Value, kv.Key)));
            default: return NullToken.Instance;
        }
    }

    private static string Expand(string name, string key) => key switch
    {
        "Filter" => name switch
        {
            "AHx" => "ASCIIHexDecode", "A85" => "ASCII85Decode", "LZW" => "LZWDecode", "Fl" => "FlateDecode", "RL" => "RunLengthDecode",
            "CCF" => "CCITTFaxDecode", "DCT" => "DCTDecode", _ => name,
        },
        "ColorSpace" => name switch { "G" => "DeviceGray", "RGB" => "DeviceRGB", "CMYK" => "DeviceCMYK", "I" => "Indexed", _ => name },
        _ => name,
    };
}
