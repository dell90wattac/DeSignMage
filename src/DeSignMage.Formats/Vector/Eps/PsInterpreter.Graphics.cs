using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Vector.Eps;

internal sealed partial class PsInterpreter
{
    private sealed class GState
    {
        public Affine2 Ctm;
        public DocColor? Fill = DocColor.Black, Stroke = DocColor.Black;
        public PsPattern? FillPattern;
        public double LineWidth = 1, Miter = 10;
        public LineCap Cap = LineCap.Butt;
        public LineJoin Join = LineJoin.Miter;
        public bool Dashed, EvenOdd;
        public IReadOnlyList<PathGeometry> Clips = [];
        public object? ColorSpace;
        public PsFont Font = new("Helvetica", 12);

        public GState Clone() => (GState)MemberwiseClone();
    }

    internal sealed record PsFont(string Name, double Size);

    private sealed record PsCustomColor(double C, double M, double Y, double K, string Name);

    private sealed record PsPattern(object? Shading, Affine2 Matrix);

    private readonly VectorBuilder _b;
    private readonly VectorImportOptions _options;
    private readonly Affine2 _baseMatrix;
    private readonly bool _aiMode;
    private GState _s = new();
    private readonly List<GState> _saved = [];

    // the path under construction, in document points, and the current point in user space
    private readonly List<PathVerb> _verbs = [];
    private readonly List<Vec2> _points = [];
    private bool _hasCurrent;
    private double _cux, _cuy, _sux, _suy;

    // compound path (AI *u ... *U)
    private bool _compound;
    private readonly List<PathVerb> _cverbs = [];
    private readonly List<Vec2> _cpoints = [];
    private bool _cFill, _cStroke, _cHas;

    private string? _pendingName;
    private string? _gradientName;
    private readonly List<DocColor> _gradientStops = [];
    private readonly Dictionary<string, DocColor> _gradientColors = [];
    private int _groupDepth;
    private bool _layerHidden;
    private string? _layerName;
    private readonly HashSet<string> _unknown = [];
    private readonly HashSet<string> _problems = [];

    public static void Run(byte[] data, int start, int end, VectorBuilder builder, VectorImportOptions options, Affine2 baseMatrix, bool aiMode, Action<PsScanner>? hook = null)
    {
        var scanner = new PsScanner(data, start, end);
        var interp = new PsInterpreter(scanner, builder, options, baseMatrix, aiMode);
        scanner.OnComment = interp.Comment;
        hook?.Invoke(scanner);
        interp.Run();
        interp.Finish();
    }

    private void Finish()
    {
        while (_groupDepth > 0) { _b.EndGroup(); _groupDepth--; }
        _b.SetLayer(null);
        if (_unknown.Count > 0)
            _b.Warn("These PostScript operators are not supported and were skipped: " + string.Join(", ", _unknown.Take(8)) + (_unknown.Count > 8 ? ", ..." : "") + ".");
    }

    private void Problem(string message)
    {
        if (_problems.Count < 5 && _problems.Add(message)) _b.Warn("PostScript: " + message + ".");
    }

    private void Unknown(string name)
    {
        if (_aiMode) { _stack.Clear(); return; }
        if (name.Length > 0 && _unknown.Count < 40) _unknown.Add(name);
    }

    // ---------------------------------------------------------------- comments

    private void Comment(string text)
    {
        if (text.StartsWith("Begin Object:", StringComparison.Ordinal))
        {
            int a = text.IndexOf('\'');
            int z = text.LastIndexOf('\'');
            _pendingName = a >= 0 && z > a ? text[(a + 1)..z] : null;
            return;
        }
        if (text.StartsWith("End Object", StringComparison.Ordinal)) { _pendingName = null; return; }
        if (text.StartsWith("AI5_BeginPalette", StringComparison.Ordinal)) { _scanner.SkipUntilComment("AI5_EndPalette"); return; }
        if (text.StartsWith("AI5_BeginPattern", StringComparison.Ordinal)) { _scanner.SkipUntilComment("AI5_EndPattern"); _b.Count("{0} pattern definition(s) in the file are not read."); return; }
        if (text.StartsWith("AI5_BeginRaster", StringComparison.Ordinal)) { _scanner.SkipUntilComment("AI5_EndRaster"); _b.Count("{0} raster picture(s) in the file are not read."); return; }
        if (text.StartsWith("%BeginData", StringComparison.Ordinal) || text.StartsWith("%BeginBinary", StringComparison.Ordinal))
        {
            _scanner.SkipUntilComment(text.StartsWith("%BeginData", StringComparison.Ordinal) ? "%EndData" : "%EndBinary");
            return;
        }
        if (text.StartsWith("BeginProcSet:", StringComparison.Ordinal) || text.StartsWith("BeginResource: procset", StringComparison.Ordinal))
        {
            // a prolog that defines the operators of a drawing program; they are built in here
            _scanner.SkipUntilComment(text.StartsWith("BeginProcSet", StringComparison.Ordinal) ? "%EndProcSet" : "%EndResource");
            return;
        }
        if (text.StartsWith("%Trailer", StringComparison.Ordinal) || text.StartsWith("AI9_PrivateDataBegin", StringComparison.Ordinal)) { _done = true; return; }
    }

    // ---------------------------------------------------------------- graphics state

    private int SaveGraphics()
    {
        _saved.Add(_s.Clone());
        return _saved.Count;
    }

    private void RestoreGraphics(int level)
    {
        if (level <= 0 || level > _saved.Count) return;
        _s = _saved[level - 1];
        _saved.RemoveRange(level - 1, _saved.Count - level + 1);
    }

    private void Gsave() => _saved.Add(_s.Clone());

    private void Grestore()
    {
        if (_saved.Count == 0) return;
        _s = _saved[^1];
        _saved.RemoveAt(_saved.Count - 1);
    }

    // ---------------------------------------------------------------- the operators

    private bool Graphics(string name)
    {
        switch (name)
        {
            // ---- state
            case "gsave" or "q": Gsave(); if (name == "q") BeginGroup(); return true;
            case "grestore" or "Q": if (name == "Q") EndGroup(); Grestore(); return true;
            case "initgraphics": _s.Ctm = _baseMatrix; _s.LineWidth = 1; _s.Fill = _s.Stroke = DocColor.Black; _s.Clips = []; return true;
            case "initclip": _s.Clips = []; return true;
            case "setlinewidth" or "w": _s.LineWidth = PopNum(); return true;
            case "setlinecap" or "J": _s.Cap = PopInt() switch { 1 => LineCap.Round, 2 => LineCap.Square, _ => LineCap.Butt }; return true;
            case "setlinejoin" or "j": _s.Join = PopInt() switch { 1 => LineJoin.Round, 2 => LineJoin.Bevel, _ => LineJoin.Miter }; return true;
            case "setmiterlimit" or "M": _s.Miter = Math.Max(1, PopNum()); return true;
            case "setdash" or "d": { PopNum(); var a = Pop(); _s.Dashed = a is PsArr { Items.Count: > 0 } arr && arr.Items.OfType<double>().Any(v => v > 0); return true; }
            case "setflat" or "i" or "setoverprint" or "setstrokeadjust" or "setpacking" or "setglobal" or "setaccuratecurves" or "setfilloverprint" or "setstrokeoverprint" or "setoverprintmode":
                if (_stack.Count > 0) Pop();
                return true;
            case "setscreen" or "setcolorscreen" or "settransfer" or "setcolortransfer" or "sethalftone" or "setundercolorremoval" or "setblackgeneration" or "setcolorrendering" or "setuserparams" or "setsystemparams" or "setdevparams" or "setpagedevice" or "setcachelimit" or "setcacheparams" or "setfileposition":
                _stack.Clear();
                return true;
            case "currentflat" or "currentlinewidth": Push(name == "currentflat" ? 1.0 : _s.LineWidth); return true;

            // ---- matrix
            case "translate": { var v = PopNumbers(2); _s.Ctm = Affine2.Translation(v[0], v[1]) * _s.Ctm; return true; }
            case "scale": { var v = PopNumbers(2); _s.Ctm = Affine2.Scale(v[0], v[1]) * _s.Ctm; return true; }
            case "rotate": { double a = PopNum() * Math.PI / 180; _s.Ctm = Affine2.Rotation(a) * _s.Ctm; return true; }
            case "concat": { var m = PopMatrix(); _s.Ctm = m * _s.Ctm; return true; }
            case "setmatrix": { _s.Ctm = PopMatrix(); return true; }
            case "initmatrix": _s.Ctm = _baseMatrix; return true;
            case "currentmatrix": { var a = PopArray(); SetMatrix(a, _s.Ctm); Push(a); return true; }
            case "defaultmatrix": { var a = PopArray(); SetMatrix(a, _baseMatrix); Push(a); return true; }
            case "matrix" or "identmatrix": { if (name == "identmatrix") Pop(); Push(MatrixArray(Affine2.Identity)); return true; }
            case "transform": { var v = PopNumbers(2); var p = _s.Ctm.Transform(new Vec2(v[0], v[1])); Push(p.X); Push(p.Y); return true; }
            case "itransform": { var v = PopNumbers(2); var p = _s.Ctm.Invert().Transform(new Vec2(v[0], v[1])); Push(p.X); Push(p.Y); return true; }
            case "dtransform": { var v = PopNumbers(2); var p = _s.Ctm.TransformVector(new Vec2(v[0], v[1])); Push(p.X); Push(p.Y); return true; }
            case "idtransform": { var v = PopNumbers(2); var p = _s.Ctm.Invert().TransformVector(new Vec2(v[0], v[1])); Push(p.X); Push(p.Y); return true; }

            // ---- path
            case "newpath" or "n" or "N": ClearPath(); return true;
            case "moveto" or "m": { var v = PopNumbers(2); MoveTo(v[0], v[1]); return true; }
            case "rmoveto": { var v = PopNumbers(2); MoveTo(_cux + v[0], _cuy + v[1]); return true; }
            case "lineto" or "l" or "L": { var v = PopNumbers(2); LineTo(v[0], v[1]); return true; }
            case "rlineto": { var v = PopNumbers(2); LineTo(_cux + v[0], _cuy + v[1]); return true; }
            case "curveto" or "c" or "C": { var v = PopNumbers(6); CurveTo(v[0], v[1], v[2], v[3], v[4], v[5]); return true; }
            case "rcurveto": { var v = PopNumbers(6); CurveTo(_cux + v[0], _cuy + v[1], _cux + v[2], _cuy + v[3], _cux + v[4], _cuy + v[5]); return true; }
            case "v" or "V": { var v = PopNumbers(4); CurveTo(_cux, _cuy, v[0], v[1], v[2], v[3]); return true; }
            case "y" or "Y": { var v = PopNumbers(4); CurveTo(v[0], v[1], v[2], v[3], v[2], v[3]); return true; }
            case "closepath" or "h": ClosePath(); return true;
            case "currentpoint": Push(_cux); Push(_cuy); return true;
            case "arc" or "arcn": { var v = PopNumbers(5); Arc(v[0], v[1], v[2], v[3], v[4], name == "arcn"); return true; }
            case "rectfill" or "rectstroke" or "rectclip":
            {
                var v = PopNumbers(4);
                var savedVerbs = _verbs.ToArray(); var savedPoints = _points.ToArray(); bool had = _hasCurrent;
                ClearPath();
                MoveTo(v[0], v[1]); LineTo(v[0] + v[2], v[1]); LineTo(v[0] + v[2], v[1] + v[3]); LineTo(v[0], v[1] + v[3]); ClosePath();
                if (name == "rectfill") PaintPath(true, false, FillRule.NonZero);
                else if (name == "rectstroke") PaintPath(false, true, FillRule.NonZero);
                else AddClip(FillRule.NonZero);
                ClearPath();
                _verbs.AddRange(savedVerbs); _points.AddRange(savedPoints); _hasCurrent = had;
                return true;
            }

            // ---- painting (PostScript)
            case "fill": PaintPath(true, false, FillRule.NonZero); ClearPath(); return true;
            case "eofill": PaintPath(true, false, FillRule.EvenOdd); ClearPath(); return true;
            case "stroke": PaintPath(false, true, FillRule.NonZero); ClearPath(); return true;
            case "clip": AddClip(FillRule.NonZero); return true;
            case "eoclip": AddClip(FillRule.EvenOdd); return true;
            case "W": AddClip(AiRule()); return true;

            // ---- painting (Illustrator): lowercase closes the path first
            case "F": PaintPath(true, false, AiRule()); ClearPath(); return true;
            case "f": ClosePath(); PaintPath(true, false, AiRule()); ClearPath(); return true;
            case "S": PaintPath(false, true, AiRule()); ClearPath(); return true;
            case "s": ClosePath(); PaintPath(false, true, AiRule()); ClearPath(); return true;
            case "B": PaintPath(true, true, AiRule()); ClearPath(); return true;
            case "b": ClosePath(); PaintPath(true, true, AiRule()); ClearPath(); return true;
            case "XR": _s.EvenOdd = PopNum() != 0; return true;

            // ---- groups and layers (Illustrator)
            case "u": BeginGroup(); return true;
            case "U": EndGroup(); return true;
            case "*u": _compound = true; _cverbs.Clear(); _cpoints.Clear(); _cHas = false; return true;
            case "*U": EndCompound(); return true;
            case "Lb": { var v = _stack.OfType<double>().ToList(); _stack.Clear(); _layerHidden = v.Count > 0 && v[0] == 0; _b.SetLayer("Layer"); return true; }
            case "Ln": { var s = _stack.OfType<PsStr>().LastOrDefault(); _stack.Clear(); _layerName = s?.ToString() ?? "Layer"; _b.SetLayer(_layerName); return true; }
            case "LB": _layerHidden = false; _layerName = null; _b.SetLayer(null); _stack.Clear(); return true;

            // ---- color
            case "setgray": { double g = PopNum(); _s.Fill = _s.Stroke = Gray(g); _s.FillPattern = null; return true; }
            case "g": { double g = PopNum(); _s.Fill = Gray(g); _s.FillPattern = null; return true; }
            case "G": _s.Stroke = Gray(PopNum()); return true;
            case "setrgbcolor": { var v = PopNumbers(3); _s.Fill = _s.Stroke = Rgb(v[0], v[1], v[2]); _s.FillPattern = null; return true; }
            case "sethsbcolor": { var v = PopNumbers(3); var (r, g, b) = ColorMath.HsvToRgb(v[0] * 360, v[1] * 100, v[2] * 100); _s.Fill = _s.Stroke = new DocColor(r, g, b); _s.FillPattern = null; return true; }
            case "setcmykcolor": { var v = PopNumbers(4); _s.Fill = _s.Stroke = Cmyk(v[0], v[1], v[2], v[3]); _s.FillPattern = null; return true; }
            case "k": { var v = PopNumbers(4); _s.Fill = Cmyk(v[0], v[1], v[2], v[3]); _s.FillPattern = null; return true; }
            case "K": { var v = PopNumbers(4); _s.Stroke = Cmyk(v[0], v[1], v[2], v[3]); return true; }
            case "x" or "X": { var c = CustomColor(); if (name == "x") { _s.Fill = c; _s.FillPattern = null; } else _s.Stroke = c; return true; }
            case "Xa": { var v = PopNumbers(3); _s.Fill = Rgb(v[0], v[1], v[2]); _s.FillPattern = null; return true; }
            case "XA": { var v = PopNumbers(3); _s.Stroke = Rgb(v[0], v[1], v[2]); return true; }
            case "findcmykcustomcolor": { string n = PopString(); var v = PopNumbers(4); Push(new PsCustomColor(v[0], v[1], v[2], v[3], n)); return true; }
            case "setcustomcolor":
            {
                double tint = PopNum();
                var c = Pop() as PsCustomColor ?? throw new PsError("a custom color was expected");
                double k = Math.Clamp(tint, 0, 1);
                var color = DocColor.FromCmyk(c.C * k * 100, c.M * k * 100, c.Y * k * 100, c.K * k * 100);
                if (k >= 0.999 && !string.IsNullOrWhiteSpace(c.Name)) color = color with { Name = c.Name };
                _s.Fill = _s.Stroke = color; _s.FillPattern = null;
                return true;
            }
            case "setcolorspace": _s.ColorSpace = Pop(); return true;
            case "setcolor": SetColor(); return true;

            // ---- patterns and shadings
            case "makepattern": { var m = PopMatrix(); var d = PopDict(); Push(new PsDict(new Dictionary<string, object?>(d.Map) { ["__matrix"] = MatrixArray(m * _s.Ctm) })); return true; }
            case "setpattern": SetPattern(); return true;
            case "shfill": ShFill(); return true;

            // ---- pictures and text
            case "image" or "imagemask" or "colorimage": Image(name); return true;
            case "findfont": { string n = PopName(); Push(new PsFontObject(new PsFont(n, 12))); return true; }
            case "scalefont": { double size = PopNum(); var f = Pop() as PsFontObject; Push(new PsFontObject((f?.Font ?? _s.Font) with { Size = size })); return true; }
            case "makefont": { var m = PopMatrix(); var f = Pop() as PsFontObject; Push(new PsFontObject((f?.Font ?? _s.Font) with { Size = Math.Abs(m.M22) })); return true; }
            case "setfont": { if (Pop() is PsFontObject f) _s.Font = f.Font; return true; }
            case "selectfont": { double size = PopNum(); string n = PopName(); _s.Font = new PsFont(n, size); return true; }
            case "show": ShowText(PopString()); return true;
            case "ashow": { var s = PopString(); PopNumbers(2); ShowText(s); return true; }
            case "widthshow": { var s = PopString(); PopNumbers(4); ShowText(s); return true; }
            case "awidthshow": { var s = PopString(); PopNumbers(6); ShowText(s); return true; }
            case "xshow" or "yshow" or "xyshow": { Pop(); ShowText(PopString()); return true; }
            case "stringwidth": { var s = PopString(); Push(s.Length * _s.Font.Size * 0.5); Push(0.0); return true; }
            case "charpath": { Pop(); PopString(); _b.Count("{0} text path(s) (charpath) are not read."); return true; }

            // ---- Illustrator text and other objects that are not read
            case "To" or "TO" or "Tp" or "TP" or "Tx" or "TX" or "Tj" or "TJ" or "Tc" or "Tw" or "Tz" or "Tr" or "Ts" or "Tl" or "Tf" or "TA" or "TZ" or "TW" or "Tq" or "TR" or "Ta" or "TY" or "Tk" or "TV" or "TQ" or "To1":
                if (name is "To" or "TO") _b.Count("{0} text object(s) in the file are not read: Illustrator keeps text as live text and the outlines are not in the file.");
                _stack.Clear();
                return true;
            case "Bd":
            {
                // a gradient definition: (name) type stops Bd [ ... stops ... ] BD
                _gradientName = _stack.OfType<PsStr>().LastOrDefault()?.ToString();
                _gradientStops.Clear();
                _stack.Clear();
                return true;
            }
            case "Bs":
            {
                var v = _stack.OfType<double>().ToList();
                _stack.Clear();
                if (v.Count >= 4) _gradientStops.Add(Cmyk(v[0], v[1], v[2], v[3]));
                return true;
            }
            case "BD":
            {
                if (_gradientName is not null && _gradientStops.Count > 0)
                {
                    double r = _gradientStops.Average(c => (double)c.R), g = _gradientStops.Average(c => (double)c.G), b = _gradientStops.Average(c => (double)c.B);
                    _gradientColors[_gradientName] = new DocColor((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
                }
                _gradientName = null;
                _stack.Clear();
                return true;
            }
            case "Bg":
            {
                var gname = _stack.OfType<PsStr>().LastOrDefault()?.ToString();
                _stack.Clear();
                if (gname is not null && _gradientColors.TryGetValue(gname, out var avg)) { _s.Fill = avg; _s.FillPattern = null; }
                _b.Count("{0} gradient fill(s) in this Illustrator file are drawn in one color, the average of the gradient.");
                return true;
            }
            case "Bb" or "BB" or "Bm" or "Bc" or "Bh" or "Br" or "Ba": _stack.Clear(); return true;
            case "XI" or "XF" or "Xh" or "XH":
                if (name == "XI") _b.Count("{0} raster picture(s) in the file are not read.");
                _stack.Clear();
                return true;
            case "R" or "O" or "A" or "D" or "Xw" or "Xy" or "Xk" or "Xt" or "XT" or "Xm" or "Xp" or "XP" or "XQ" or "Xe" or "Xr" or "Xs" or "Xu" or "XU" or "Xx" or "Xz":
                _stack.Clear();
                return true;
            case "annotatepage" or "initviewclip" or "setpagedevice2": _stack.Clear(); return true;
        }
        return false;
    }

    private FillRule AiRule() => _s.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero;

    // ---------------------------------------------------------------- matrices

    private Affine2 PopMatrix()
    {
        var a = PopArray();
        if (a.Items.Count < 6) throw new PsError("a matrix was expected");
        var v = a.Items.Take(6).Select(ToNum).ToArray();
        return new Affine2(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    private static PsArr MatrixArray(Affine2 m) => new([m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY], false);

    private static void SetMatrix(PsArr a, Affine2 m)
    {
        var v = new object?[] { m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY };
        a.Items.Clear();
        a.Items.AddRange(v);
    }

    // ---------------------------------------------------------------- colors

    private static DocColor Gray(double g) { byte v = (byte)Math.Clamp(Math.Round(g * 255), 0, 255); return new DocColor(v, v, v); }

    private static DocColor Rgb(double r, double g, double b) =>
        new((byte)Math.Clamp(Math.Round(r * 255), 0, 255), (byte)Math.Clamp(Math.Round(g * 255), 0, 255), (byte)Math.Clamp(Math.Round(b * 255), 0, 255));

    private static DocColor Cmyk(double c, double m, double y, double k) =>
        DocColor.FromCmyk(Math.Clamp(c, 0, 1) * 100, Math.Clamp(m, 0, 1) * 100, Math.Clamp(y, 0, 1) * 100, Math.Clamp(k, 0, 1) * 100);

    /// <summary>Illustrator's <c>c m y k (name) tint x</c>: the named color at full strength is the CMYK values; tint 0 is the full color.</summary>
    private DocColor CustomColor()
    {
        var tintOp = Pop();
        double tint = tintOp is double t ? t : 0;
        string? name = null;
        var nameOp = _stack.Count > 0 ? _stack[^1] : null;
        if (nameOp is PsStr s) { name = s.ToString(); Pop(); }
        var v = PopNumbersUpTo(4);
        double strength = Math.Clamp(1 - tint, 0, 1);
        var color = DocColor.FromCmyk(v[0] * strength * 100, v[1] * strength * 100, v[2] * strength * 100, v[3] * strength * 100);
        return strength >= 0.999 && !string.IsNullOrWhiteSpace(name) ? color with { Name = name } : color;
    }

    private double[] PopNumbersUpTo(int n)
    {
        var r = new double[n];
        for (int i = n - 1; i >= 0 && _stack.Count > 0 && _stack[^1] is double; i--) r[i] = PopNum();
        return r;
    }

    private void SetColor()
    {
        var cs = _s.ColorSpace;
        string family = cs switch
        {
            PsName n => n.Value,
            PsArr { Items.Count: > 0 } a when a.Items[0] is PsName n0 => n0.Value,
            _ => "DeviceGray",
        };
        switch (family)
        {
            case "DeviceRGB" or "CalRGB": { var v = PopNumbers(3); _s.Fill = _s.Stroke = Rgb(v[0], v[1], v[2]); _s.FillPattern = null; break; }
            case "DeviceCMYK": { var v = PopNumbers(4); _s.Fill = _s.Stroke = Cmyk(v[0], v[1], v[2], v[3]); _s.FillPattern = null; break; }
            case "Separation":
            {
                double tint = PopNum();
                var a = (PsArr)cs!;
                string ink = a.Items.Count > 1 ? KeyOf(a.Items[1]) : "";
                var tintProc = a.Items.Count > 3 ? a.Items[3] as PsArr : null;
                string alt = a.Items.Count > 2 ? KeyOf(a.Items[2]) : "DeviceCMYK";
                var comps = EvalTint(tintProc, tint);
                DocColor c = alt == "DeviceRGB" && comps.Length >= 3 ? Rgb(comps[0], comps[1], comps[2])
                    : alt == "DeviceGray" && comps.Length >= 1 ? Gray(comps[0])
                    : comps.Length >= 4 ? Cmyk(comps[0], comps[1], comps[2], comps[3]) : Gray(1 - tint);
                if (tint >= 0.999 && ink is not ("All" or "None" or "")) c = c with { Name = ink };
                _s.Fill = _s.Stroke = c;
                _s.FillPattern = null;
                break;
            }
            case "Pattern": break;
            default: { var v = PopNumbers(1); _s.Fill = _s.Stroke = Gray(v[0]); _s.FillPattern = null; break; }
        }
    }

    private double[] EvalTint(PsArr? proc, double tint)
    {
        if (proc is null) return [];
        int mark = _stack.Count;
        Push(tint);
        try { RunProcedure(proc); } catch (PsError) { }
        var result = _stack.Skip(mark).OfType<double>().ToArray();
        if (_stack.Count > mark) _stack.RemoveRange(mark, _stack.Count - mark);
        return result;
    }

    // ---------------------------------------------------------------- path construction

    private Vec2 ToDoc(double x, double y) => _s.Ctm.Transform(new Vec2(x, y));

    private void MoveTo(double x, double y)
    {
        var p = ToDoc(x, y);
        _verbs.Add(PathVerb.MoveTo); _points.Add(p);
        _cux = _sux = x; _cuy = _suy = y; _hasCurrent = true;
    }

    private void LineTo(double x, double y)
    {
        if (!_hasCurrent) { MoveTo(x, y); return; }
        _verbs.Add(PathVerb.LineTo); _points.Add(ToDoc(x, y));
        _cux = x; _cuy = y;
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        if (!_hasCurrent) MoveTo(x1, y1);
        _verbs.Add(PathVerb.CubicTo); _points.Add(ToDoc(x1, y1)); _points.Add(ToDoc(x2, y2)); _points.Add(ToDoc(x3, y3));
        _cux = x3; _cuy = y3;
    }

    private void ClosePath()
    {
        if (!_hasCurrent || _verbs.Count == 0 || _verbs[^1] == PathVerb.Close) return;
        _verbs.Add(PathVerb.Close);
        _cux = _sux; _cuy = _suy;
    }

    private void ClearPath()
    {
        _verbs.Clear(); _points.Clear(); _hasCurrent = false;
    }

    private void Arc(double cx, double cy, double r, double a1, double a2, bool clockwise)
    {
        double s = a1 * Math.PI / 180, e = a2 * Math.PI / 180;
        if (!clockwise) { while (e < s) e += 2 * Math.PI; }
        else { while (e > s) e -= 2 * Math.PI; }
        double sweep = e - s;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
        double step = sweep / n, k = 4.0 / 3.0 * Math.Tan(step / 4);
        double x0 = cx + r * Math.Cos(s), y0 = cy + r * Math.Sin(s);
        if (_hasCurrent) LineTo(x0, y0); else MoveTo(x0, y0);
        for (int i = 0; i < n; i++)
        {
            double a = s + step * i, b = a + step;
            double ax = cx + r * Math.Cos(a), ay = cy + r * Math.Sin(a), bx = cx + r * Math.Cos(b), by = cy + r * Math.Sin(b);
            CurveTo(ax - k * r * Math.Sin(a), ay + k * r * Math.Cos(a), bx + k * r * Math.Sin(b), by - k * r * Math.Cos(b), bx, by);
        }
    }

    // ---------------------------------------------------------------- painting

    private bool Visible => !_layerHidden;

    private void AddClip(FillRule rule)
    {
        if (_verbs.Count == 0) return;
        _s.Clips = [.. _s.Clips, new PathGeometry(_verbs.ToArray(), _points.ToArray(), rule)];
    }

    private void PaintPath(bool fill, bool stroke, FillRule rule)
    {
        if (_verbs.Count == 0 || !Visible) return;
        var geometry = new PathGeometry(_verbs.ToArray(), _points.ToArray(), rule);
        if (_compound)
        {
            _cverbs.AddRange(geometry.Verbs); _cpoints.AddRange(geometry.Points);
            if (!_cHas) { _cFill = fill; _cStroke = stroke; _cHas = true; }
            return;
        }
        Emit(geometry, fill, stroke);
    }

    private void EndCompound()
    {
        if (!_compound) return;
        _compound = false;
        if (_cHas && _cverbs.Count > 0 && Visible)
            Emit(new PathGeometry(_cverbs.ToArray(), _cpoints.ToArray(), AiRule()), _cFill, _cStroke);
        _cverbs.Clear(); _cpoints.Clear(); _cHas = false;
    }

    private void Emit(PathGeometry geometry, bool fill, bool stroke)
    {
        var bounds = geometry.GetBounds();
        if (bounds.IsEmpty) return;
        bool wantFill = fill && (_s.Fill is not null || _s.FillPattern is not null);
        bool wantStroke = stroke && _s.Stroke is not null;
        if (!wantFill && !wantStroke) return;

        var clipped = wantFill ? _b.ApplyClips(geometry, _s.Clips) : geometry;
        if (clipped is null) return;
        var objectBounds = clipped.GetBounds();

        Paint? fillPaint = null;
        if (wantFill)
        {
            fillPaint = _s.FillPattern is { } pat ? PatternPaint(pat, objectBounds) : new SolidPaint(_s.Fill!.Value);
        }
        StrokeStyle? strokeStyle = null;
        if (wantStroke)
        {
            double width = Math.Max(0.25, _s.LineWidth * _s.Ctm.AverageScale);
            strokeStyle = new StrokeStyle(new SolidPaint(_s.Stroke!.Value), width, _s.Join, _s.Cap, _s.Miter);
            if (_s.Dashed) _b.Count("{0} stroke(s) are dashed in the file; dashes are not supported, so they are drawn solid.");
        }
        _b.Add(new PathObject(clipped) { Name = _pendingName, Style = new ObjectStyle(fillPaint, strokeStyle) });
    }

    // ---------------------------------------------------------------- groups

    private void BeginGroup()
    {
        if (!_compound) { _b.BeginGroup(_pendingName); _groupDepth++; }
        _pendingName = null;
        Gsave();
    }

    private void EndGroup()
    {
        Grestore();
        if (_compound) EndCompound();
        if (_groupDepth > 0) { _b.EndGroup(); _groupDepth--; }
    }

    // ---------------------------------------------------------------- patterns and shadings

    private Paint PatternPaint(PsPattern pattern, Rect2 bounds)
    {
        if (pattern.Shading is PsDict shading)
        {
            var paint = ShadingPaint(shading, MatrixOf(pattern), bounds, out string? note);
            if (note is not null) _b.Warn(note);
            return paint;
        }
        return new SolidPaint(_s.Fill ?? DocColor.Black);
    }

    private static Affine2 MatrixOf(PsPattern p) => p.Matrix;

    private void SetPattern()
    {
        var p = Pop();
        if (p is PsDict d && d.Map.TryGetValue("__matrix", out var m) && m is PsArr ma && d.Map.TryGetValue("Shading", out var sh))
        {
            var v = ma.Items.Take(6).Select(ToNum).ToArray();
            _s.FillPattern = new PsPattern(sh, new Affine2(v[0], v[1], v[2], v[3], v[4], v[5]));
        }
        else
        {
            _b.Count("{0} fill(s) use a tiling pattern, which is not supported; a flat gray is used instead.");
            _s.FillPattern = null; _s.Fill = new DocColor(160, 160, 160);
        }
    }

    private void ShFill()
    {
        if (Pop() is not PsDict shading || !Visible) return;
        PathGeometry region;
        if (_s.Clips.Count > 0)
        {
            region = _s.Clips[^1];
            if (_s.Clips.Count > 1) region = _b.ApplyClips(region, _s.Clips.Take(_s.Clips.Count - 1).ToList()) ?? region;
        }
        else region = PathGeometry.Rectangle(_b.Page.Bounds);
        var bounds = region.GetBounds();
        if (bounds.IsEmpty) return;
        var paint = ShadingPaint(shading, _s.Ctm, bounds, out string? note);
        if (note is not null) _b.Warn(note);
        _b.Add(new PathObject(region) { Name = _pendingName, Style = new ObjectStyle(paint, null) });
    }

    private Paint ShadingPaint(PsDict shading, Affine2 toDoc, Rect2 bounds, out string? note)
    {
        int type = shading.Map.TryGetValue("ShadingType", out var t) && t is double td ? (int)td : 0;
        var coords = shading.Map.TryGetValue("Coords", out var c) && c is PsArr ca ? ca.Items.OfType<double>().ToArray() : [];
        var domain = shading.Map.TryGetValue("Domain", out var d) && d is PsArr da ? da.Items.OfType<double>().ToArray() : [0, 1];
        string space = shading.Map.TryGetValue("ColorSpace", out var cs) ? (cs is PsArr a && a.Items.Count > 0 ? KeyOf(a.Items[0]) : KeyOf(cs)) : "DeviceRGB";
        var fn = shading.Map.TryGetValue("Function", out var f) ? ReadFunction(f) : null;
        DocColor? ColorAt(double x)
        {
            if (fn is null) return null;
            var comps = fn(x);
            return space switch
            {
                "DeviceCMYK" when comps.Length >= 4 => Cmyk(comps[0], comps[1], comps[2], comps[3]),
                "DeviceGray" when comps.Length >= 1 => Gray(comps[0]),
                _ when comps.Length >= 3 => Rgb(comps[0], comps[1], comps[2]),
                _ => null,
            };
        }
        return GradientRamps.Build(type, coords, domain.Length >= 2 ? domain[0] : 0, domain.Length >= 2 ? domain[1] : 1, ColorAt, [], toDoc, bounds, 1, out note);
    }

    /// <summary>A PostScript function dictionary (type 0 with its samples, type 2) as a delegate.</summary>
    private Func<double, double[]>? ReadFunction(object? f)
    {
        if (f is not PsDict d) return null;
        int type = d.Map.TryGetValue("FunctionType", out var t) && t is double td ? (int)td : -1;
        double[] Nums(string key, double[] fallback) => d.Map.TryGetValue(key, out var v) && v is PsArr a ? a.Items.OfType<double>().ToArray() : fallback;
        var domain = Nums("Domain", [0, 1]);
        if (type == 2)
        {
            var c0 = Nums("C0", [0]); var c1 = Nums("C1", [1]);
            double n = d.Map.TryGetValue("N", out var nv) && nv is double nd ? nd : 1;
            return x => { double u = Math.Pow(Math.Clamp(x, domain[0], domain[1]), n); return c0.Select((v, i) => v + u * ((i < c1.Length ? c1[i] : v) - v)).ToArray(); };
        }
        if (type == 0)
        {
            var range = Nums("Range", [0, 1, 0, 1, 0, 1]);
            var size = Nums("Size", [2]);
            int outputs = range.Length / 2, bps = d.Map.TryGetValue("BitsPerSample", out var bv) && bv is double bd ? (int)bd : 8;
            byte[] data = d.Map.TryGetValue("DataSource", out var ds) ? SourceBytes(ds, (int)size[0] * outputs * bps / 8) : [];
            if (data.Length == 0 || bps != 8) return null;
            int count = (int)size[0];
            return x =>
            {
                double pos = domain[1] - domain[0] < 1e-12 ? 0 : (Math.Clamp(x, domain[0], domain[1]) - domain[0]) / (domain[1] - domain[0]) * (count - 1);
                int lo = (int)Math.Floor(pos), hi = Math.Min(lo + 1, count - 1);
                double frac = pos - lo;
                var result = new double[outputs];
                for (int j = 0; j < outputs; j++)
                {
                    double a = lo * outputs + j < data.Length ? data[lo * outputs + j] / 255.0 : 0;
                    double b = hi * outputs + j < data.Length ? data[hi * outputs + j] / 255.0 : 0;
                    result[j] = range[2 * j] + (a + (b - a) * frac) * (range[2 * j + 1] - range[2 * j]);
                }
                return result;
            };
        }
        return null;
    }

    private sealed record PsFontObject(PsFont Font);

    // ---------------------------------------------------------------- text

    private string PopString() => Pop() is PsStr s ? s.ToString() : throw new PsError("a string was expected");

    private void ShowText(string text)
    {
        if (!Visible || text.Length == 0 || _s.Fill is null) return;
        var ctm = _s.Ctm;
        var ex = ctm.TransformVector(new Vec2(1, 0));
        double scale = ex.Length;
        if (!(scale > 1e-9)) return;
        var (family, weight, italic) = FontNames.FromPdf(_s.Font.Name, false, false);
        double size = _s.Font.Size * scale;
        var origin = ToDoc(_cux, _cuy);
        double angle = Math.Atan2(ex.Y, ex.X);
        var t = new TextObject(text, family, size)
        {
            FontWeight = weight, Italic = italic,
            Style = ObjectStyle.FillOnly(_s.Fill.Value),
            Transform = Affine2.Rotation(angle) * Affine2.Translation(origin),
        };
        if (!DeSignMage.Core.Text.TextEngine.Fonts.IsInstalled(family)) _b.Count("{0} text(s) use a font that is not installed here; they keep its name and show in a replacement font.");
        if (_options.LiveText) _b.Add(t); else _b.Add(VectorText.ToOutlines(t));
        double advance = t.Layout.Frame.Width / scale;
        _cux += advance;
    }
}
