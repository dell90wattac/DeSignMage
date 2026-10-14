using System.Globalization;
using System.Text;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Vector.Eps;

/// <summary>
/// A small PostScript interpreter for what drawing programs write: the stack, arrays, dictionaries, procedures, control operators and the path and color
/// operators of the graphics model (<see cref="PsInterpreter"/> partial files add them), plus the operator set of Illustrator files (u, U, m, L, C, f, k, ...).
/// Operators that are not known are skipped. This is not a PostScript implementation: no fonts, no halftones, no device.
/// </summary>
internal sealed partial class PsInterpreter
{
    private const int MaxOperations = 60_000_000;
    private const int MaxDepth = 150;
    private const int MaxStack = 20_000;

    private sealed class PsExit : Exception;
    private sealed class PsStop : Exception;
    private sealed class PsError(string message) : Exception(message);

    private readonly List<object?> _stack = [];
    private readonly List<PsDict> _dicts = [];
    private readonly PsDict _userdict = new();
    private PsScanner _scanner;
    private long _operations;
    private int _depth;
    private bool _done;

    private PsInterpreter(PsScanner scanner, VectorBuilder b, VectorImportOptions options, Affine2 baseMatrix, bool aiMode)
    {
        _scanner = scanner;
        _dicts.Add(_userdict);
        _b = b; _options = options; _baseMatrix = baseMatrix; _aiMode = aiMode;
        _s.Ctm = baseMatrix;
    }

    // ---------------------------------------------------------------- running

    private void Run()
    {
        while (!_done)
        {
            var token = _scanner.Next();
            if (token is null) return;
            try { Execute(token, fromProc: false); }
            catch (PsExit) { }
            catch (PsStop) { }
            catch (PsError e) { Problem(e.Message); }
            catch (Exception e) when (e is InvalidCastException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or FormatException or OverflowException or KeyNotFoundException)
            {
                Problem($"a PostScript operator failed ({e.GetType().Name})");
            }
            if (_stack.Count > MaxStack) _stack.RemoveRange(0, _stack.Count - MaxStack / 2);
        }
    }

    private void Execute(object? token, bool fromProc)
    {
        if (++_operations > MaxOperations) { _done = true; Problem("the file is too complex; the rest was left out"); return; }
        switch (token)
        {
            case double or PsStr or bool or null: _stack.Add(token); return;
            case PsArr arr: _stack.Add(arr); return;
            case PsDict d: _stack.Add(d); return;
            case PsName name:
                if (name.Literal) { _stack.Add(name); return; }
                ExecName(name.Value);
                return;
            default: _stack.Add(token); return;
        }
    }

    private void ExecName(string name)
    {
        switch (name)
        {
            case "{": _stack.Add(ReadProcedure()); return;
            case "}": return;
        }
        // names the file defined win over the built-in operators, except the Illustrator operators of programs whose prolog is not in the file
        for (int i = _dicts.Count - 1; i >= 0; i--)
        {
            if (_dicts[i].Map.TryGetValue(name, out var value))
            {
                RunValue(value);
                return;
            }
        }
        if (!Builtin(name)) Unknown(name);
    }

    private void RunValue(object? value)
    {
        if (value is PsArr { Executable: true } proc) RunProcedure(proc);
        else if (value is PsName { Literal: false } n) ExecName(n.Value);
        else _stack.Add(value);
    }

    private void RunProcedure(PsArr proc)
    {
        if (++_depth > MaxDepth) { _depth--; throw new PsError("procedures are nested too deeply"); }
        try
        {
            foreach (var item in proc.Items)
            {
                if (_done) return;
                Execute(item is PsArr { Executable: true } p ? p : item, fromProc: true);
            }
        }
        finally { _depth--; }
    }

    private PsArr ReadProcedure()
    {
        var items = new List<object?>();
        int depth = 1;
        while (true)
        {
            var t = _scanner.Next();
            if (t is null) break;
            if (t is PsName { Literal: false, Value: "{" }) { items.Add(ReadProcedure()); continue; }
            if (t is PsName { Literal: false, Value: "}" }) break;
            items.Add(t);
            if (items.Count > 2_000_000) break;
        }
        _ = depth;
        return new PsArr(items, true);
    }

    // ---------------------------------------------------------------- stack helpers

    private object? Pop()
    {
        if (_stack.Count == 0) throw new PsError("stack underflow");
        var v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return v;
    }

    private double PopNum()
    {
        var v = Pop();
        if (v is double d) return d;
        if (v is bool b) return b ? 1 : 0;
        throw new PsError("a number was expected");
    }

    private int PopInt() => (int)Math.Round(PopNum());

    private bool PopBool() => Pop() switch { bool b => b, double d => d != 0, _ => throw new PsError("a boolean was expected") };

    private PsArr PopArray() => Pop() as PsArr ?? throw new PsError("an array was expected");

    private PsDict PopDict() => Pop() as PsDict ?? throw new PsError("a dictionary was expected");

    private string PopName() => Pop() switch { PsName n => n.Value, PsStr s => s.ToString(), _ => throw new PsError("a name was expected") };

    private object? Peek(int fromTop = 0) => _stack.Count > fromTop ? _stack[^(fromTop + 1)] : throw new PsError("stack underflow");

    private static bool IsNumber(object? o) => o is double;

    private void Push(object? o) => _stack.Add(o);

    private void PopN(int n)
    {
        n = Math.Min(n, _stack.Count);
        _stack.RemoveRange(_stack.Count - n, n);
    }

    /// <summary>The numbers of the top <paramref name="n"/> operands (in order), removed.</summary>
    private double[] PopNumbers(int n)
    {
        if (_stack.Count < n) throw new PsError("stack underflow");
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = _stack[_stack.Count - n + i] is double d ? d : 0;
        _stack.RemoveRange(_stack.Count - n, n);
        return r;
    }

    private int MarkIndex()
    {
        for (int i = _stack.Count - 1; i >= 0; i--) if (_stack[i] is PsMark) return i;
        throw new PsError("no mark on the stack");
    }

    // ---------------------------------------------------------------- built-in operators (language)

    private bool Builtin(string name)
    {
        switch (name)
        {
            // ---- stack
            case "pop": Pop(); return true;
            case "exch": { var b = Pop(); var a = Pop(); Push(b); Push(a); return true; }
            case "dup": Push(Peek()); return true;
            case "copy":
            {
                if (Peek() is double)
                {
                    int n = PopInt();
                    if (n < 0 || n > _stack.Count) throw new PsError("copy out of range");
                    _stack.AddRange(_stack.GetRange(_stack.Count - n, n));
                }
                else
                {
                    var src = Pop(); var dst = Pop();
                    if (src is PsArr sa && dst is PsArr da) { da.Items.Clear(); da.Items.AddRange(sa.Items); Push(da); }
                    else if (src is PsDict sd && dst is PsDict dd) { foreach (var kv in sd.Map) dd.Map[kv.Key] = kv.Value; Push(dd); }
                    else Push(dst);
                }
                return true;
            }
            case "index": { int n = PopInt(); Push(_stack[^(n + 1)]); return true; }
            case "roll":
            {
                int j = PopInt(), n = PopInt();
                if (n > 0 && n <= _stack.Count)
                {
                    var part = _stack.GetRange(_stack.Count - n, n);
                    _stack.RemoveRange(_stack.Count - n, n);
                    j = ((j % n) + n) % n;
                    for (int i = 0; i < n; i++) _stack.Add(part[(i - j + n) % n]);
                }
                return true;
            }
            case "clear": _stack.Clear(); return true;
            case "count": Push((double)_stack.Count); return true;
            case "mark" or "[" or "<<": Push(PsMark.Instance); return true;
            case "counttomark": Push((double)(_stack.Count - 1 - MarkIndex())); return true;
            case "cleartomark": { int m = MarkIndex(); _stack.RemoveRange(m, _stack.Count - m); return true; }
            case "]":
            {
                int m = MarkIndex();
                var items = _stack.GetRange(m + 1, _stack.Count - m - 1);
                _stack.RemoveRange(m, _stack.Count - m);
                Push(new PsArr(items, false));
                return true;
            }
            case ">>":
            {
                int m = MarkIndex();
                var d = new PsDict();
                for (int i = m + 1; i + 1 < _stack.Count; i += 2) d.Map[KeyOf(_stack[i])] = _stack[i + 1];
                _stack.RemoveRange(m, _stack.Count - m);
                Push(d);
                return true;
            }

            // ---- arithmetic
            case "add": { var b = PopNum(); Push(PopNum() + b); return true; }
            case "sub": { var b = PopNum(); Push(PopNum() - b); return true; }
            case "mul": { var b = PopNum(); Push(PopNum() * b); return true; }
            case "div": { var b = PopNum(); var a = PopNum(); Push(b == 0 ? 0 : a / b); return true; }
            case "idiv": { var b = PopNum(); var a = PopNum(); Push(b == 0 ? 0 : Math.Truncate(a / b)); return true; }
            case "mod": { var b = PopNum(); var a = PopNum(); Push(b == 0 ? 0 : a % b); return true; }
            case "neg": Push(-PopNum()); return true;
            case "abs": Push(Math.Abs(PopNum())); return true;
            case "ceiling": Push(Math.Ceiling(PopNum())); return true;
            case "floor": Push(Math.Floor(PopNum())); return true;
            case "round": Push(Math.Floor(PopNum() + 0.5)); return true;
            case "truncate" or "cvi": Push(Math.Truncate(PopNum())); return true;
            case "cvr": return true;
            case "sqrt": Push(Math.Sqrt(Math.Max(0, PopNum()))); return true;
            case "exp": { var b = PopNum(); Push(Math.Pow(PopNum(), b)); return true; }
            case "ln": Push(Math.Log(PopNum())); return true;
            case "log": Push(Math.Log10(PopNum())); return true;
            case "sin": Push(Math.Sin(PopNum() * Math.PI / 180)); return true;
            case "cos": Push(Math.Cos(PopNum() * Math.PI / 180)); return true;
            case "atan": { var den = PopNum(); var num = PopNum(); double a = Math.Atan2(num, den) * 180 / Math.PI; Push(a < 0 ? a + 360 : a); return true; }
            case "min": { var b = PopNum(); Push(Math.Min(PopNum(), b)); return true; }
            case "max": { var b = PopNum(); Push(Math.Max(PopNum(), b)); return true; }
            case "rand": Push(4.0); return true;

            // ---- comparison, logic
            case "eq": { var b = Pop(); var a = Pop(); Push(Same(a, b)); return true; }
            case "ne": { var b = Pop(); var a = Pop(); Push(!Same(a, b)); return true; }
            case "gt": { var b = PopNum(); Push(PopNum() > b); return true; }
            case "ge": { var b = PopNum(); Push(PopNum() >= b); return true; }
            case "lt": { var b = PopNum(); Push(PopNum() < b); return true; }
            case "le": { var b = PopNum(); Push(PopNum() <= b); return true; }
            case "true": Push(true); return true;
            case "false": Push(false); return true;
            case "and": { var b = Pop(); var a = Pop(); Push(a is bool x && b is bool y ? x && y : (object)(double)((long)ToNum(a) & (long)ToNum(b))); return true; }
            case "or": { var b = Pop(); var a = Pop(); Push(a is bool x && b is bool y ? x || y : (object)(double)((long)ToNum(a) | (long)ToNum(b))); return true; }
            case "xor": { var b = Pop(); var a = Pop(); Push(a is bool x && b is bool y ? x ^ y : (object)(double)((long)ToNum(a) ^ (long)ToNum(b))); return true; }
            case "not": { var a = Pop(); Push(a is bool x ? !x : (object)(double)~(long)ToNum(a)); return true; }
            case "bitshift": { int s = PopInt(); long v = (long)PopNum(); Push((double)(s >= 0 ? v << s : v >> -s)); return true; }

            // ---- control
            case "if": { var proc = PopArray(); if (PopBool()) RunProcedure(proc); return true; }
            case "ifelse": { var no = PopArray(); var yes = PopArray(); RunProcedure(PopBool() ? yes : no); return true; }
            case "repeat":
            {
                var proc = PopArray(); int n = PopInt();
                try { for (int i = 0; i < n && !_done; i++) RunProcedure(proc); } catch (PsExit) { }
                return true;
            }
            case "loop":
            {
                var proc = PopArray();
                try { while (!_done) RunProcedure(proc); } catch (PsExit) { }
                return true;
            }
            case "for":
            {
                var proc = PopArray(); double limit = PopNum(), inc = PopNum(), start = PopNum();
                if (inc == 0) return true;
                try { for (double v = start; (inc > 0 ? v <= limit : v >= limit) && !_done; v += inc) { Push(v); RunProcedure(proc); } } catch (PsExit) { }
                return true;
            }
            case "forall":
            {
                var proc = PopArray(); var obj = Pop();
                try
                {
                    switch (obj)
                    {
                        case PsArr a: foreach (var item in a.Items.ToList()) { Push(item); RunProcedure(proc); } break;
                        case PsStr s: foreach (byte b in s.Bytes) { Push((double)b); RunProcedure(proc); } break;
                        case PsDict d: foreach (var kv in d.Map.ToList()) { Push(new PsName(kv.Key, true)); Push(kv.Value); RunProcedure(proc); } break;
                    }
                }
                catch (PsExit) { }
                return true;
            }
            case "exit": throw new PsExit();
            case "stop": throw new PsStop();
            case "stopped": { var proc = Pop(); bool stopped = false; try { RunValue(proc); } catch (PsStop) { stopped = true; } catch (PsError) { stopped = true; } Push(stopped); return true; }
            case "exec": RunValue(Pop()); return true;
            case "bind" or "readonly" or "executeonly" or "noaccess" or "cvlit": return true;
            case "cvx": { var o = Pop(); Push(o is PsArr a ? new PsArr(a.Items, true) : o is PsName n ? new PsName(n.Value, false) : o); return true; }
            case "xcheck": { var o = Pop(); Push(o is PsArr { Executable: true } || o is PsName { Literal: false }); return true; }
            case "quit": _done = true; return true;
            case "showpage" or "copypage": _done = true; return true;

            // ---- dictionaries and names
            case "def": { var v = Pop(); var k = Pop(); _dicts[^1].Map[KeyOf(k)] = v; return true; }
            case "store": { var v = Pop(); string k = KeyOf(Pop()); for (int i = _dicts.Count - 1; i >= 0; i--) if (_dicts[i].Map.ContainsKey(k)) { _dicts[i].Map[k] = v; return true; } _dicts[^1].Map[k] = v; return true; }
            case "load": { string k = KeyOf(Pop()); Push(Lookup(k) ?? throw new PsError("undefined: " + k)); return true; }
            case "known": { string k = KeyOf(Pop()); Push(PopDict().Map.ContainsKey(k)); return true; }
            case "where": { string k = KeyOf(Pop()); var d = _dicts.LastOrDefault(x => x.Map.ContainsKey(k)); if (d is null) Push(false); else { Push(d); Push(true); } return true; }
            case "dict": { PopNum(); Push(new PsDict()); return true; }
            case "begin": _dicts.Add(PopDict()); return true;
            case "end": if (_dicts.Count > 1) _dicts.RemoveAt(_dicts.Count - 1); return true;
            case "currentdict": Push(_dicts[^1]); return true;
            case "userdict": Push(_userdict); return true;
            case "systemdict" or "globaldict" or "statusdict" or "errordict": Push(new PsDict()); return true;
            case "undef": { string k = KeyOf(Pop()); PopDict().Map.Remove(k); return true; }
            case "get":
            {
                var key = Pop(); var obj = Pop();
                switch (obj)
                {
                    case PsArr a: Push(a.Items[(int)ToNum(key)]); break;
                    case PsStr s: Push((double)s.Bytes[(int)ToNum(key)]); break;
                    case PsDict d: Push(d.Map.TryGetValue(KeyOf(key), out var v) ? v : throw new PsError("undefined key " + KeyOf(key))); break;
                    default: throw new PsError("get needs an array, string or dictionary");
                }
                return true;
            }
            case "put":
            {
                var v = Pop(); var key = Pop(); var obj = Pop();
                switch (obj)
                {
                    case PsArr a: a.Items[(int)ToNum(key)] = v; break;
                    case PsStr s: s.Bytes[(int)ToNum(key)] = (byte)ToNum(v); break;
                    case PsDict d: d.Map[KeyOf(key)] = v; break;
                }
                return true;
            }
            case "length":
            {
                Push(Pop() switch { PsArr a => (double)a.Items.Count, PsStr s => s.Bytes.Length, PsDict d => d.Map.Count, PsName n => n.Value.Length, _ => 0.0 });
                return true;
            }
            case "array": { int n = PopInt(); Push(new PsArr(Enumerable.Repeat<object?>(0.0, Math.Clamp(n, 0, 100_000)).ToList(), false)); return true; }
            case "string": { int n = PopInt(); Push(new PsStr(new byte[Math.Clamp(n, 0, 10_000_000)])); return true; }
            case "aload": { var a = PopArray(); foreach (var i in a.Items) Push(i); Push(a); return true; }
            case "astore": { var a = PopArray(); for (int i = a.Items.Count - 1; i >= 0; i--) a.Items[i] = Pop(); Push(a); return true; }
            case "getinterval":
            {
                int count = PopInt(), index = PopInt(); var obj = Pop();
                if (obj is PsArr a) Push(new PsArr(a.Items.GetRange(index, count), a.Executable));
                else if (obj is PsStr s) Push(new PsStr(s.Bytes[index..(index + count)]));
                return true;
            }
            case "cvs": { var s = Pop(); var o = Pop(); Push(new PsStr(Encoding.Latin1.GetBytes(Text(o)))); return true; }
            case "cvn": Push(new PsName(PopName(), true)); return true;
            case "type": { var o = Pop(); Push(new PsName(o switch { double d => d == Math.Floor(d) ? "integertype" : "realtype", bool => "booleantype", PsStr => "stringtype", PsName => "nametype", PsArr => "arraytype", PsDict => "dicttype", PsMark => "marktype", _ => "nulltype" }, false)); return true; }
            case "null": Push(null); return true;
            case "currentfile": Push(new PsFile(this)); return true;
            case "languagelevel": Push(3.0); return true;
            case "version": Push(new PsStr("3010"u8.ToArray())); return true;
            case "product": Push(new PsStr("DeSign Mage"u8.ToArray())); return true;
            case "revision" or "serialnumber": Push(1.0); return true;
            case "usertime" or "realtime" or "vmstatus": Push(0.0); return true;
            case "save": Push(new PsSave(SaveGraphics())); return true;
            case "restore": { var s = Pop(); if (s is PsSave save) RestoreGraphics(save.Level); return true; }
            case "filter": return Filter();
            case "readhexstring" or "readstring" or "readline": return ReadFromFile(name);
            case "closefile": Pop(); return true;
            case "flush" or "flushfile" or "prompt" or "print" or "==" or "=" or "pstack" or "stack": if (name is "print" or "==" or "=") Pop(); return true;
        }
        return Graphics(name);
    }

    private object? Lookup(string key)
    {
        for (int i = _dicts.Count - 1; i >= 0; i--) if (_dicts[i].Map.TryGetValue(key, out var v)) return v;
        return null;
    }

    private static string KeyOf(object? k) => k switch { PsName n => n.Value, PsStr s => s.ToString(), double d => d.ToString(CultureInfo.InvariantCulture), _ => "?" };

    private static double ToNum(object? o) => o is double d ? d : o is bool b ? (b ? 1 : 0) : throw new PsError("a number was expected");

    private static string Text(object? o) => o switch
    {
        double d => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.######", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        PsName n => n.Value,
        PsStr s => s.ToString(),
        _ => "--nostringval--",
    };

    private static bool Same(object? a, object? b)
    {
        if (a is double x && b is double y) return x == y;
        if (a is bool p && b is bool q) return p == q;
        if ((a is PsName || a is PsStr) && (b is PsName || b is PsStr)) return Text(a) == Text(b);
        if (a is null && b is null) return true;
        return ReferenceEquals(a, b);
    }

    private sealed record PsSave(int Level);
}
