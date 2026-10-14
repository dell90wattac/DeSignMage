using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Vector;

internal static class VectorText
{
    /// <summary>The outlines of a text as a path object (what live text becomes when it is switched off).</summary>
    public static DocObject ToOutlines(TextObject t)
    {
        var outline = t.GetLocalGeometry();
        return new PathObject(outline.Transform(t.Transform)) { Name = t.Text.Length > 40 ? t.Text[..40] : t.Text, Style = t.Style };
    }
}
