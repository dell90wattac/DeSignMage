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
    /// <summary>
    /// The Tools strip mirrors the Toolbox (R29): the same tools in the same order with the same icons, then what the Tools menu has besides
    /// (Loop Edit, Show Transparency, Options). The other menus' strips list exactly what their menus list. The drop-down menus are not changed.
    /// </summary>
    private IEnumerable<MenuNode> StripMenus() => MenuLayout.Select(m => m.Header == "_Tools" ? ToolsStripNode(m) : m);

    /// <summary>The command id of a Toolbox tool (<c>select</c> is "tool.select", <c>vectordraw</c> is "tool.vectorDraw").</summary>
    internal string CommandOfTool(string toolId) =>
        _commands.All.FirstOrDefault(c => c.Id.Equals("tool." + toolId, StringComparison.OrdinalIgnoreCase))?.Id
        ?? throw new InvalidOperationException($"The Toolbox has a tool '{toolId}' with no command to run it.");

    private MenuNode ToolsStripNode(MenuNode tools)
    {
        var fromToolbox = Toolbox.Buttons.Select(b => CommandOfTool(b.ToolId)).ToList();
        var children = new List<object>(fromToolbox);
        children.Add("-");
        // what the Tools menu has besides the Toolbox's tools, in the menu's own order
        foreach (var node in tools.Children ?? [])
        {
            if (node.IsSeparator) { children.Add("-"); continue; }
            if (node.CommandId is { } id && !fromToolbox.Contains(id)) children.Add(id);
        }
        return MenuNode.Menu(tools.Header, [.. children]);
    }

    /// <summary>Tool buttons of the strip look pressed while their tool is the active one, as the Toolbox's buttons do.</summary>
    private bool? ToolButtonPressed(AppCommand c) =>
        c.Id.StartsWith("tool.", StringComparison.Ordinal) ? _session.ActiveTool.Id.Equals(c.Id["tool.".Length..], StringComparison.OrdinalIgnoreCase) : null;

    private void InitMenuStrip()
    {
        MenuStrip.Pressed = ToolButtonPressed;
        MenuStrip.Populate(StripMenus(), _commands);
        ContextBarBorder.SizeChanged += (_, _) => FitMenuStrip();
        UiScale.Changed += FitMenuStrip;
        AppTheme.Changed += MenuStrip.Refresh;   // a switch changed from the menu or its own button: the strip button follows (pressed or not)
        Closed += (_, _) => { UiScale.Changed -= FitMenuStrip; AppTheme.Changed -= MenuStrip.Refresh; };
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