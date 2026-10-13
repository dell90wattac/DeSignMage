using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace DeSignMage.App.Shell;

/// <summary>
/// Makes a dialog window follow the interface text size (R12): its size is its design size (at 100%) times the scale, never more
/// than the screen's work area, and it re-sizes live when the scale changes. A dialog taller than the screen scrolls instead of being cut off.
/// </summary>
public static class DialogScale
{
    /// <summary>Largest share of the screen's work area a dialog may take.</summary>
    private const double ScreenShare = 0.96;

    /// <summary>
    /// Sizes <paramref name="window"/> from design sizes at 100%. With <paramref name="baseHeight"/> null the window sizes itself to
    /// its content in height (set <c>SizeToContent</c> yourself) but is still limited to the screen.
    /// </summary>
    public static void Attach(Window window, double baseWidth, double? baseHeight = null, double? minWidth = null, double? minHeight = null)
    {
        void Fit()
        {
            var (workW, workH) = WorkArea(window);
            double s = UiScale.Scale;
            window.Width = Math.Min(baseWidth * s, workW * ScreenShare);
            window.MaxHeight = workH * ScreenShare;
            window.MaxWidth = workW * ScreenShare;
            if (baseHeight is { } h) window.Height = Math.Min(h * s, workH * ScreenShare);
            if (minWidth is { } mw) window.MinWidth = Math.Min(mw * s, workW * ScreenShare);
            if (minHeight is { } mh) window.MinHeight = Math.Min(mh * s, workH * ScreenShare);
        }

        Fit();
        window.Opened += (_, _) => Fit();   // now the real screen is known
        void OnChanged() => Fit();
        UiScale.Changed += OnChanged;
        window.Closed += (_, _) => UiScale.Changed -= OnChanged;
    }

    /// <summary>The body of a simple dialog: scrolls vertically when it is taller than the window can be (large text on a small screen).</summary>
    public static Control Scrollable(Control body) => new ScrollViewer
    {
        Content = body,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>The screen's work area in device-independent pixels (a sensible default while the window has no screen yet).</summary>
    internal static (double Width, double Height) WorkArea(Window window)
    {
        try
        {
            var screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
            if (screen is not null && screen.Scaling > 0)
                return (screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
        return (1920, 1040);
    }
}
