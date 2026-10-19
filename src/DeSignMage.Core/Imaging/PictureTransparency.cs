using System.Collections;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Imaging;

/// <summary>An immutable list that compares by its contents, so records that hold one (recipes) compare by value.</summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>> where T : IEquatable<T>
{
    public static readonly ValueList<T> Empty = new([]);

    private readonly T[] _items;

    public ValueList(IEnumerable<T> items) => _items = items.ToArray();

    public int Count => _items.Length;
    public T this[int index] => _items[index];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public ValueList<T> Add(T item) => new(_items.Append(item));
    public ValueList<T> RemoveAt(int index) => new(_items.Where((_, i) => i != index));
    public ValueList<T> Without(T item) => new(_items.Where(i => !i.Equals(item)));

    public bool Equals(ValueList<T>? other) => other is not null && (ReferenceEquals(this, other) || _items.AsSpan().SequenceEqual(other._items));
    public override bool Equals(object? obj) => obj is ValueList<T> o && Equals(o);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var i in _items) h.Add(i);
        return h.ToHashCode();
    }
}

/// <summary>
/// One cut-out outline drawn with the lasso or the polygon lasso: a closed polygon in pixels of the <b>original</b> picture (so it stays where it was
/// drawn when the picture is turned or cropped). <see cref="Keep"/>: only what is inside stays; otherwise what is inside is removed.
/// </summary>
public sealed record CutShape(ValueList<Vec2> Points, bool Keep) : IEquatable<CutShape>
{
    public bool IsUsable => Points.Count >= 3;
}

/// <summary>
/// The Transparency tab of the Picture Editor: what is made see-through, applied after the crop and before the colors. The pieces:
/// <list type="bullet">
/// <item><b>Colors</b> (clicked in the picture or found by "remove background"): every pixel within <see cref="Tolerance"/> of one of them becomes transparent;
/// with <see cref="EdgeOnly"/> only the part that touches the edge of the picture does, so the same color inside the picture stays.</item>
/// <item><b>Cuts</b> (lasso, polygon lasso): the kept or removed area of closed outlines. All "keep" outlines together are what remains; all "remove" outlines are cut out of it.</item>
/// <item><see cref="Shrink"/> eats the transparent area's border inward (pixels of the original) to lose a light fringe; <see cref="Softness"/> blurs the border;
/// <see cref="Opacity"/> makes the whole picture see-through.</item>
/// </list>
/// Sizes are pixels of the original picture; the preview scales them.
/// </summary>
public sealed record PictureTransparency
{
    public static readonly PictureTransparency None = new();

    /// <summary>Colors that are made transparent, as 0xRRGGBB.</summary>
    public ValueList<int> Colors { get; init; } = ValueList<int>.Empty;

    /// <summary>How far from a chosen color a pixel may be and still go: 0 (exactly that color) to 100 (everything), in percent of the distance from black to white.</summary>
    public int Tolerance { get; init; } = DefaultTolerance;

    /// <summary>Remove a color only where it touches the picture's edge (a plain background), not where it also occurs inside the picture.</summary>
    public bool EdgeOnly { get; init; }

    public ValueList<CutShape> Cuts { get; init; } = ValueList<CutShape>.Empty;

    /// <summary>Pixels the border of the cut-out is moved inward, 0..10.</summary>
    public int Shrink { get; init; }

    /// <summary>Pixels over which the border fades, 0..50.</summary>
    public int Softness { get; init; }

    /// <summary>Overall picture opacity in percent, 1..100 (100 = not changed).</summary>
    public int Opacity { get; init; } = 100;

    public const int DefaultTolerance = 12;
    public const int MaxShrink = 10;
    public const int MaxSoftness = 50;

    public bool HasColors => Colors.Count > 0;
    public bool HasCuts => Cuts.Any(c => c.IsUsable);

    /// <summary>True when nothing is made transparent (settings that only shape an edge do nothing without something to cut).</summary>
    public bool IsIdentity => !HasColors && !HasCuts && Opacity >= 100;

    public PictureTransparency Clamped() => this with
    {
        Tolerance = Math.Clamp(Tolerance, 0, 100),
        Shrink = Math.Clamp(Shrink, 0, MaxShrink),
        Softness = Math.Clamp(Softness, 0, MaxSoftness),
        Opacity = Math.Clamp(Opacity, 1, 100),
    };

    /// <summary>The same settings with the cut outlines moved and scaled (used when the original is replaced by one of another size).</summary>
    public PictureTransparency ScaledCuts(double kx, double ky) => Cuts.Count == 0 ? this : this with
    {
        Cuts = new ValueList<CutShape>(Cuts.Select(c => c with { Points = new ValueList<Vec2>(c.Points.Select(p => new Vec2(p.X * kx, p.Y * ky))) })),
    };

    /// <summary>Color as 0xRRGGBB.</summary>
    public static int Pack(byte r, byte g, byte b) => (r << 16) | (g << 8) | b;
}

/// <summary>
/// A coarse picture of which parts of a picture are visible, used to decide what a click hits: the transparent area outside a cut-out must not select the picture.
/// <see cref="Alpha"/> is Width x Height bytes covering the picture's frame (row 0 at the top).
/// </summary>
public sealed class AlphaMask(int width, int height, byte[] alpha)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Alpha { get; } = alpha;

    /// <summary>Alpha that still counts as part of the picture when clicking (a nearly clear pixel does not).</summary>
    public const int VisibleAlpha = 13;

    /// <summary>
    /// True when the picture is visible at (<paramref name="u"/>, <paramref name="v"/>) given as fractions of the frame (0..1), or within
    /// <paramref name="radiusU"/> x <paramref name="radiusV"/> of it (the click tolerance, as fractions too).
    /// </summary>
    public bool IsVisibleNear(double u, double v, double radiusU, double radiusV)
    {
        int x0 = (int)Math.Floor((u - radiusU) * Width), x1 = (int)Math.Floor((u + radiusU) * Width);
        int y0 = (int)Math.Floor((v - radiusV) * Height), y1 = (int)Math.Floor((v + radiusV) * Height);
        if (x1 < 0 || y1 < 0 || x0 >= Width || y0 >= Height) return false;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (Alpha[y * Width + x] >= VisibleAlpha) return true;
        return false;
    }
}
