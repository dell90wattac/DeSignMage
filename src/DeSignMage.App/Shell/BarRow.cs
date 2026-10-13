using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DeSignMage.App.Shell;

/// <summary>
/// A row of controls that flows from left to right and starts a new line only when the next control does not fit in the width it was
/// given (a narrow window or large interface text). Used for the Main Toolbar, the Context Bar and the Project Color Bar.
/// <para>
/// Controls with the style class <c>sep</c> are separators: they are never the first or last item of a line and never appear twice
/// in a row (also when the control between two separators is hidden), so a wrap does not leave a stray line at a line's edge.
/// Group controls that must stay together (a label and its field) in one panel.
/// </para>
/// </summary>
public class BarRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<BarRow, double>(nameof(Spacing), 0);

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<BarRow, double>(nameof(LineSpacing), 2);

    static BarRow() => AffectsMeasure<BarRow>(SpacingProperty, LineSpacingProperty);

    /// <summary>Gap between neighbours on a line.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Gap between lines when the row wraps.</summary>
    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>Kept for code written against a wrapping panel; the row always runs horizontally.</summary>
    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    private static bool IsSeparator(Control c) => c.Classes.Contains("sep");

    private readonly record struct Line(List<int> Items, double Width, double Height);

    /// <summary>Splits the visible children into lines for the given width (children must be measured).</summary>
    private List<Line> Flow(double width)
    {
        var lines = new List<Line>();
        var items = new List<int>();
        double x = 0, h = 0;
        bool lastWasSeparator = false;

        void Close()
        {
            // A separator never ends a line.
            while (items.Count > 0 && IsSeparator(Children[items[^1]]))
            {
                int i = items[^1];
                items.RemoveAt(items.Count - 1);
                x -= Children[i].DesiredSize.Width + (items.Count > 0 ? Spacing : 0);
            }
            if (items.Count > 0) lines.Add(new Line(items, Math.Max(0, x), h));
            items = [];
            x = h = 0;
            lastWasSeparator = false;
        }

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!child.IsVisible) continue;
            var size = child.DesiredSize;
            bool sep = IsSeparator(child);
            if (sep && (items.Count == 0 || lastWasSeparator)) continue;   // not at the start of a line, not twice in a row

            double need = size.Width + (items.Count > 0 ? Spacing : 0);
            if (!sep && items.Count > 0 && x + need > width + 0.5)
            {
                Close();
                need = size.Width;
            }
            items.Add(i);
            x += need;
            h = Math.Max(h, size.Height);
            lastWasSeparator = sep;
        }
        Close();
        return lines;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childLimit = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (var child in Children)
            if (child.IsVisible) child.Measure(childLimit);

        var lines = Flow(availableSize.Width);
        double width = 0, height = 0;
        foreach (var line in lines)
        {
            width = Math.Max(width, line.Width);
            height += line.Height;
        }
        if (lines.Count > 1) height += (lines.Count - 1) * LineSpacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var lines = Flow(finalSize.Width);
        var placed = new HashSet<int>();
        double used = lines.Sum(l => l.Height) + Math.Max(0, lines.Count - 1) * LineSpacing;
        double y = Math.Max(0, (finalSize.Height - used) / 2);   // a row given more height than its content centers it
        foreach (var line in lines)
        {
            double x = 0;
            foreach (int i in line.Items)
            {
                var child = Children[i];
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, y + (line.Height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Spacing;
                placed.Add(i);
            }
            y += line.Height + LineSpacing;
        }
        for (int i = 0; i < Children.Count; i++)
            if (!placed.Contains(i)) Children[i].Arrange(default);   // hidden, or a separator that would sit at a line's edge
        return finalSize;
    }
}

/// <summary>
/// The thin vertical divider between groups in a <see cref="BarRow"/> (style class <c>sep</c>). It has no width of its own while the row
/// hides it, so a hidden one leaves nothing behind (a plain border with a fixed width would still paint at the row's left edge).
/// </summary>
public sealed class BarSeparator : Control
{
    private static readonly Avalonia.Media.IPen LinePen = new Avalonia.Media.Pen(AppTheme.Brush("Ui.Separator"), 1);
    private static double FullWidth => 1 + 2 * UiScale.Sp(4);   // the air on either side grows with the spacing scale, not the text

    public BarSeparator()
    {
        Classes.Add("sep");
        IsHitTestVisible = false;
        this.ScaledHeight(18);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UiScale.Changed += Remeasure;
        AppTheme.Changed += Repaint;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UiScale.Changed -= Remeasure;
        AppTheme.Changed -= Repaint;
        base.OnDetachedFromVisualTree(e);
    }

    private void Remeasure() => InvalidateMeasure();
    private void Repaint() => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(FullWidth, 0);

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        if (Bounds.Width < FullWidth - 0.5) return;
        double x = Math.Floor(Bounds.Width / 2) + 0.5;
        context.DrawLine(LinePen, new Point(x, 0), new Point(x, Bounds.Height));
    }
}