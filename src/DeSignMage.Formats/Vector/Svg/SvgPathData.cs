using System.Globalization;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Formats.Vector.Svg;

/// <summary>The path data mini language of SVG (<c>d="M 10 10 L ..."</c>) as a <see cref="PathGeometry"/>. Arcs stay circular arcs when they can.</summary>
internal static class SvgPathData
{
    /// <summary>Parses the data; a syntax error ends the path where it is (as the SVG rules say), and <paramref name="complete"/> says so.</summary>
    public static PathGeometry Parse(string data, FillRule rule, out bool complete)
    {
        var b = new PathBuilder { FillRule = rule };
        complete = true;
        int i = 0;
        char cmd = '\0';
        Vec2 cur = Vec2.Zero, start = Vec2.Zero, lastCubic = Vec2.Zero, lastQuad = Vec2.Zero;
        bool haveCubic = false, haveQuad = false, open = false, started = false;
        var nums = new List<double>();

        bool Number(out double v)
        {
            SkipSeparators(data, ref i);
            return ReadNumber(data, ref i, out v);
        }
        bool Flag(out bool v)
        {
            SkipSeparators(data, ref i);
            v = false;
            if (i >= data.Length || (data[i] != '0' && data[i] != '1')) return false;
            v = data[i] == '1';
            i++;
            return true;
        }
        void EnsureOpen()
        {
            if (!open) { b.MoveTo(cur); start = cur; open = true; }
        }

        while (true)
        {
            SkipSeparators(data, ref i);
            if (i >= data.Length) break;
            char c = data[i];
            if (char.IsLetter(c)) { cmd = c; i++; if (cmd is 'Z' or 'z') { if (started && open) { b.Close(); cur = start; open = false; } haveCubic = haveQuad = false; continue; } }
            else if (cmd == '\0' || cmd is 'Z' or 'z') { complete = false; break; }
            else if (cmd is 'M') cmd = 'L';
            else if (cmd is 'm') cmd = 'l';

            bool rel = char.IsLower(cmd);
            char u = char.ToUpperInvariant(cmd);
            if (!started && u != 'M') { complete = false; break; }
            switch (u)
            {
                case 'M':
                {
                    if (!Number(out double x) || !Number(out double y)) { complete = false; return b.Build(); }
                    var p = rel ? cur + new Vec2(x, y) : new Vec2(x, y);
                    cur = start = p;
                    b.MoveTo(p);
                    open = true; started = true;
                    haveCubic = haveQuad = false;
                    cmd = rel ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    if (!Number(out double x) || !Number(out double y)) { complete = false; return b.Build(); }
                    EnsureOpen();
                    cur = rel ? cur + new Vec2(x, y) : new Vec2(x, y);
                    b.LineTo(cur);
                    haveCubic = haveQuad = false;
                    break;
                }
                case 'H':
                {
                    if (!Number(out double x)) { complete = false; return b.Build(); }
                    EnsureOpen();
                    cur = new Vec2(rel ? cur.X + x : x, cur.Y);
                    b.LineTo(cur);
                    haveCubic = haveQuad = false;
                    break;
                }
                case 'V':
                {
                    if (!Number(out double y)) { complete = false; return b.Build(); }
                    EnsureOpen();
                    cur = new Vec2(cur.X, rel ? cur.Y + y : y);
                    b.LineTo(cur);
                    haveCubic = haveQuad = false;
                    break;
                }
                case 'C':
                case 'S':
                {
                    nums.Clear();
                    int need = u == 'C' ? 6 : 4;
                    for (int k = 0; k < need; k++) { if (!Number(out double v)) { complete = false; return b.Build(); } nums.Add(v); }
                    EnsureOpen();
                    Vec2 Pt(int k) => rel ? cur + new Vec2(nums[k], nums[k + 1]) : new Vec2(nums[k], nums[k + 1]);
                    Vec2 c1, c2, end;
                    if (u == 'C') { c1 = Pt(0); c2 = Pt(2); end = Pt(4); }
                    else { c1 = haveCubic ? cur * 2 - lastCubic : cur; c2 = Pt(0); end = Pt(2); }
                    b.CubicTo(c1, c2, end);
                    lastCubic = c2; haveCubic = true; haveQuad = false;
                    cur = end;
                    break;
                }
                case 'Q':
                case 'T':
                {
                    nums.Clear();
                    int need = u == 'Q' ? 4 : 2;
                    for (int k = 0; k < need; k++) { if (!Number(out double v)) { complete = false; return b.Build(); } nums.Add(v); }
                    EnsureOpen();
                    Vec2 Pt(int k) => rel ? cur + new Vec2(nums[k], nums[k + 1]) : new Vec2(nums[k], nums[k + 1]);
                    Vec2 q, end;
                    if (u == 'Q') { q = Pt(0); end = Pt(2); }
                    else { q = haveQuad ? cur * 2 - lastQuad : cur; end = Pt(0); }
                    b.QuadTo(q, end);
                    lastQuad = q; haveQuad = true; haveCubic = false;
                    cur = end;
                    break;
                }
                case 'A':
                {
                    if (!Number(out double rx) || !Number(out double ry) || !Number(out double rot) || !Flag(out bool large) || !Flag(out bool sweep) ||
                        !Number(out double x) || !Number(out double y)) { complete = false; return b.Build(); }
                    EnsureOpen();
                    var end = rel ? cur + new Vec2(x, y) : new Vec2(x, y);
                    AddArc(b, cur, rx, ry, rot, large, sweep, end);
                    cur = end;
                    haveCubic = haveQuad = false;
                    break;
                }
                default:
                    complete = false;
                    return b.Build();
            }
        }
        return b.Build();
    }

    private static void SkipSeparators(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
    }

    private static bool ReadNumber(string s, ref int i, out double value)
    {
        value = 0;
        int p = i;
        if (p < s.Length && (s[p] == '+' || s[p] == '-')) p++;
        int digits = 0;
        while (p < s.Length && char.IsAsciiDigit(s[p])) { p++; digits++; }
        if (p < s.Length && s[p] == '.')
        {
            p++;
            while (p < s.Length && char.IsAsciiDigit(s[p])) { p++; digits++; }
        }
        if (digits == 0) return false;
        if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
        {
            int q = p + 1;
            if (q < s.Length && (s[q] == '+' || s[q] == '-')) q++;
            if (q < s.Length && char.IsAsciiDigit(s[q]))
            {
                while (q < s.Length && char.IsAsciiDigit(s[q])) q++;
                p = q;
            }
        }
        if (!double.TryParse(s.AsSpan(i, p - i), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        i = p;
        return true;
    }

    /// <summary>A number at the start of <paramref name="s"/> (for lengths such as "12px"); the rest is returned as the unit text.</summary>
    public static bool TryReadNumber(string s, out double value, out string rest)
    {
        int i = 0;
        SkipSeparators(s, ref i);
        bool ok = ReadNumber(s, ref i, out value);
        rest = ok ? s[i..].Trim() : s;
        return ok;
    }

    // ---------------------------------------------------------------- arcs (SVG implementation notes F.6)

    internal static void AddArc(PathBuilder b, Vec2 from, double rx, double ry, double degrees, bool large, bool sweep, Vec2 to)
    {
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        if (Vec2.Distance(from, to) < 1e-12) return;
        if (rx < 1e-12 || ry < 1e-12) { b.LineTo(to); return; }

        double phi = degrees * Math.PI / 180, cp = Math.Cos(phi), sp = Math.Sin(phi);
        double dx = (from.X - to.X) / 2, dy = (from.Y - to.Y) / 2;
        double x1 = cp * dx + sp * dy, y1 = -sp * dx + cp * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1) { double s = Math.Sqrt(lambda); rx *= s; ry *= s; }

        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double coef = den < 1e-18 ? 0 : Math.Sqrt(Math.Max(0, num / den));
        if (large == sweep) coef = -coef;
        double cx1 = coef * rx * y1 / ry, cy1 = -coef * ry * x1 / rx;
        double cx = cp * cx1 - sp * cy1 + (from.X + to.X) / 2, cy = sp * cx1 + cp * cy1 + (from.Y + to.Y) / 2;

        double Angle(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        double theta = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double delta = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        Vec2 At(double t) => new(cx + rx * Math.Cos(t) * cp - ry * Math.Sin(t) * sp, cy + rx * Math.Cos(t) * sp + ry * Math.Sin(t) * cp);

        if (Math.Abs(rx - ry) < 1e-9 * Math.Max(rx, 1) && Math.Abs(delta) < 2 * Math.PI - 1e-6)
        {
            b.ArcTo(At(theta + delta / 2), to);
            return;
        }
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        double step = delta / n;
        double k = 4.0 / 3.0 * Math.Tan(step / 4);
        for (int s = 0; s < n; s++)
        {
            double a0 = theta + step * s, a1 = a0 + step;
            Vec2 D(double a) => new(-rx * Math.Sin(a) * cp - ry * Math.Cos(a) * sp, -rx * Math.Sin(a) * sp + ry * Math.Cos(a) * cp);
            var p0 = At(a0); var p3 = s == n - 1 ? to : At(a1);
            b.CubicTo(p0 + D(a0) * k, p3 - D(a1) * k, p3);
        }
    }
}
