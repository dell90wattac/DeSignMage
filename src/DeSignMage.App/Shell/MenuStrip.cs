using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeSignMage.App.Shell;

/// <summary>
/// The menu tabs (requirement R25): a tab for each of a few menus (Image, Effects, Tools), in the top row beside the Main Toolbar. A click
/// on a tab drops a strip of buttons, one per command of that menu, down over the top of the canvas; a click on the open tab, on one of
/// its buttons or on the canvas closes it. One strip is open at a time (<see cref="DockLayout"/> decides, as for the Dock), and the program
/// starts with none open. The normal drop-down menus stay. A button runs the very command its menu item runs and is enabled when the item
/// is. The strip floats over the canvas (the window places <see cref="StripPanel"/>), so opening or closing it never moves anything.
/// </summary>
public sealed class MenuStrip : Grid
{
    /// <summary>Size of a button label at 100% interface text (compact, but readable at a glance).</summary>
    public const double LabelSize = 11;

    /// <summary>Size of the small icon beside the label at 100% (it scales with the icon scale).</summary>
    public const double IconSize = 12;

    /// <summary>Menus that get a tab, by their header in the menu bar (with the mnemonic underscore).</summary>
    public static readonly string[] TabbedMenus = ["_Image", "Effe_cts", "_Tools"];

    /// <summary>Commands that stay out of the strips: development helpers, and placeholders for things that are not built yet.</summary>
    private static bool Skipped(AppCommand c) => c.IsPlaceholder || c.Id.StartsWith("debug.", StringComparison.Ordinal);

    private sealed record Entry(AppCommand? Command, Button? Button);   // a null command is a divider

    private readonly Dictionary<string, ToggleButton> _tabs = [];
    private readonly Dictionary<string, List<Entry>> _entries = [];
    private readonly Dictionary<string, BarRow> _rows = [];
    private readonly Border _panel;
    private readonly ScrollViewer _scroll;
    private readonly Panel _rowHost = new();
    private DockLayout _layout = new([]);

    public MenuStrip()
    {
        Tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }
            .Spaced(StackPanel.SpacingProperty, 3);
        Children.Add(Tabs);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,   // the buttons wrap onto more lines instead
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,       // and a strip taller than the window allows scrolls: no button is hidden
            Content = _rowHost,
        };
        _panel = new Border
        {
            Name = "MenuStripPanel",
            IsVisible = false,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = _scroll,
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 4 14 0 #40000000"),
        };
        _panel.Bind(Border.BackgroundProperty, _panel.GetResourceObservable("Ui.Panel"));
        _panel.Bind(Border.BorderBrushProperty, _panel.GetResourceObservable("Ui.TabBorder"));
        _panel.SpacedThickness(Border.PaddingProperty, new Thickness(6, 5));
    }

    /// <summary>The tab row (for the window to place and for tests).</summary>
    public StackPanel Tabs { get; }

    /// <summary>The drop-down panel with the open strip's buttons: the window puts it in a layer over the canvas, under the tabs.</summary>
    public Border StripPanel => _panel;

    /// <summary>Closes the open strip, if any.</summary>
    public void Close()
    {
        if (_layout.Open is { } open) _layout.Toggle(open);
    }

    /// <summary>The tab that is open now ("Image", "Effects", "Tools"), or null.</summary>
    public string? Open => _layout.Open;

    /// <summary>
    /// Says whether a command's button looks pressed when the command itself does not know (a tool button: pressed while that tool is the active one,
    /// as in the Toolbox); null means "use the command's own check state". Set by the window.
    /// </summary>
    public Func<AppCommand, bool?>? Pressed { get; set; }

    /// <summary>Raised after a click opened, switched or closed a strip.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<string> TabNames => _layout.Panels;

    /// <summary>Builds a tab and a strip for each menu in <see cref="TabbedMenus"/> from the menu layout, so the strips list what the menus list.</summary>
    public void Populate(IEnumerable<MenuNode> menuLayout, CommandRegistry commands)
    {
        var menus = menuLayout.Where(m => m.CommandId is null && TabbedMenus.Contains(m.Header)).ToList();
        _layout = new DockLayout([.. menus.Select(m => NameOf(m.Header))]);
        _layout.Changed += (_, _) =>
        {
            Apply();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        foreach (var menu in menus)
        {
            string name = NameOf(menu.Header);
            // the name with a small down arrow: the buttons drop down from it
            var chevron = new Avalonia.Controls.Shapes.Path
            {
                Data = Avalonia.Media.Geometry.Parse("M0,0 L3.5,3.5 L7,0"), StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, chevron.GetResourceObservable("Ui.TabText"));
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(chevron);
            var tab = new ToggleButton { Name = $"MenuTab{name}", Content = header, Focusable = false };
            tab.Classes.Add("mtab");
            ToolTip.SetTip(tab, $"Show or hide the {name} buttons");
            Avalonia.Automation.AutomationProperties.SetName(tab, name);
            tab.Click += (_, _) => _layout.Toggle(name);
            Tabs.Children.Add(tab);
            _tabs[name] = tab;

            var row = new BarRow { IsVisible = false }.Spaced(BarRow.SpacingProperty, 0).Spaced(BarRow.LineSpacingProperty, 3);
            var entries = new List<Entry>();
            foreach (var node in menu.Children ?? [])
            {
                if (node.IsSeparator) {   // dividers are shorter than the bar's own: a line of text buttons is only about 15 px tall
                 if (entries.Count > 0 && entries[^1].Command is not null) { entries.Add(new Entry(null, null)); row.Children.Add(ContextBars.Separator().ScaledHeight(12)); } continue; }
                if (node.CommandId is not { } id) continue;   // submenus (not built yet)
                var c = commands.Get(id);
                if (Skipped(c)) continue;
                var button = MakeButton(c);
                row.Children.Add(button);
                entries.Add(new Entry(c, button));
            }
            _rows[name] = row;
            _entries[name] = entries;
            _rowHost.Children.Add(row);
        }
        Apply();
        Refresh();
    }

    private static string NameOf(string header) => header.Replace("_", "");

    private Button MakeButton(AppCommand c)
    {
        // Text first (user): the short label is the button, with a small icon beside it; the tooltip has the full command name.
        var label = new TextBlock { Text = MenuStripIcons.LabelFor(c), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap }.ScaledFont(LabelSize);
        label.Classes.Add("striplabel");
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }.Spaced(StackPanel.SpacingProperty, 3);
        if (MenuStripIcons.For(c.Id, IconSize) is { } icon)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
        }
        content.Children.Add(label);
        var b = new Button { Name = "Strip_" + c.Id.Replace('.', '_'), Content = content, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        b.Classes.Add("tb");
        b.Classes.Add("strip");
        ToolTip.SetTip(b, c.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(b, c.ToolTip);
        b.Click += (_, _) =>
        {
            Close();   // the strip covers the canvas: it goes away once a command is picked, like a menu
            if (c.CanExecute()) c.Execute();
        };
        return b;
    }

    /// <summary>What a command without a drawn icon shows: the first letters of its name.</summary>
    private static Control Initials(AppCommand c)
    {
        var words = c.Header.Replace("_", "").Replace(".", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string text = string.Concat(words.Take(2).Select(w => w[0]));
        return new TextBlock { Text = text, FontWeight = FontWeight.Bold, Foreground = AppTheme.Brush("Ui.Icon"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Opens or closes a strip as a click on its tab does (tests click the tab itself).</summary>
    public void Toggle(string name) => _layout.Toggle(name);

    /// <summary>Re-reads every button's enabled and pressed state from its command (the window calls this whenever its panels refresh).</summary>
    public void Refresh()
    {
        if (_layout.Open is not { } open) return;   // a closed strip is not looked at, and costs nothing
        foreach (var e in _entries[open])
        {
            if (e.Command is not { } c || e.Button is not { } b) continue;
            b.IsEnabled = c.CanExecute();
            if (Pressed?.Invoke(c) is { } pressed) b.Classes.Set("on", pressed);
            else if (c.IsChecked is { } isOn) b.Classes.Set("on", isOn());
        }
    }

    private void Apply()
    {
        foreach (var (name, tab) in _tabs)
        {
            bool open = _layout.IsOpen(name);
            tab.IsChecked = open;
            _rows[name].IsVisible = open;
        }
        _panel.IsVisible = _layout.Open is not null;
        Refresh();
    }

    // ---- For tests ----

    internal ToggleButton Tab(string name) => _tabs[name];

    /// <summary>The buttons of one strip, in order.</summary>
    internal IReadOnlyList<Button> ButtonsOf(string name) => [.. _entries[name].Where(e => e.Button is not null).Select(e => e.Button!)];

    /// <summary>The command each button of a strip runs, in order.</summary>
    internal IReadOnlyList<AppCommand> CommandsOf(string name) => [.. _entries[name].Where(e => e.Command is not null).Select(e => e.Command!)];

    internal Border Panel => _panel;

    /// <summary>One strip as a list of command ids with "-" for each divider, in order (tests).</summary>
    internal IReadOnlyList<string> LayoutOf(string name) => [.. _entries[name].Select(e => e.Command?.Id ?? "-")];

    /// <summary>The label under each button of a strip, in order (tests).</summary>
    internal IReadOnlyList<string> LabelsOf(string name) =>
        [.. ButtonsOf(name).Select(b => ((StackPanel)b.Content!).Children.OfType<TextBlock>().Last().Text ?? "")];

}
