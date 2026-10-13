using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Formats.Lyt;

internal sealed partial class LytWriteSession
{
    /// <summary>Node-space height of a capital letter: the layout normalises every text to 10 units from baseline to cap height.</summary>
    private const double CapHeightUnits = 10;

    private void AddText(TextObject t, Affine2 world, ObjectStyle style, int group, uint layerId)
    {
        var live = TryLiveText(t, world, style, layerId, out string reason);
        if (live is not null)
        {
            Add(live, group);
            result.LiveTextCount++;
            if (style.Stroke is { Width: > 0 } stroke) AddTextStroke(t, world, stroke, live.Name, group, layerId);
            return;
        }

        result.TextAsOutlinesCount++;
        Fallback(reason);
        string label = (string.IsNullOrEmpty(t.Name) ? t.Text : t.Name).Replace('\r', ' ').Replace('\n', ' ');
        if (label.Length > 100) label = label[..100];
        var outline = t.GetLocalGeometry();
        AddShape(outline, outline.GetBounds(), world, style, label, group, layerId);
    }

    private void AddTextStroke(TextObject t, Affine2 world, StrokeStyle stroke, string name, int group, uint layerId)
    {
        var geom = t.GetLocalGeometry().Transform(world);
        var ring = BuildRing(geom, stroke, world);
        if (ring is not null && ShapePart(ring, ring.GetBounds(), name + " (stroke)", StrokeColor(stroke), null, layerId) is { } part)
        {
            Add(part, group);
            result.StrokeShapeCount++;
        }
        else _strokesDropped++;
    }

    /// <summary>
    /// The record of a live TrueType text, or null with the reason it cannot be one. The record's name is the string; the nodes are, for every
    /// letter, a marker (x of its ink-left edge, lowest point, em height, ink width) followed by the letter's contours. Node space has the
    /// baseline at y = 0 and the capital height at 10, the first letter's ink-left edge at x = 0.
    /// </summary>
    private PartRecord? TryLiveText(TextObject t, Affine2 world, ObjectStyle style, uint layerId, out string reason)
    {
        reason = "";
        if (!options.LiveText) { reason = "live text was switched off"; return null; }
        if (style.Fill is not SolidPaint { Color.A: > 0 } fill) { reason = "its fill is not a plain color"; return null; }
        string text = t.Text, family = t.FontFamily.Trim();
        if (text.Length == 0 || text.Contains('\n') || text.Contains('\r')) { reason = "only single-line text can stay live"; return null; }
        if (t.FontWeight != 400 || t.Italic) { reason = "bold and italic text can not be named in the legacy font table"; return null; }
        if (family.Length == 0 || family.Length > 100 || !LytEncoding.CanEncode(family) || family.Contains('/') || family.Contains('\\'))
        { reason = "the font name can not be stored"; return null; }
        if (!LytEncoding.CanEncode(text)) { reason = "the text has characters outside the legacy character set"; return null; }
        if (!Decompose(world, out double a, out double b, out double rotation)) { reason = "it is skewed, mirrored or not an upright text"; return null; }

        var layout = t.Layout;
        var letters = layout.Letters;
        var elements = TextElements.Split(text);
        if (letters.Count != elements.Count || letters.Any(l => l.IsBreak)) { reason = "its letters could not be matched to the string"; return null; }

        double k = t.FontSize * TextEngine.CapHeightEm(family, 400, false) / CapHeightUnits;   // points per node unit
        if (!(k > 1e-9) || !double.IsFinite(k)) { reason = "the font size is not usable"; return null; }

        bool firstInked = !letters[0].Outline.IsEmpty;
        double xref = firstInked ? letters[0].Outline.GetBounds().Left : letters[0].X;
        Vec2 ToNode(Vec2 p) => new((p.X - xref) / k, -p.Y / k);

        var part = new PartRecord
        {
            Kind = PartKind.Text, Name = text, FontFile = $"_{family}.ptt", Align = 0, CharSpacing = (float)t.Tracking,
            Color = LytColors.From(fill.Color with { A = 255 }), LayerId = layerId, Type = 0x4011, Type2 = 0x4100,
        };
        foreach (var letter in letters)
        {
            bool inked = !letter.Outline.IsEmpty;
            var rect = inked ? letter.Outline.GetBounds() : default;
            double markerX = ((inked ? rect.Left : letter.X) - xref) / k;
            double bottom = inked ? -rect.Bottom / k : 0;
            double width = (inked ? rect.Width : letter.Advance) / k;
            part.Nodes.Add(OutNode.Marker((float)markerX, (float)bottom, (float)CapHeightUnits, width));
            if (inked) part.Nodes.AddRange(LytNodeBuilder.Build(letter.Outline, ToNode, closeOpen: true, markGlyph: true));
        }
        if (part.Nodes.Count(n => !n.IsMarker) == 0) { reason = "it has no visible letters"; return null; }

        var ink = layout.Outline.GetBounds();
        part.ActualLeft = (ink.Left - xref) / k; part.ActualRight = (ink.Right - xref) / k;
        part.ActualTop = -ink.Top / k; part.ActualBottom = -ink.Bottom / k;
        part.SizeW = part.ActualRight - part.ActualLeft; part.SizeH = CapHeightUnits;

        // Where the node origin (first letter's ink-left edge, baseline) lands on the page, and how the node units are scaled and turned.
        var origin = world.Transform(new Vec2(xref, 0));
        (part.OrgX, part.OrgY) = Units.ToUser(origin, Page);
        part.Scale = k * b / Units.PointsPerInch;
        part.XScale = a / b;
        part.Rotation = rotation;
        SetPageBounds(part, t.GetWorldBounds());
        return part;
    }
}
