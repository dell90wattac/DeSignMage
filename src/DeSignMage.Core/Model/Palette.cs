namespace DeSignMage.Core.Model;

/// <summary>
/// A named list of colors (a vinyl line such as "Oracal 651", or "Designer"). Palettes live outside the document;
/// the document keeps the colors it uses. Each color keeps its name, vendor/group and model (architecture D7).
/// </summary>
public sealed class Palette(string name, IReadOnlyList<DocColor> colors)
{
    public string Name { get; } = name;
    public IReadOnlyList<DocColor> Colors { get; } = colors;

    /// <summary>Colors whose display name or vendor contains <paramref name="text"/> (case-insensitive).</summary>
    public IEnumerable<DocColor> Search(string text) =>
        Colors.Where(c => (c.DisplayName ?? "").Contains(text, StringComparison.OrdinalIgnoreCase) ||
                          (c.Vendor ?? "").Contains(text, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name} ({Colors.Count})";
}
