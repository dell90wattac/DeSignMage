using System.Globalization;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>A PDF function (types 0 sampled, 2 exponential, 3 stitching, 4 PostScript calculator) or an array of one-output functions.</summary>
internal abstract class PdfFunction
{
    public double[] Domain { get; protected set; } = [0, 1];
    public double[]? Range { get; protected set; }
    public int Inputs => Domain.Length / 2;
    public abstract int Outputs { get; }

    protected abstract double[] Compute(double[] x);

    /// <summary>Inputs where a one-input function changes its formula (the bounds of a stitching function); a color ramp is sampled there too.</summary>
    public virtual IEnumerable<double> Breakpoints() => [];

    public double[] Eval(params double[] input)
    {
        var x = new double[Inputs];
        for (int i = 0; i < x.Length; i++)
        {
            double v = i < input.Length ? input[i] : 0;
            x[i] = Math.Clamp(v, Domain[2 * i], Domain[2 * i + 1]);
        }
        var y = Compute(x);
        if (Range is { } r)
            for (int i = 0; i < y.Length && 2 * i + 1 < r.Length; i++) y[i] = Math.Clamp(y[i], r[2 * i], r[2 * i + 1]);
        return y;
    }

    /// <summary>Reads a function object (a dictionary, a stream or an array of functions); null when it cannot be read.</summary>
    public static PdfFunction? Read(PdfFile file, IToken? token, int depth = 0)
    {
        if (depth > 8) return null;
        token = file.Resolve(token);
        if (token is ArrayToken arr)
        {
            var parts = new List<PdfFunction>();
            foreach (var item in arr.Data)
            {
                var f = Read(file, item, depth + 1);
                if (f is null) return null;
                parts.Add(f);
            }
            return parts.Count == 0 ? null : new Combined(parts);
        }
        if (token is NameToken { Data: "Identity" }) return new Identity();
        var dict = file.Dict(token);
        if (dict is null) return null;
        int type = (int)file.GetNumber(dict, "FunctionType", -1);
        var domain = file.Numbers(file.Get(dict, "Domain")) ?? [0, 1];
        if (domain.Length < 2) domain = [0, 1];
        var range = file.Numbers(file.Get(dict, "Range"));
        try
        {
            PdfFunction? f = type switch
            {
                0 => Sampled.Create(file, token as StreamToken, dict, domain, range),
                2 => Exponential.Create(file, dict, domain),
                3 => Stitching.Create(file, dict, domain, depth),
                4 => token is StreamToken s ? PostScript.Create(file, s, domain, range) : null,
                _ => null,
            };
            if (f is not null) { f.Domain = domain; f.Range = range; }
            return f;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class Identity : PdfFunction
    {
        public override int Outputs => 1;
        protected override double[] Compute(double[] x) => [x[0]];
    }

    private sealed class Combined(List<PdfFunction> parts) : PdfFunction
    {
        public override int Outputs => parts.Sum(p => p.Outputs);

        protected override double[] Compute(double[] x) => parts.SelectMany(p => p.Eval(x)).ToArray();

        public override IEnumerable<double> Breakpoints() => parts.SelectMany(p => p.Breakpoints());
    }

    // ---------------------------------------------------------------- type 2

    private sealed class Exponential : PdfFunction
    {
        private double[] _c0 = [0], _c1 = [1];
        private double _n = 1;
        public override int Outputs => _c0.Length;

        public static Exponential Create(PdfFile file, DictionaryToken dict, double[] domain)
        {
            var f = new Exponential
            {
                _c0 = file.Numbers(file.Get(dict, "C0")) ?? [0],
                _c1 = file.Numbers(file.Get(dict, "C1")) ?? [1],
                _n = file.GetNumber(dict, "N", 1),
            };
            if (f._c1.Length != f._c0.Length) f._c1 = f._c0.Select((_, i) => i < f._c1.Length ? f._c1[i] : 1.0).ToArray();
            return f;
        }

        protected override double[] Compute(double[] x)
        {
            double t = _n == 1 ? x[0] : Math.Pow(x[0], _n);
            var y = new double[_c0.Length];
            for (int i = 0; i < y.Length; i++) y[i] = _c0[i] + t * (_c1[i] - _c0[i]);
            return y;
        }
    }

    // ---------------------------------------------------------------- type 3

    private sealed class Stitching : PdfFunction
    {
        private List<PdfFunction> _functions = [];
        private double[] _bounds = [], _encode = [];
        public override int Outputs => _functions[0].Outputs;

        public override IEnumerable<double> Breakpoints() => _bounds.Concat(_functions.SelectMany(f => f.Breakpoints()));

        public static Stitching? Create(PdfFile file, DictionaryToken dict, double[] domain, int depth)
        {
            var fs = file.Array(file.Get(dict, "Functions"));
            if (fs is null) return null;
            var list = new List<PdfFunction>();
            foreach (var item in fs.Data)
            {
                var f = Read(file, item, depth + 1);
                if (f is null) return null;
                list.Add(f);
            }
            if (list.Count == 0) return null;
            var bounds = file.Numbers(file.Get(dict, "Bounds")) ?? [];
            var encode = file.Numbers(file.Get(dict, "Encode")) ?? [];
            if (bounds.Length < list.Count - 1) return null;
            if (encode.Length < 2 * list.Count) encode = Enumerable.Range(0, list.Count).SelectMany(_ => new[] { 0.0, 1.0 }).ToArray();
            return new Stitching { _functions = list, _bounds = bounds, _encode = encode };
        }

        protected override double[] Compute(double[] x)
        {
            double v = x[0];
            int k = 0;
            while (k < _bounds.Length && k < _functions.Count - 1 && v >= _bounds[k]) k++;
            double lo = k == 0 ? Domain[0] : _bounds[k - 1];
            double hi = k >= _bounds.Length || k == _functions.Count - 1 ? Domain[1] : _bounds[k];
            double e0 = _encode[2 * k], e1 = _encode[2 * k + 1];
            double t = hi - lo < 1e-12 ? e0 : e0 + (v - lo) * (e1 - e0) / (hi - lo);
            return _functions[k].Eval(t);
        }
    }

    // ---------------------------------------------------------------- type 0

    private sealed class Sampled : PdfFunction
    {
        private int[] _size = [];
        private double[] _encode = [], _decode = [];
        private double[] _samples = [];
        private int _outputs;
        public override int Outputs => _outputs;

        public static Sampled? Create(PdfFile file, StreamToken? stream, DictionaryToken dict, double[] domain, double[]? range)
        {
            if (stream is null || range is null || range.Length < 2) return null;
            int m = domain.Length / 2, n = range.Length / 2;
            var size = file.Numbers(file.Get(dict, "Size"))?.Select(v => (int)v).ToArray();
            if (size is null || size.Length < m) return null;
            int bps = (int)file.GetNumber(dict, "BitsPerSample", 8);
            var encode = file.Numbers(file.Get(dict, "Encode")) ?? Enumerable.Range(0, m).SelectMany(i => new[] { 0.0, size[i] - 1.0 }).ToArray();
            var decode = file.Numbers(file.Get(dict, "Decode")) ?? range;
            var data = file.Decode(stream, out _);
            long count = n;
            for (int i = 0; i < m; i++) count *= size[i];
            if (count > 4_000_000 || count <= 0) return null;
            var samples = new double[count];
            double max = Math.Pow(2, bps) - 1;
            long bit = 0;
            for (long i = 0; i < count; i++)
            {
                long v = 0;
                for (int b = 0; b < bps; b++, bit++)
                {
                    long byteIndex = bit >> 3;
                    int val = byteIndex < data.Length ? (data[byteIndex] >> (7 - (int)(bit & 7))) & 1 : 0;
                    v = (v << 1) | (uint)val;
                }
                samples[i] = v / max;
            }
            return new Sampled { _size = size, _encode = encode, _decode = decode, _samples = samples, _outputs = n };
        }

        protected override double[] Compute(double[] x)
        {
            int m = Inputs;
            var e = new double[m];
            for (int i = 0; i < m; i++)
            {
                double d0 = Domain[2 * i], d1 = Domain[2 * i + 1];
                double t = d1 - d0 < 1e-12 ? 0 : (x[i] - d0) / (d1 - d0);
                e[i] = Math.Clamp(_encode[2 * i] + t * (_encode[2 * i + 1] - _encode[2 * i]), 0, _size[i] - 1);
            }
            var y = new double[_outputs];
            // multilinear interpolation over the 2^m corners
            int corners = 1 << m;
            for (int c = 0; c < corners; c++)
            {
                double w = 1;
                long index = 0, stride = 1;
                for (int i = 0; i < m; i++)
                {
                    int lo = (int)Math.Floor(e[i]);
                    int hi = Math.Min(lo + 1, _size[i] - 1);
                    double frac = e[i] - lo;
                    bool up = (c >> i & 1) == 1;
                    w *= up ? frac : 1 - frac;
                    index += (up ? hi : lo) * stride;
                    stride *= _size[i];
                }
                if (w == 0) continue;
                for (int j = 0; j < _outputs; j++) y[j] += w * _samples[index * _outputs + j];
            }
            for (int j = 0; j < _outputs; j++) y[j] = _decode[2 * j] + y[j] * (_decode[2 * j + 1] - _decode[2 * j]);
            return y;
        }
    }

    // ---------------------------------------------------------------- type 4

    private sealed class PostScript : PdfFunction
    {
        private List<object> _program = [];
        private int _outputs;
        public override int Outputs => _outputs;

        public static PostScript? Create(PdfFile file, StreamToken stream, double[] domain, double[]? range)
        {
            if (range is null) return null;
            var text = System.Text.Encoding.Latin1.GetString(file.Decode(stream, out _));
            var tokens = text.Replace("{", " { ").Replace("}", " } ").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            if (tokens.Length == 0 || tokens[0] != "{") return null;
            i = 1;
            var program = ReadBlock(tokens, ref i);
            return new PostScript { _program = program, _outputs = range.Length / 2 };
        }

        private static List<object> ReadBlock(string[] t, ref int i)
        {
            var list = new List<object>();
            while (i < t.Length)
            {
                string s = t[i++];
                if (s == "}") return list;
                if (s == "{") list.Add(ReadBlock(t, ref i));
                else if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) list.Add(v);
                else list.Add(s);
            }
            return list;
        }

        protected override double[] Compute(double[] x)
        {
            var stack = new List<double>(x);
            Run(_program, stack, 0);
            var y = new double[_outputs];
            int n = Math.Min(_outputs, stack.Count);
            for (int i = 0; i < n; i++) y[_outputs - 1 - i] = stack[stack.Count - 1 - i];
            return y;
        }

        private static void Run(List<object> program, List<double> s, int depth)
        {
            if (depth > 32) return;
            for (int pc = 0; pc < program.Count; pc++)
            {
                switch (program[pc])
                {
                    case double v: s.Add(v); break;
                    case List<object> block:
                    {
                        // a block is followed by "if" or by another block and "ifelse"
                        if (pc + 1 < program.Count && program[pc + 1] is "if")
                        {
                            pc++;
                            if (Pop(s) != 0) Run(block, s, depth + 1);
                        }
                        else if (pc + 2 < program.Count && program[pc + 1] is List<object> other && program[pc + 2] is "ifelse")
                        {
                            pc += 2;
                            Run(Pop(s) != 0 ? block : other, s, depth + 1);
                        }
                        break;
                    }
                    case string op: Operator(op, s); break;
                }
            }
        }

        private static double Pop(List<double> s)
        {
            if (s.Count == 0) return 0;
            double v = s[^1];
            s.RemoveAt(s.Count - 1);
            return v;
        }

        private static void Operator(string op, List<double> s)
        {
            double a, b;
            switch (op)
            {
                case "abs": s.Add(Math.Abs(Pop(s))); break;
                case "add": b = Pop(s); a = Pop(s); s.Add(a + b); break;
                case "sub": b = Pop(s); a = Pop(s); s.Add(a - b); break;
                case "mul": b = Pop(s); a = Pop(s); s.Add(a * b); break;
                case "div": b = Pop(s); a = Pop(s); s.Add(b == 0 ? 0 : a / b); break;
                case "idiv": b = Pop(s); a = Pop(s); s.Add(b == 0 ? 0 : Math.Truncate(a / b)); break;
                case "mod": b = Pop(s); a = Pop(s); s.Add(b == 0 ? 0 : a % b); break;
                case "neg": s.Add(-Pop(s)); break;
                case "atan": b = Pop(s); a = Pop(s); { double d = Math.Atan2(a, b) * 180 / Math.PI; s.Add(d < 0 ? d + 360 : d); } break;
                case "ceiling": s.Add(Math.Ceiling(Pop(s))); break;
                case "floor": s.Add(Math.Floor(Pop(s))); break;
                case "round": s.Add(Math.Floor(Pop(s) + 0.5)); break;
                case "truncate": case "cvi": s.Add(Math.Truncate(Pop(s))); break;
                case "cvr": break;
                case "cos": s.Add(Math.Cos(Pop(s) * Math.PI / 180)); break;
                case "sin": s.Add(Math.Sin(Pop(s) * Math.PI / 180)); break;
                case "exp": b = Pop(s); a = Pop(s); s.Add(Math.Pow(a, b)); break;
                case "ln": s.Add(Math.Log(Pop(s))); break;
                case "log": s.Add(Math.Log10(Pop(s))); break;
                case "sqrt": s.Add(Math.Sqrt(Math.Max(0, Pop(s)))); break;
                case "and": b = Pop(s); a = Pop(s); s.Add((long)a & (long)b); break;
                case "or": b = Pop(s); a = Pop(s); s.Add((long)a | (long)b); break;
                case "xor": b = Pop(s); a = Pop(s); s.Add((long)a ^ (long)b); break;
                case "not": a = Pop(s); s.Add(a == 0 ? 1 : a == 1 ? 0 : ~(long)a); break;
                case "bitshift": b = Pop(s); a = Pop(s); s.Add(b >= 0 ? (long)a << (int)b : (long)a >> (int)-b); break;
                case "eq": b = Pop(s); a = Pop(s); s.Add(a == b ? 1 : 0); break;
                case "ne": b = Pop(s); a = Pop(s); s.Add(a != b ? 1 : 0); break;
                case "gt": b = Pop(s); a = Pop(s); s.Add(a > b ? 1 : 0); break;
                case "ge": b = Pop(s); a = Pop(s); s.Add(a >= b ? 1 : 0); break;
                case "lt": b = Pop(s); a = Pop(s); s.Add(a < b ? 1 : 0); break;
                case "le": b = Pop(s); a = Pop(s); s.Add(a <= b ? 1 : 0); break;
                case "true": s.Add(1); break;
                case "false": s.Add(0); break;
                case "pop": Pop(s); break;
                case "exch": b = Pop(s); a = Pop(s); s.Add(b); s.Add(a); break;
                case "dup": a = Pop(s); s.Add(a); s.Add(a); break;
                case "copy":
                {
                    int n = (int)Pop(s);
                    if (n > 0 && n <= s.Count) s.AddRange(s.GetRange(s.Count - n, n));
                    break;
                }
                case "index":
                {
                    int n = (int)Pop(s);
                    s.Add(n >= 0 && n < s.Count ? s[s.Count - 1 - n] : 0);
                    break;
                }
                case "roll":
                {
                    int j = (int)Pop(s), n = (int)Pop(s);
                    if (n > 1 && n <= s.Count)
                    {
                        var part = s.GetRange(s.Count - n, n);
                        s.RemoveRange(s.Count - n, n);
                        j = ((j % n) + n) % n;
                        for (int i = 0; i < n; i++) s.Add(part[(i - j + n) % n]);
                    }
                    break;
                }
            }
        }
    }
}
