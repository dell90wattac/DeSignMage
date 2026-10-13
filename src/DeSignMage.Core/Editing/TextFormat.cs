using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

/// <summary>The look given to new text objects; the text Context Bar edits it when no text is selected.</summary>
public sealed class TextFormat
{
    public string Family { get; set; } = "Arial";
    public int Weight { get; set; } = 400;
    public bool Italic { get; set; }
    /// <summary>Points. Half an inch by default.</summary>
    public double Size { get; set; } = Units.FromInches(0.5);
    /// <summary>Ems.</summary>
    public double Tracking { get; set; }
    public TextAlignment Alignment { get; set; }
    public ObjectStyle Style { get; set; } = ObjectStyle.FillOnly(DocColor.Black);

    public void Apply(TextFormatChange c)
    {
        if (c.Family is not null) Family = c.Family;
        if (c.Weight is { } w) Weight = w;
        if (c.Italic is { } i) Italic = i;
        if (c.Size is { } s) Size = s;
        if (c.Tracking is { } k) Tracking = k;
        if (c.Alignment is { } a) Alignment = a;
    }
}
