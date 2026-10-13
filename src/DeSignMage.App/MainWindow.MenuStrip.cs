using Avalonia;
using Avalonia.Controls;
using DeSignMage.App.Shell;

namespace DeSignMage.App;

/// <summary>
/// The menu tabs in the right half of the Context Bar (R25). The tabs and strips are built from <see cref="MenuLayout"/>, so they list what
/// the Image, Effects and Tools menus list. The program starts with every strip closed. Only the strip's width changes when one opens (the
/// bar's height is fixed), so nothing else on the window moves.
/// </summary>
public partial class MainWindow
{
    private void InitMenuStrip()
    {
        MenuStrip.Populate(MenuLayout, _commands);
        ContextBarBorder.SizeChanged += (_, _) => FitMenuStrip();
        UiScale.Changed += FitMenuStrip;
        Closed += (_, _) => UiScale.Changed -= FitMenuStrip;
        FitMenuStrip();
    }

    /// <summary>The menu name of the strip that is open, or null (tests).</summary>
    internal string? OpenMenuStrip => MenuStrip.Open;

    /// <summary>The share of the Context Bar's width the menu area always has (the tool controls keep the rest); the mockup split the bar at about 62%.</summary>
    internal const double MenuStripShare = 0.38;

    /// <summary>
    /// The menu area has the same width whether a strip is open, closed or swapped, and never less than its own tabs: the divider and the left-aligned
    /// tabs stay where they are, and only the panel under them comes and goes. A longer strip wraps onto a second line inside the panel.
    /// </summary>
    private void FitMenuStrip()
    {
        var pad = ContextBarBorder.Padding;
        MenuStrip.FitTo(pad);
        MenuStrip.Tabs.Measure(Size.Infinity);
        double tabs = MenuStrip.Tabs.DesiredSize.Width + MenuStrip.Tabs.Margin.Left + MenuStrip.Tabs.Margin.Right;
        // the area reaches out through the bar's right padding, which its own width must make up for
        MenuStrip.Width = Math.Max(tabs, ContextBarBorder.Bounds.Width * MenuStripShare) + pad.Right;
    }
}