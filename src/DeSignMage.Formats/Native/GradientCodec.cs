using System.Text.Json;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>
/// The JSON form of a <see cref="GradientPaint"/>, shared by the document file (<see cref="GradientPaintCodec"/>) and the saved gradient
/// presets: <c>{ "kind", "points": [x0, y0, x1, y1, ...] (relative to the object's bounds), "stops": [ { "offset", "color", "highlight"? } ],
/// "alternate"?: true, "reflect"?: true }</c>. Stop colors keep their name, vendor and model like every other color.
/// </summary>
public static class GradientJson
{
    public static void WriteFields(Utf8JsonWriter w, GradientPaint g)
    {
        w.WriteEnum("kind", g.Kind);
        var coords = new double[g.Points.Count * 2];
        for (int i = 0; i < g.Points.Count; i++) { coords[2 * i] = g.Points[i].X; coords[2 * i + 1] = g.Points[i].Y; }
        w.WriteNumbers("points", coords);
        w.WriteStartArray("stops");
        foreach (var s in g.Stops)
        {
            w.WriteStartObject();
            w.WriteNum("offset", s.Offset);
            w.WriteColor("color", s.Color);
            if (s.Highlight) w.WriteBoolean("highlight", true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (g.AlternateColors) w.WriteBoolean("alternate", true);
        if (g.Reflect) w.WriteBoolean("reflect", true);
    }

    /// <summary>Reads the fields written by <see cref="WriteFields"/>; throws <see cref="DsmFormatException"/> for a gradient that cannot be used.</summary>
    public static GradientPaint Read(JsonElement e, DsmReadContext? ctx = null)
    {
        var kind = e.EnumOf("kind", GradientKind.Linear, ctx);
        var coords = e.Doubles("points");
        int need = GradientPaint.PointCount(kind);
        Vec2[] points;
        if (coords.Length == need * 2)
        {
            points = new Vec2[need];
            for (int i = 0; i < need; i++) points[i] = new Vec2(coords[2 * i], coords[2 * i + 1]);
        }
        else
        {
            ctx?.Warn($"A {kind} gradient had {coords.Length / 2} control points instead of {need}; its default ones are used.");
            points = GradientPaint.DefaultPoints(kind);
        }

        var stops = new List<GradientNode>();
        foreach (var s in e.Items("stops"))
        {
            if (stops.Count >= 256) break;   // far beyond anything the tool makes
            var color = DsmJson.ReadColor(s.TryGet("color", out var c) ? c : throw new DsmFormatException("A gradient stop needs a 'color'."));
            stops.Add(new GradientNode(s.Num("offset"), color, s.Bool("highlight")));
        }
        if (stops.Count < GradientPaint.MinStops) throw new DsmFormatException("A gradient needs at least two colors.");
        return new GradientPaint(kind, points, stops, e.Bool("alternate"), e.Bool("reflect"));
    }
}

/// <summary>A gradient fill (<c>"type": "gradient"</c>).</summary>
internal sealed class GradientPaintCodec : PaintCodec<GradientPaint>
{
    public override string Type => "gradient";
    protected override void WriteFields(GradientPaint paint, DsmWriteContext ctx) => GradientJson.WriteFields(ctx.Json, paint);
    public override Paint Read(JsonElement e, DsmReadContext ctx) => GradientJson.Read(e, ctx);
}
