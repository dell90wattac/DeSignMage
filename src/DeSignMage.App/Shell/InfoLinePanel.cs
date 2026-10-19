using Avalonia;
using Avalonia.Controls;

namespace DeSignMage.App.Shell;

/// <summary>
/// The layout of the bottom bar (R38): one line, never two. Its children keep their order from left to right (a child marked
/// <see cref="RightProperty"/> goes to the right end instead), and when they do not all fit in the width of the window, the ones that matter least
/// give way first, in the order of their <see cref="DropOrderProperty"/> (the highest number goes first; 0 is never dropped). The project colors, Fill, Stroke and the measurement fields are
/// always whole. Only when a window is too narrow even for those (very large interface text) the child marked <see cref="WrapsProperty"/> (the Project
/// Color Bar, a <see cref="BarRow"/>) takes what room is left and wraps onto a second line, so the bar grows taller instead of cutting anything off.
/// <para>
/// A <see cref="Flash"/> (a short message such as "Saved x.dsm") takes the room of every child that can give way for as long as it is shown, and is
/// trimmed with an ellipsis if it is longer than that room.
/// </para>
/// </summary>
public sealed class InfoLinePanel : Panel
{
    public static readonly AttachedProperty<int> DropOrderProperty = AvaloniaProperty.RegisterAttached<InfoLinePanel, Control, int>("DropOrder");
    public static readonly AttachedProperty<bool> RightProperty = AvaloniaProperty.RegisterAttached<InfoLinePanel, Control, bool>("Right");
    public static readonly AttachedProperty<bool> WrapsProperty = AvaloniaProperty.RegisterAttached<InfoLinePanel, Control, bool>("Wraps");
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<InfoLinePanel, double>(nameof(Spacing), 8);

    static InfoLinePanel() => AffectsMeasure<InfoLinePanel>(SpacingProperty);

    public static int GetDropOrder(Control c) => c.GetValue(DropOrderProperty);
    public static void SetDropOrder(Control c, int value) => c.SetValue(DropOrderProperty, value);
    public static bool GetRight(Control c) => c.GetValue(RightProperty);
    public static void SetRight(Control c, bool value) => c.SetValue(RightProperty, value);
    public static bool GetWraps(Control c) => c.GetValue(WrapsProperty);
    public static void SetWraps(Control c, bool value) => c.SetValue(WrapsProperty, value);

    /// <summary>The gap between neighbours.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The message line: a child that is normally hidden. While it is visible it gets the room left by the children that can not give way.</summary>
    public Control? Flash { get; set; }

    private readonly HashSet<Control> _given = [];   // the children that gave way in the last measure

    /// <summary>True when the child gave way for lack of room (it takes no space and shows nowhere).</summary>
    public bool IsGivenWay(Control child) => _given.Contains(child);

    private double Need(IEnumerable<Control> shown)
    {
        double total = 0;
        int n = 0;
        foreach (var c in shown)
        {
            total += c.DesiredSize.Width;
            n++;
        }
        return total + Math.Max(0, n - 1) * Spacing;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _given.Clear();
        double height = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        double room = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        var shown = Children.Where(c => c.IsVisible && !ReferenceEquals(c, Flash)).ToList();
        bool flashing = Flash is { IsVisible: true };

        if (flashing)
        {
            foreach (var c in shown.Where(c => GetDropOrder(c) > 0).ToList()) { shown.Remove(c); _given.Add(c); }
        }
        else
        {
            while (Need(shown) > room + 0.5)
            {
                var next = shown.Where(c => GetDropOrder(c) > 0).OrderByDescending(GetDropOrder).FirstOrDefault();
                if (next is null) break;
                shown.Remove(next);
                _given.Add(next);
            }
        }

        // Still too wide with nothing left to give up: the wrapping child takes the room that is left and goes onto more lines.
        if (!flashing && Need(shown) > room + 0.5 && shown.FirstOrDefault(GetWraps) is { } wrapper)
        {
            double others = Need(shown.Where(c => !ReferenceEquals(c, wrapper))) + Spacing;
            wrapper.Measure(new Size(Math.Max(40, room - others), double.PositiveInfinity));
        }
        height = shown.Count == 0 ? 0 : shown.Max(c => c.DesiredSize.Height);

        if (flashing && Flash is { } flash)
        {
            double left = Math.Max(0, room - Need(shown) - Spacing);
            flash.Measure(new Size(double.IsInfinity(left) ? double.PositiveInfinity : left, availableSize.Height));
            height = Math.Max(height, flash.DesiredSize.Height);
            return new Size(Need(shown) + Spacing + flash.DesiredSize.Width, height);
        }
        return new Size(Need(shown), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var flash = Flash is { IsVisible: true } f ? f : null;
        var left = Children.Where(c => c.IsVisible && !_given.Contains(c) && !ReferenceEquals(c, flash) && !GetRight(c)).ToList();
        var right = Children.Where(c => c.IsVisible && !_given.Contains(c) && !ReferenceEquals(c, flash) && GetRight(c)).ToList();

        double x = 0;
        foreach (var c in left)
        {
            c.Arrange(new Rect(x, (finalSize.Height - c.DesiredSize.Height) / 2, c.DesiredSize.Width, c.DesiredSize.Height));
            x += c.DesiredSize.Width + Spacing;
        }
        if (flash is not null)
        {
            double w = Math.Max(0, Math.Min(flash.DesiredSize.Width, finalSize.Width - x));
            flash.Arrange(new Rect(x, (finalSize.Height - flash.DesiredSize.Height) / 2, w, flash.DesiredSize.Height));
        }
        double rx = finalSize.Width;
        for (int i = right.Count - 1; i >= 0; i--)
        {
            var c = right[i];
            rx -= c.DesiredSize.Width;
            c.Arrange(new Rect(rx, (finalSize.Height - c.DesiredSize.Height) / 2, c.DesiredSize.Width, c.DesiredSize.Height));
            rx -= Spacing;
        }
        // A child that gave way is laid out far outside the bar (the bar clips), so none of its drawing can show.
        foreach (var c in Children)
            if (_given.Contains(c)) c.Arrange(new Rect(-100000, 0, 0, 0));
            else if (!c.IsVisible) c.Arrange(default);
        return finalSize;
    }
}
