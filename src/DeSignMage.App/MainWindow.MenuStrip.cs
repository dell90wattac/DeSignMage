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
        MenuStrip.Changed += (_, _) => FitMenuStrip();
        ContextBarBorder.SizeChanged += (_, _) => FitMenuStrip();
        UiScale.Changed += FitMenuStrip;
        Closed += (_, _) => UiScale.Changed -= FitMenuStrip;
        FitMenuStrip();
    }

    /// <summary>The menu name of the strip that is open, or null (tests).</summary>
    internal string? OpenMenuStrip => MenuStrip.Open;

    /// <summary>
    /// Closed, the right part is only as wide as its tabs; open, it takes the right half of the Context Bar (the tool controls keep the left
    /// half, and scroll sideways if they need more), and never less than its own tabs.
    /// </summary>
    private void FitMenuStrip()
    {
        if (MenuStrip.Open is null)
        {
            MenuStrip.Width = double.NaN;
            return;
        }
        double inner = ContextBarBorder.Bounds.Width - ContextBarBorder.Padding.Left - ContextBarBorder.Padding.Right;
        MenuStrip.Tabs.Measure(Size.Infinity);
        double tabs = MenuStrip.Tabs.DesiredSize.Width;
        MenuStrip.Width = Math.Max(tabs, inner / 2 - MenuStrip.Margin.Left);
    }
}
