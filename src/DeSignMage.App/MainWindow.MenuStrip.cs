using Avalonia;
using Avalonia.Controls;
using DeSignMage.App.Shell;

namespace DeSignMage.App;

/// <summary>
/// The menu tabs in the top row (R25). The tabs and strips are built from <see cref="MenuLayout"/>, so they list what the Image, Effects and
/// Tools menus list. The program starts with every strip closed. An open strip drops down over the top of the canvas, under its tab, so
/// nothing else on the window moves; picking a command, clicking the tab again or pressing on the canvas closes it.
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
        OverlayLayer.Children.Add(MenuStrip.StripPanel);
        MenuStrip.Changed += (_, _) => PlaceMenuStrip();
        SizeChanged += (_, _) => PlaceMenuStrip();
        UiScale.Changed += PlaceMenuStrip;
        AppTheme.Changed += MenuStrip.Refresh;   // a switch changed from the menu or its own button: the strip button follows (pressed or not)
        Closed += (_, _) => { UiScale.Changed -= PlaceMenuStrip; AppTheme.Changed -= MenuStrip.Refresh; };
        // Pressing on the canvas closes an open strip (the press still does its own work there).
        Surface.AddHandler(PointerPressedEvent, (_, _) => MenuStrip.Close(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The menu name of the strip that is open, or null (tests).</summary>
    internal string? OpenMenuStrip => MenuStrip.Open;

    /// <summary>Widest an open strip gets at 100% interface text; its buttons wrap onto more lines inside it.</summary>
    internal const double MenuStripWidth = 560;

    /// <summary>
    /// Puts the open strip right under the top row, its left edge under the open tab, moved left as far as needed to stay inside the window.
    /// </summary>
    private void PlaceMenuStrip()
    {
        var panel = MenuStrip.StripPanel;
        if (MenuStrip.Open is not { } open || Bounds.Width <= 0) return;
        double margin = UiScale.Sp(6);
        double width = Math.Min(MenuStripWidth * UiScale.Scale, Math.Max(200, Bounds.Width - 2 * margin));
        panel.Width = width;
        panel.MaxHeight = Math.Max(120, Bounds.Height * 0.6);
        var tab = MenuStrip.Tab(open);
        double left = tab.TranslatePoint(new Point(0, 0), this)?.X ?? margin;
        left = Math.Max(margin, Math.Min(left, Bounds.Width - width - margin));
        double top = TopRowBorder.TranslatePoint(new Point(0, TopRowBorder.Bounds.Height), this)?.Y ?? TopRowBorder.Bounds.Height;
        Avalonia.Controls.Canvas.SetLeft(panel, left);
        Avalonia.Controls.Canvas.SetTop(panel, top + 2);
    }
}