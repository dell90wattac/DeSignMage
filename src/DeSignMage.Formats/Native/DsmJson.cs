using System.Globalization;
using System.Text;
using System.Text.Json;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>
/// Small JSON helpers for codecs. Reading is lenient about <i>missing</i> fields (a default is used) and strict about
/// <i>wrong</i> ones (a <see cref="DsmFormatException"/>, which skips just the object being read, never the whole file).
/// Unknown fields are ignored, so files written by a newer version still open.
/// </summary>
public static class DsmJson
{
    // ---------------------------------------------------------------- reading

    public static bool Has(this JsonElement e, string name) => TryGet(e, name, out _);

    public static bool TryGet(this JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new DsmFormatException("An object was expected here.");
        return e.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    public static double Num(this JsonElement e, string name, double fallback = 0)
    {
        if (!TryGet(e, name, out var v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number) throw new DsmFormatException($"'{name}' must be a number.");
        return v.GetDouble();
    }

    public static int Int(this JsonElement e, string name, int fallback = 0)
    {
        if (!TryGet(e, name, out var v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i)) throw new DsmFormatException($"'{name}' must be a whole number.");
        return i;
    }

    public static bool Bool(this JsonElement e, string name, bool fallback = false)
    {
        if (!TryGet(e, name, out var v)) return fallback;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new DsmFormatException($"'{name}' must be true or false.");
        return v.GetBoolean();
    }

    public static string? Str(this JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) throw new DsmFormatException($"'{name}' must be text.");
        return v.GetString();
    }

    public static JsonElement Obj(this JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v) || v.ValueKind != JsonValueKind.Object) throw new DsmFormatException($"'{name}' is missing or not an object.");
        return v;
    }

    public static JsonElement Arr(this JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v) || v.ValueKind != JsonValueKind.Array) throw new DsmFormatException($"'{name}' is missing or not a list.");
        return v;
    }

    /// <summary>The elements of an optional list (empty when absent).</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return [];
        if (v.ValueKind != JsonValueKind.Array) throw new DsmFormatException($"'{name}' must be a list.");
        return v.EnumerateArray();
    }

    public static double[] Doubles(this JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return [];
        if (v.ValueKind != JsonValueKind.Array) throw new DsmFormatException($"'{name}' must be a list of numbers.");
        var result = new double[v.GetArrayLength()];
        int i = 0;
        foreach (var n in v.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number) throw new DsmFormatException($"'{name}' must contain only numbers.");
            result[i++] = n.GetDouble();
        }
        return result;
    }

    /// <summary>Reads an enum stored as its (camelCase) name; unknown names give <paramref name="fallback"/>.</summary>
    public static T EnumOf<T>(this JsonElement e, string name, T fallback, DsmReadContext? ctx = null) where T : struct, Enum
    {
        var s = Str(e, name);
        if (s is null) return fallback;
        if (Enum.TryParse<T>(s, ignoreCase: true, out var value) && Enum.IsDefined(value)) return value;
        ctx?.Warn($"Unknown {typeof(T).Name} '{s}' was replaced by {fallback}.");
        return fallback;
    }

    public static Affine2 ReadTransform(this JsonElement e, string name)
    {
        if (!TryGet(e, name, out _)) return Affine2.Identity;
        var m = Doubles(e, name);
        if (m.Length != 6) throw new DsmFormatException($"'{name}' must have 6 numbers.");
        return new Affine2(m[0], m[1], m[2], m[3], m[4], m[5]);
    }

    public static DocColor ReadColor(JsonElement e)
    {
        // "#RRGGBB" / "#AARRGGBB", or { "hex", "name"?, "vendor"?, "model"?: "cmyk", "cmyk"?: [c, m, y, k] }
        if (e.ValueKind == JsonValueKind.String) return ParseHex(e.GetString()!, null);
        if (e.ValueKind != JsonValueKind.Object) throw new DsmFormatException("A color must be text or an object.");
        var color = ParseHex(e.Str("hex") ?? throw new DsmFormatException("A color needs 'hex'."), e.Str("name")) with { Vendor = e.Str("vendor") };
        var model = e.EnumOf("model", ColorModel.Rgb);
        var cmyk = e.Doubles("cmyk");
        if (cmyk.Length != 0 && cmyk.Length != 4) throw new DsmFormatException("A color's 'cmyk' needs 4 numbers.");
        return color with { Model = model, Cmyk = cmyk.Length == 4 ? new Cmyk(cmyk[0], cmyk[1], cmyk[2], cmyk[3]) : null };
    }

    private static DocColor ParseHex(string hex, string? name)
    {
        try { return DocColor.FromHex(hex, name); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new DsmFormatException($"'{hex}' is not a color."); }
    }
    public static PathGeometry ReadPath(JsonElement e)
    {
        var verbText = e.Str("verbs") ?? "";
        var coords = e.Doubles("points");
        if (coords.Length % 2 != 0) throw new DsmFormatException("A path has an odd number of coordinates.");
        var verbs = new PathVerb[verbText.Length];
        int need = 0;
        for (int i = 0; i < verbs.Length; i++)
        {
            (verbs[i], int n) = verbText[i] switch
            {
                'M' => (PathVerb.MoveTo, 1),
                'L' => (PathVerb.LineTo, 1),
                'C' => (PathVerb.CubicTo, 3),
                'A' => (PathVerb.ArcTo, 2),
                'Z' => (PathVerb.Close, 0),
                var c => throw new DsmFormatException($"Unknown path command '{c}'."),
            };
            need += n;
        }
        if (need != coords.Length / 2) throw new DsmFormatException("A path's commands and points do not match.");
        var points = new Vec2[need];
        for (int i = 0; i < need; i++) points[i] = new Vec2(coords[2 * i], coords[2 * i + 1]);
        var rule = e.EnumOf("fillRule", FillRule.NonZero);
        return new PathGeometry(verbs, points, rule);
    }

    // ---------------------------------------------------------------- writing

    public static string EnumName<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static void WriteEnum<T>(this Utf8JsonWriter w, string name, T value) where T : struct, Enum => w.WriteString(name, EnumName(value));

    /// <summary>A list of numbers on one line (indented JSON would otherwise put every number on its own line).</summary>
    public static void WriteNumbers(this Utf8JsonWriter w, string name, IReadOnlyList<double> values, int count = -1)
    {
        if (count < 0) count = values.Count;
        var sb = new StringBuilder(count * 8 + 2);
        sb.Append('[');
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendNumber(sb, values[i]);
        }
        sb.Append(']');
        w.WritePropertyName(name);
        w.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }

    private static void AppendNumber(StringBuilder sb, double v) => sb.Append(FormatNumber(v));

    /// <summary>
    /// The shortest text that parses back to exactly <paramref name="v"/>. (.NET's own "R" text is occasionally one ulp
    /// off for exact powers of two, e.g. 2^-25, so the text is checked and widened to 17 digits when needed.)
    /// </summary>
    public static string FormatNumber(double v)
    {
        if (!double.IsFinite(v)) throw new DsmFormatException("The document contains a number that is not finite (NaN or infinity), which cannot be saved.");
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return v == 0 && double.IsNegative(v) ? "-0" : ((long)v).ToString(CultureInfo.InvariantCulture);
        var s = v.ToString("R", CultureInfo.InvariantCulture);
        return double.Parse(s, CultureInfo.InvariantCulture) == v ? s : v.ToString("G17", CultureInfo.InvariantCulture);
    }

    /// <summary>Writes <c>"name": number</c> with <see cref="FormatNumber"/> (use this instead of WriteNumber for doubles).</summary>
    public static void WriteNum(this Utf8JsonWriter w, string name, double value)
    {
        w.WritePropertyName(name);
        w.WriteRawValue(FormatNumber(value), skipInputValidation: true);
    }
    public static void WriteTransform(this Utf8JsonWriter w, string name, Affine2 m) =>
        WriteNumbers(w, name, [m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY]);

    /// <summary>A color with everything that gives it identity (architecture D7): RGB+alpha as hex, vinyl name, vendor, model, CMYK components.</summary>
    public static void WriteColor(this Utf8JsonWriter w, string name, DocColor c)
    {
        w.WriteStartObject(name);
        w.WriteString("hex", c.ToHex());
        if (c.Name is { } n) w.WriteString("name", n);
        if (c.Vendor is { } v) w.WriteString("vendor", v);
        if (c.Model != ColorModel.Rgb) w.WriteEnum("model", c.Model);
        if (c.Cmyk is { } k) w.WriteNumbers("cmyk", [k.C, k.M, k.Y, k.K]);
        w.WriteEndObject();
    }
    public static void WritePath(this Utf8JsonWriter w, string name, PathGeometry g)
    {
        w.WriteStartObject(name);
        var verbs = new StringBuilder(g.Verbs.Count);
        foreach (var v in g.Verbs)
            verbs.Append(v switch
            {
                PathVerb.MoveTo => 'M', PathVerb.LineTo => 'L', PathVerb.CubicTo => 'C', PathVerb.ArcTo => 'A', _ => 'Z',
            });
        w.WriteString("verbs", verbs.ToString());
        if (g.FillRule != FillRule.NonZero) w.WriteEnum("fillRule", g.FillRule);
        var coords = new double[g.Points.Count * 2];
        for (int i = 0; i < g.Points.Count; i++) { coords[2 * i] = g.Points[i].X; coords[2 * i + 1] = g.Points[i].Y; }
        w.WriteNumbers("points", coords);
        w.WriteEndObject();
    }
}


