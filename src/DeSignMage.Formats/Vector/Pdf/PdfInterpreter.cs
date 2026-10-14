using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>
/// Runs the content stream of a page and the forms it draws, building objects in drawing order: paths (filled and stroked, with their clipping
/// paths), shadings as gradient fills, pictures, and text (through <see cref="PdfTextEmitter"/>). Form XObjects are groups; marked-content layers
/// are layers; text letters come from PdfPig, placed by the n-th text-showing operator.
/// </summary>
internal sealed partial class PdfInterpreter
{
    private const int MaxFormDepth = 16;
    private const int MaxOperations = 20_000_000;

    private sealed class GState
    {
        public Affine2 Ctm;
        public PdfColorSpace FillSpace = PdfColorSpace.Gray, StrokeSpace = PdfColorSpace.Gray;
        public DocColor? FillColor = DocColor.Black, StrokeColor = DocColor.Black;
        public Pattern? FillPattern, StrokePattern;
        public double LineWidth = 1, Miter = 10, FillAlpha = 1, StrokeAlpha = 1;
        public LineCap Cap = LineCap.Butt;
        public LineJoin Join = LineJoin.Miter;
        public bool Dashed;
        public IReadOnlyList<PathGeometry> Clips = [];

        public GState Clone() => (GState)MemberwiseClone();
    }

    /// <summary>A pattern color: a shading pattern with its matrix, or a tiling pattern (drawn as a flat color).</summary>
    private sealed record Pattern(PdfShading? Shading, Affine2 ToDoc, DocColor? Flat);

    private readonly PdfFile _file;
    private readonly VectorBuilder _b;
    private readonly VectorImportOptions _options;
    private readonly Affine2 _pageBase;
    private readonly Dictionary<int, List<Letter>> _lettersBySequence;
    private readonly PdfTextEmitter _text;
    private readonly HashSet<long> _hiddenGroups;

    private GState _s;
    private readonly Stack<GState> _stack = new();
    private DictionaryToken? _resources;
    private Affine2 _streamBase;          // user space of the stream being run, for patterns
    private int _sequence;                // text-showing operators seen so far
    private readonly List<Letter> _pendingText = [];
    private int _depth;
    private long _operations;
    private bool _stopped;

    // the current path, in document points
    private readonly List<PathVerb> _verbs = [];
    private readonly List<Vec2> _points = [];
    private Vec2 _current, _subpathStart;
    private bool _hasCurrent;
    private FillRule? _pendingClip;

    private readonly record struct Marked(bool IsLayer, bool Hidden, string? PreviousLayer);
    private readonly Stack<Marked> _marked = new();
    private string? _layerName;
    private int _hiddenDepth;

    public PdfInterpreter(PdfFile file, VectorBuilder builder, VectorImportOptions options, Affine2 pageBase, IEnumerable<Letter> letters, HashSet<long> hiddenGroups)
    {
        _file = file; _b = builder; _options = options; _pageBase = pageBase; _hiddenGroups = hiddenGroups;
        _lettersBySequence = letters.GroupBy(l => l.TextSequence).ToDictionary(g => g.Key, g => g.ToList());
        _text = new PdfTextEmitter(builder, options, pageBase);
        _s = new GState { Ctm = pageBase };
        _streamBase = pageBase;
    }

    public void RunPage(DictionaryToken? resources, byte[] content)
    {
        _resources = resources;
        Run(content);
        FlushText();
        if (_lettersBySequence.Keys.Any(k => k > _sequence))
            _b.Warn("Some text could not be placed in the drawing order and was left out.");
    }

    // ---------------------------------------------------------------- the loop

    private void Run(byte[] content)
    {
        foreach (var op in PdfContent.Parse(content))
        {
            if (_stopped) return;
            if (++_operations > MaxOperations) { _b.Warn("The page is too complex; the rest of it was left out."); _stopped = true; return; }
            Execute(op);
        }
    }

    private void Execute(PdfOp op)
    {
        switch (op.Name)
        {
            // ---- graphics state
            case "q": if (_stack.Count < 256) _stack.Push(_s.Clone()); break;
            case "Q": if (_stack.Count > 0) _s = _stack.Pop(); break;
            case "cm":
                if (op.Has(6)) _s.Ctm = new Affine2(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(4), op.Num(5)) * _s.Ctm;
                break;
            case "w": _s.LineWidth = op.Num(0); break;
            case "J": _s.Cap = (int)op.Num(0) switch { 1 => LineCap.Round, 2 => LineCap.Square, _ => LineCap.Butt }; break;
            case "j": _s.Join = (int)op.Num(0) switch { 1 => LineJoin.Round, 2 => LineJoin.Bevel, _ => LineJoin.Miter }; break;
            case "M": _s.Miter = Math.Max(1, op.Num(0)); break;
            case "d": _s.Dashed = op.Operands.Count > 0 && op.Operands[0] is List<object?> { Count: > 0 } arr && arr.OfType<double>().Any(v => v > 0); break;
            case "gs": ExtGState(op.NameAt(0)?.Value); break;

            // ---- path construction
            case "m": if (op.Has(2)) MoveTo(op.Num(0), op.Num(1)); break;
            case "l": if (op.Has(2)) LineTo(op.Num(0), op.Num(1)); break;
            case "c": if (op.Has(6)) CurveTo(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(4), op.Num(5)); break;
            case "v": if (op.Has(4)) CurveTo(CurrentUser.X, CurrentUser.Y, op.Num(0), op.Num(1), op.Num(2), op.Num(3)); break;
            case "y": if (op.Has(4)) CurveTo(op.Num(0), op.Num(1), op.Num(2), op.Num(3), op.Num(2), op.Num(3)); break;
            case "h": ClosePath(); break;
            case "re": if (op.Has(4)) Rectangle(op.Num(0), op.Num(1), op.Num(2), op.Num(3)); break;

            // ---- painting
            case "S": PaintPath(false, true, FillRule.NonZero, false); break;
            case "s": PaintPath(false, true, FillRule.NonZero, true); break;
            case "f" or "F": PaintPath(true, false, FillRule.NonZero, false); break;
            case "f*": PaintPath(true, false, FillRule.EvenOdd, false); break;
            case "B": PaintPath(true, true, FillRule.NonZero, false); break;
            case "B*": PaintPath(true, true, FillRule.EvenOdd, false); break;
            case "b": PaintPath(true, true, FillRule.NonZero, true); break;
            case "b*": PaintPath(true, true, FillRule.EvenOdd, true); break;
            case "n": PaintPath(false, false, FillRule.NonZero, false); break;
            case "W": _pendingClip = FillRule.NonZero; break;
            case "W*": _pendingClip = FillRule.EvenOdd; break;

            // ---- color
            case "g": _s.FillSpace = PdfColorSpace.Gray; SetFill(op.Operands); break;
            case "G": _s.StrokeSpace = PdfColorSpace.Gray; SetStroke(op.Operands); break;
            case "rg": _s.FillSpace = PdfColorSpace.Rgb; SetFill(op.Operands); break;
            case "RG": _s.StrokeSpace = PdfColorSpace.Rgb; SetStroke(op.Operands); break;
            case "k": _s.FillSpace = PdfColorSpace.Cmyk; SetFill(op.Operands); break;
            case "K": _s.StrokeSpace = PdfColorSpace.Cmyk; SetStroke(op.Operands); break;
            case "cs": SetSpace(op.NameAt(0)?.Value, fill: true); break;
            case "CS": SetSpace(op.NameAt(0)?.Value, fill: false); break;
            case "sc" or "scn": SetFill(op.Operands); break;
            case "SC" or "SCN": SetStroke(op.Operands); break;
            case "sh": PaintShading(op.NameAt(0)?.Value); break;

            // ---- objects
            case "Do": DoXObject(op.NameAt(0)?.Value); break;
            case "BI": InlineImage(op); break;

            // ---- text (the letters come from PdfPig; here only the places in the drawing order matter)
            case "BT": FlushText(); break;
            case "ET": FlushText(); break;
            case "Tj" or "TJ" or "'" or "\"":
                ShowText();
                break;

            // ---- marked content
            case "BDC": BeginMarked(op); break;
            case "BMC": _marked.Push(new Marked(false, false, null)); break;
            case "EMC": EndMarked(); break;
        }
    }

    // ---------------------------------------------------------------- graphics state helpers

    private void ExtGState(string? name)
    {
        var states = _file.GetDict(_resources, "ExtGState");
        var d = _file.GetDict(states, name ?? "");
        if (d is null) return;
        if (_file.Number(_file.Get(d, "LW")) is { } lw) _s.LineWidth = lw;
        if (_file.Number(_file.Get(d, "LC")) is { } lc) _s.Cap = (int)lc switch { 1 => LineCap.Round, 2 => LineCap.Square, _ => LineCap.Butt };
        if (_file.Number(_file.Get(d, "LJ")) is { } lj) _s.Join = (int)lj switch { 1 => LineJoin.Round, 2 => LineJoin.Bevel, _ => LineJoin.Miter };
        if (_file.Number(_file.Get(d, "ML")) is { } ml) _s.Miter = Math.Max(1, ml);
        if (_file.Number(_file.Get(d, "CA")) is { } ca) _s.StrokeAlpha = Math.Clamp(ca, 0, 1);
        if (_file.Number(_file.Get(d, "ca")) is { } fa) _s.FillAlpha = Math.Clamp(fa, 0, 1);
        if (_file.Get(d, "D") is ArrayToken { Length: > 0 } da && _file.Array(da[0]) is { } dashes)
            _s.Dashed = dashes.Data.Select(x => _file.Number(x) ?? 0).Any(v => v > 0);
        var bm = _file.Get(d, "BM");
        string? blend = bm is ArrayToken { Length: > 0 } ba ? _file.Name(ba[0]) : _file.Name(bm);
        if (blend is not null and not ("Normal" or "Compatible")) _b.Count("{0} object(s) use a blend mode (multiply, overlay, ...), which is not supported; they are drawn normally.");
        var sm = _file.Get(d, "SMask");
        if (sm is DictionaryToken) _b.Count("{0} object(s) use a soft mask, which is not supported; they are drawn without it.");
    }

    private void SetSpace(string? name, bool fill)
    {
        if (name is null) return;
        var cs = PdfColorSpace.Read(_file, NameToken.Create(name), _resources);
        if (fill)
        {
            _s.FillSpace = cs; _s.FillPattern = null; _s.FillColor = cs.IsPattern ? null : cs.ToColor(cs.Initial);
        }
        else
        {
            _s.StrokeSpace = cs; _s.StrokePattern = null; _s.StrokeColor = cs.IsPattern ? null : cs.ToColor(cs.Initial);
        }
    }

    private void SetFill(List<object?> operands)
    {
        var (color, pattern) = ReadColor(_s.FillSpace, operands);
        _s.FillColor = color; _s.FillPattern = pattern;
    }

    private void SetStroke(List<object?> operands)
    {
        var (color, pattern) = ReadColor(_s.StrokeSpace, operands);
        _s.StrokeColor = color; _s.StrokePattern = pattern;
    }

    private (DocColor? Color, Pattern? Pattern) ReadColor(PdfColorSpace space, List<object?> operands)
    {
        if (space.IsPattern || (operands.Count > 0 && operands[^1] is PdfName))
        {
            if (operands.Count > 0 && operands[^1] is PdfName pn)
            {
                var under = operands.Take(operands.Count - 1).OfType<double>().ToArray();
                var pattern = ReadPattern(pn.Value, space, under);
                return (pattern?.Flat, pattern);
            }
            return (null, null);
        }
        var comps = operands.OfType<double>().ToArray();
        if (comps.Length < space.Components) comps = comps.Concat(space.Initial.Skip(comps.Length)).ToArray();
        return (space.ToColor(comps), null);
    }

    private Pattern? ReadPattern(string name, PdfColorSpace space, double[] underComponents)
    {
        var patterns = _file.GetDict(_resources, "Pattern");
        var token = _file.Get(patterns, name);
        var dict = _file.Dict(token);
        if (dict is null) return null;
        int type = (int)_file.GetNumber(dict, "PatternType", 0);
        var m = _file.Numbers(_file.Get(dict, "Matrix"));
        var matrix = m is { Length: 6 } ? new Affine2(m[0], m[1], m[2], m[3], m[4], m[5]) : Affine2.Identity;
        var toDoc = matrix * _streamBase;
        if (type == 2)
        {
            var shading = PdfShading.Read(_file, _file.Get(dict, "Shading"), _resources);
            if (shading is null) return null;
            return new Pattern(shading, toDoc, shading.Fallback);
        }
        // a tiling pattern: its color if it has one (an uncolored pattern takes the color given with it), else a gray; the tile is not drawn
        _b.Count("{0} fill(s) use a tiling pattern, which is not supported; a flat color is used instead.");
        DocColor? flat = underComponents.Length > 0 ? space.ToColor(underComponents) : null;
        return new Pattern(null, toDoc, flat ?? new DocColor(160, 160, 160));
    }
    // ---------------------------------------------------------------- paths

    private Vec2 CurrentUser => _hasCurrent ? _s.Ctm.Invert().Transform(_current) : Vec2.Zero;

    private Vec2 ToDoc(double x, double y) => _s.Ctm.Transform(new Vec2(x, y));

    private void MoveTo(double x, double y)
    {
        var p = ToDoc(x, y);
        _verbs.Add(PathVerb.MoveTo); _points.Add(p);
        _current = _subpathStart = p; _hasCurrent = true;
    }

    private void LineTo(double x, double y)
    {
        if (!_hasCurrent) { MoveTo(x, y); return; }
        var p = ToDoc(x, y);
        _verbs.Add(PathVerb.LineTo); _points.Add(p);
        _current = p;
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        if (!_hasCurrent) MoveTo(x1, y1);
        var c1 = ToDoc(x1, y1); var c2 = ToDoc(x2, y2); var p = ToDoc(x3, y3);
        _verbs.Add(PathVerb.CubicTo); _points.Add(c1); _points.Add(c2); _points.Add(p);
        _current = p;
    }

    private void ClosePath()
    {
        if (!_hasCurrent || _verbs.Count == 0 || _verbs[^1] == PathVerb.Close) return;
        _verbs.Add(PathVerb.Close);
        _current = _subpathStart;
    }

    private void Rectangle(double x, double y, double w, double h)
    {
        MoveTo(x, y); LineTo(x + w, y); LineTo(x + w, y + h); LineTo(x, y + h); ClosePath();
    }

    private PathGeometry BuildPath(FillRule rule) => new(_verbs.ToArray(), _points.ToArray(), rule);

    private void ClearPath()
    {
        _verbs.Clear(); _points.Clear(); _hasCurrent = false; _pendingClip = null;
    }

    private void PaintPath(bool fill, bool stroke, FillRule rule, bool close)
    {
        if (close) ClosePath();
        if (_verbs.Count > 0)
        {
            if (fill || stroke) EmitShape(BuildPath(rule), fill, stroke);
            if (_pendingClip is { } clipRule)
            {
                var clip = BuildPath(clipRule);
                _s.Clips = [.. _s.Clips, clip];
            }
        }
        ClearPath();
    }

    private bool Hidden => _hiddenDepth > 0;

    private void EmitShape(PathGeometry geometry, bool fill, bool stroke)
    {
        if (Hidden) return;
        var bounds = geometry.GetBounds();
        if (bounds.IsEmpty) return;
        bool wantFill = fill && (_s.FillColor is not null || _s.FillPattern is not null);
        bool wantStroke = stroke && _s.StrokeColor is not null && _s.StrokeColor.Value.A > 0;
        if (!wantFill && !wantStroke) return;
        if (_s.Clips.Count > 0 && !wantFill && _s.Clips.Any(c => !VectorBuilder.IsRectangle(c))) { /* strokes are not cut */ }

        PathGeometry? clipped = wantFill ? _b.ApplyClips(geometry, _s.Clips) : geometry;
        if (clipped is null) return;
        var objectBounds = clipped.GetBounds();

        Paint? fillPaint = wantFill ? FillPaint(objectBounds) : null;
        StrokeStyle? strokeStyle = null;
        if (wantStroke)
        {
            double width = _s.LineWidth * _s.Ctm.AverageScale;
            if (width < 0.25) width = 0.25;
            var strokePaint = StrokePaint(objectBounds);
            if (strokePaint is not null)
            {
                strokeStyle = new StrokeStyle(strokePaint, width, _s.Join, _s.Cap, _s.Miter);
                if (_s.Dashed) _b.Count("{0} stroke(s) are dashed in the file; dashes are not supported, so they are drawn solid.");
            }
        }
        if (fillPaint is null && strokeStyle is null) return;
        _b.Add(new PathObject(clipped) { Style = new ObjectStyle(fillPaint, strokeStyle) });
    }

    private Paint? FillPaint(Rect2 bounds)
    {
        if (_s.FillPattern is { } pat) return PatternPaint(pat, bounds, _s.FillAlpha);
        if (_s.FillColor is not { } c) return null;
        return new SolidPaint(WithAlpha(c, _s.FillAlpha));
    }

    private Paint? StrokePaint(Rect2 bounds)
    {
        if (_s.StrokePattern is { } pat) return PatternPaint(pat, bounds, _s.StrokeAlpha);
        if (_s.StrokeColor is not { } c) return null;
        return new SolidPaint(WithAlpha(c, _s.StrokeAlpha));
    }

    private static DocColor WithAlpha(DocColor c, double alpha) => alpha >= 1 ? c : c with { A = (byte)Math.Round(c.A * alpha) };

    private Paint PatternPaint(Pattern pat, Rect2 bounds, double alpha)
    {
        if (pat.Shading is { } shading)
        {
            var paint = shading.ToPaint(pat.ToDoc, bounds, alpha, out string? note);
            if (note is not null) _b.Warn(note);
            return paint;
        }
        return new SolidPaint(WithAlpha(pat.Flat ?? DocColor.Black, alpha));
    }

    // ---------------------------------------------------------------- shading operator

    private void PaintShading(string? name)
    {
        if (Hidden || name is null) return;
        var shadings = _file.GetDict(_resources, "Shading");
        var shading = PdfShading.Read(_file, _file.Get(shadings, name), _resources);
        if (shading is null) return;

        // the shading fills the clipping region: its outline is the last clip (or the page when nothing clips it)
        PathGeometry region;
        if (_s.Clips.Count > 0)
        {
            region = _s.Clips[^1];
            if (_s.Clips.Count > 1) region = _b.ApplyClips(region, _s.Clips.Take(_s.Clips.Count - 1).ToList()) ?? region;
        }
        else region = PathGeometry.Rectangle(_b.Page.Bounds);
        var bounds = region.GetBounds();
        if (bounds.IsEmpty) return;
        var paint = shading.ToPaint(_s.Ctm, bounds, _s.FillAlpha, out string? note);
        if (note is not null) _b.Warn(note);
        _b.Add(new PathObject(region) { Style = new ObjectStyle(paint, null) });
    }

    // ---------------------------------------------------------------- text

    private void ShowText()
    {
        _sequence++;
        if (Hidden) return;
        if (_lettersBySequence.TryGetValue(_sequence, out var letters)) _pendingText.AddRange(letters);
    }

    private void FlushText()
    {
        if (_pendingText.Count == 0) return;
        if (_s.Clips.Any(c => !VectorBuilder.IsRectangle(c))) _b.Count("{0} text(s) lie in a clipping path; the text is not cut by it.");
        _text.Emit(_pendingText.ToList());
        _pendingText.Clear();
    }

    // ---------------------------------------------------------------- marked content (layers)

    private void BeginMarked(PdfOp op)
    {
        bool layer = false, hidden = false;
        string? previous = _layerName;
        if (op.NameAt(0)?.Value == "OC")
        {
            string? propName = op.NameAt(1)?.Value;
            DictionaryToken? props = _file.GetDict(_resources, "Properties");
            var raw = propName is null ? null : _file.GetRaw(props, propName);
            var oc = _file.Dict(raw);
            if (oc is not null)
            {
                hidden = IsHidden(raw, oc);
                string? name = _file.StringValue(_file.Get(oc, "Name"));
                if (name is not null && _marked.All(m => !m.IsLayer) && _depth == 0 && _file.GetName(oc, "Type") != "OCMD")
                {
                    _layerName = name;
                    _b.SetLayer(name);
                    layer = true;
                }
            }
        }
        if (hidden) _hiddenDepth++;
        _marked.Push(new Marked(layer, hidden, previous));
    }

    private bool IsHidden(IToken? raw, DictionaryToken oc)
    {
        if (raw is IndirectReferenceToken r && _hiddenGroups.Contains(r.Data.ObjectNumber)) return true;
        if (_file.GetName(oc, "Type") == "OCMD" && _file.Get(oc, "OCGs") is { } groups)
        {
            var refs = groups is ArrayToken a ? a.Data : [_file.GetRaw(oc, "OCGs")!];
            var states = refs.OfType<IndirectReferenceToken>().Select(x => _hiddenGroups.Contains(x.Data.ObjectNumber)).ToList();
            return states.Count > 0 && states.All(s => s);
        }
        return false;
    }

    private void EndMarked()
    {
        if (_marked.Count == 0) return;
        var m = _marked.Pop();
        if (m.Hidden) _hiddenDepth--;
        if (m.IsLayer)
        {
            _layerName = m.PreviousLayer;
            _b.SetLayer(m.PreviousLayer);
        }
    }
}
