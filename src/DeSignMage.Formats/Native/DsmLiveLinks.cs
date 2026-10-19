using System.Text.Json;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>
/// Live effects in a <c>.dsm</c> file (format 4). The source of a live effect carries <c>"linkId": n</c>; the effect object carries
/// <c>"live": { "source": n, "effect": "...", settings..., "frame": [6 numbers] }</c>. A reader that does not know <c>live</c> (formats 1 to 3)
/// opens the effect as the plain object it also is, so nothing is lost on screen.
/// </summary>
internal static class DsmLiveLinks
{
    public static void Write(Utf8JsonWriter json, LiveLink link, int sourceId)
    {
        json.WriteStartObject("live");
        json.WriteNumber("source", sourceId);
        switch (link.Spec)
        {
            case LiveOutline o:
                json.WriteString("effect", o.Kind == OutlineKind.Inline ? "inline" : "outline");
                var s = o.Settings;
                json.WriteNum("first", s.First);
                if (s.Second != 0) json.WriteNum("second", s.Second);
                if (s.Corners != CornerStyle.Auto) json.WriteEnum("corners", s.Corners);
                if (s.Oat != 70) json.WriteNum("oat", s.Oat);
                if (s.ContourOnly) json.WriteBoolean("contour", true);
                break;

            case LiveShadow sh:
                json.WriteString("effect", "shadow");
                var t = sh.Settings;
                json.WriteEnum("form", t.Form);
                if (t.Type != ShadowType.Drop) json.WriteEnum("type", t.Type);
                if (t.Outline != 0) json.WriteNum("outline", t.Outline);
                json.WriteNum("shiftX", t.ShiftX);
                json.WriteNum("shiftY", t.ShiftY);
                if (t.DepthPercent != 75) json.WriteNum("depth", t.DepthPercent);
                if (t.Corners != CornerStyle.Auto) json.WriteEnum("corners", t.Corners);
                if (t.Oat != 70) json.WriteNum("oat", t.Oat);
                if (t.Color is { } c) json.WriteColor("color", c);
                else json.WriteBoolean("sameAsObject", true);
                if (t.Pivot is { } p) json.WriteNumbers("pivot", [p.X, p.Y]);
                break;

            case LiveSoft soft:
                json.WriteString("effect", soft.Kind == SoftKind.Glow ? "glow" : "softShadow");   // the settings are the object's blur style
                break;

            case LiveBevel bv:
                json.WriteString("effect", "bevel");
                json.WriteString("part", bv.Part == BevelPart.Shadow ? "shadow" : "highlight");
                var b = bv.Settings;
                json.WriteNum("inset", b.Inset);
                json.WriteNum("width", b.Width);
                json.WriteNum("softness", b.Softness);
                json.WriteNum("highlight", b.Highlight);
                json.WriteNum("shadow", b.Shadow);
                if (!b.IncludeShadow) json.WriteBoolean("includeShadow", false);
                if (b.Neutral) json.WriteBoolean("neutral", true);
                if (b.LightAngle != BevelSettings.DefaultLightAngle) json.WriteNum("light", b.LightAngle);
                json.WriteColor("highlightColor", b.HighlightColor);
                json.WriteColor("shadowColor", b.ShadowColor);
                break;
        }
        if (link.Frame is { } f) json.WriteTransform("frame", f);
        if (link.SourceFrame is { } sf) json.WriteTransform("sourceFrame", sf);   // only after the effect object was moved by itself
        json.WriteEndObject();
    }

    /// <summary>The link an effect object had; null for an outline, inline or shadow (they are plain now), and null with a warning for a kind unknown to this version.</summary>
    public static LiveLink? Read(JsonElement e, DocObject source, DsmReadContext ctx)
    {
        LiveEffectSpec? spec;
        switch (e.Str("effect"))
        {
            // Outlines, inlines and shadows are plain objects again (user, 2026-10-18: "vector based objects stay, effects don't"). Files saved while they
            // were live carry a link for them: it is dropped quietly, the object itself stays as it was saved.
            case "outline" or "inline" or "shadow":
                return null;

            case "softShadow": spec = new LiveSoft(SoftKind.SoftShadow); break;
            case "glow": spec = new LiveSoft(SoftKind.Glow); break;

            case "bevel":
                var d = new BevelSettings();
                spec = new LiveBevel(new BevelSettings
                {
                    Inset = e.Num("inset", d.Inset),
                    Width = e.Num("width", d.Width),
                    Softness = e.Num("softness", d.Softness),
                    Highlight = e.Num("highlight", d.Highlight),
                    Shadow = e.Num("shadow", d.Shadow),
                    IncludeShadow = e.Bool("includeShadow", true),
                    Neutral = e.Bool("neutral"),
                    LightAngle = e.Num("light", BevelSettings.DefaultLightAngle),
                    HighlightColor = e.TryGet("highlightColor", out var hc) ? DsmJson.ReadColor(hc) : d.HighlightColor,
                    ShadowColor = e.TryGet("shadowColor", out var sc) ? DsmJson.ReadColor(sc) : d.ShadowColor,
                }, e.Str("part") == "shadow" ? BevelPart.Shadow : BevelPart.Highlight);
                break;

            default:
                ctx.Warn($"A live effect of unknown kind '{e.Str("effect")}' was opened as a plain object (saved by a newer version?).");
                return null;
        }
        Affine2? frame = e.Has("frame") ? e.ReadTransform("frame") : null;
        Affine2? sourceFrame = e.Has("sourceFrame") ? e.ReadTransform("sourceFrame") : null;
        return new LiveLink(source, spec, frame is { IsIdentity: false } ? frame : null, sourceFrame);
    }
}
