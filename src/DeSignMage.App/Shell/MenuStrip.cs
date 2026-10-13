using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeSignMage.App.Shell;

/// <summary>
/// The menu tabs of the Context Bar (requirement R25): a tab for each of a few menus (Image, Effects, Tools), in the right half of the bar
/// that the tool and selection controls rarely use. A click on a tab opens a strip of icon buttons, one per command of that menu, right
/// below it; a click on the open tab closes it. One strip is open at a time (<see cref="DockLayout"/> decides, as for the Dock), and the
/// program starts with none open. The normal drop-down menus stay. A button runs the very command its menu item runs and is enabled when
/// the item is. The strip lives inside the Context Bar's fixed height, so opening or closing it never moves anything.
/// </summary>
public sealed class MenuStrip : Grid
{
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
        RowDefinitions = new RowDefinitions("Auto,*");
        HorizontalAlignment = HorizontalAlignment.Right;
        Tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ZIndex = 1 }
            .Spaced(StackPanel.SpacingProperty, 2);
        // the open tab overlaps the panel's top line by one pixel, so tab and panel read as one piece (as in the Dock)
        Tabs.Margin = new Thickness(0, 0, 0, -1);
        SetRow(Tabs, 0);
        Children.Add(Tabs);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rowHost,
        };
        _scroll.PointerWheelChanged += (_, e) =>   // a strip that is wider than its half scrolls sideways with the wheel
        {
            _scroll.Offset = new Vector(Math.Max(0, _scroll.Offset.X - e.Delta.Y * 48), 0);
            e.Handled = true;
        };
        _panel = new Border
        {
            Name = "MenuStripPanel",
            IsVisible = false,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = UiScale.Space(3, 1),
            Child = _scroll,
            ClipToBounds = true,
        };
        _panel.Bind(Border.BackgroundProperty, _panel.GetResourceObservable("Ui.Panel"));
        _panel.Bind(Border.BorderBrushProperty, _panel.GetResourceObservable("Ui.TabBorder"));
        _panel.SpacedThickness(Border.PaddingProperty, new Thickness(3, 1));
        SetRow(_panel, 1);
        Children.Add(_panel);
    }

    /// <summary>The tab row (for the window to place and for tests).</summary>
    public StackPanel Tabs { get; }

    /// <summary>The tab that is open now ("Image", "Effects", "Tools"), or null.</summary>
    public string? Open => _layout.Open;

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
            var tab = new ToggleButton { Name = $"MenuTab{name}", Content = name, Focusable = false };
            tab.Classes.Add("dtab");
            ToolTip.SetTip(tab, $"Open or close the {name} buttons");
            tab.Click += (_, _) => _layout.Toggle(name);
            Tabs.Children.Add(tab);
            _tabs[name] = tab;

            var row = new BarRow { IsVisible = false }.Spaced(BarRow.SpacingProperty, 0);
            var entries = new List<Entry>();
            foreach (var node in menu.Children ?? [])
            {
                if (node.IsSeparator) { if (entries.Count > 0 && entries[^1].Command is not null) { entries.Add(new Entry(null, null)); row.Children.Add(ContextBars.Separator()); } continue; }
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

    private static Button MakeButton(AppCommand c)
    {
        Control icon = MenuStripIcons.For(c.Id) ?? Initials(c);
        var b = new Button { Name = "Strip_" + c.Id.Replace('.', '_'), Content = icon, Focusable = false };
        b.Classes.Add("tb");
        b.Classes.Add("strip");
        ToolTip.SetTip(b, c.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(b, c.ToolTip);
        b.Click += (_, _) => { if (c.CanExecute()) c.Execute(); };
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
            if (c.IsChecked is { } isOn) b.Classes.Set("on", isOn());
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
}
