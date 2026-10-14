using Avalonia.Controls;
using Avalonia.Input;
using DeSignMage.Feedback;

namespace DeSignMage.App.Shell;

/// <summary>
/// A user-invokable action with an id, menu text and toolbar glyph. Menus, toolbars and the key map
/// (<see cref="KeyMap"/>) all refer to commands by id, so those tables can be rearranged freely.
/// </summary>
public sealed class AppCommand(string id, string header, Action execute)
{
    public string Id { get; } = id;
    public string Header { get; } = header;
    /// <summary>Runs the command; every run (menu, toolbar, key) is written to the log by id so a bug report shows what was done.</summary>
    public Action Execute { get; } = () =>
    {
        AppLog.Command(id);
        execute();
    };
    public Func<bool> CanExecute { get; init; } = () => true;
    /// <summary>Key bindings, filled from the key map. The first one is shown in menus and tooltips.</summary>
    public List<KeyGesture> Gestures { get; } = [];
    public KeyGesture? Gesture => Gestures.Count > 0 ? Gestures[0] : null;
    /// <summary>Segoe Fluent Icons code point for toolbar buttons.</summary>
    public string? Glyph { get; init; }
    /// <summary>Set for on/off commands (View ▸ Show Rulers ...): menus show a check mark, toolbar buttons look pressed.</summary>
    public Func<bool>? IsChecked { get; init; }
    public string ToolTip => Gesture is null ? Header.Replace("_", "") : $"{Header.Replace("_", "")} ({Gesture})";

    /// <summary>True for a menu item that is only a placeholder for something not built yet (always disabled).</summary>
    public bool IsPlaceholder { get; init; }

    public static AppCommand NotImplemented(string id, string header) => new(id, header, () => { }) { CanExecute = () => false, IsPlaceholder = true };
}

/// <summary>
/// Keyboard shortcuts as data: (gesture, command id) pairs. Many commands use plain letter keys (requirement R3);
/// those fire only while the canvas has focus, so typing in fields and text is never hijacked.
/// The full letter map is not settled yet; add entries here.
/// </summary>
public static class KeyMap
{
    public static readonly (string Gesture, string CommandId)[] Default =
    [
        // Standard
        ("Ctrl+O", "file.open"), ("Ctrl+S", "file.save"), ("Ctrl+Shift+S", "file.saveAs"),
        ("Ctrl+F8", "file.import"), ("Alt+X", "file.export"),
        // Undo and Redo also work on the plain letters Z and Y (user, 2026-10-13); listed first so the Edit menu shows "Z" and "Y".
        // Like X/C/V they only fire while the canvas has the keyboard focus and no text is being edited.
        ("Z", "edit.undo"), ("Y", "edit.redo"),
        ("Ctrl+Z", "edit.undo"), ("Ctrl+Y", "edit.redo"), ("Ctrl+Shift+Z", "edit.redo"),
        // Cut, Copy and Paste also work on the plain letters (user, 2026-10-13). Listed first, so the menus show "X", "C" and "V"; Ctrl+X/C/V
        // still work. Plain keys only fire while the canvas has the keyboard focus and no text is being edited (see IsPlainKey).
        ("X", "edit.cut"), ("C", "edit.copy"), ("V", "edit.paste"),
        ("Ctrl+X", "edit.cut"), ("Ctrl+C", "edit.copy"), ("Ctrl+V", "edit.paste"),
        ("Delete", "edit.delete"), ("Ctrl+A", "edit.selectAll"),
        // Accelerator table
        ("Ctrl+E", "view.rulers"),
        ("Ctrl+Shift+G", "tool.rectangle"), ("Ctrl+Shift+E", "tool.ellipse"),
        ("PageUp", "arrange.toTop"), ("Add", "arrange.oneUp"), ("Subtract", "arrange.oneDown"), ("PageDown", "arrange.toBottom"),
        ("Ctrl+G", "arrange.group"), ("Ctrl+B", "arrange.combine"),
        // Plain-letter keys (R3)
        ("U", "arrange.ungroup"), ("Ctrl+U", "arrange.ungroup"),
        // Align Left / Right / Center: plain L, R and N, approved by the user (2026-10-13). L is also To Line in the Vector Edit tool: the two commands are
        // never enabled together (the align commands need the Layout tool), so the key does whichever applies.
        ("L", "arrange.alignLeft"), ("R", "arrange.alignRight"), ("N", "arrange.alignCenter"),
        ("K", "arrange.breakApart"),                                   // plain K only (user, 2026-10-11); Ctrl+K "Break Into Loops" is not bound
        ("D1", "tool.select"), ("D2", "tool.vector"),                    // 1 = Layout tool, 2 = Vector Edit tool
        ("F3", "view.fillColors"),
        // Vector menu keys: active only in the vector tool (their commands are disabled elsewhere)
        ("Insert", "vector.insert"), ("L", "vector.toLine"), ("A", "vector.toArc"), ("B", "vector.toBezier"),
        ("O", "vector.breakLoop"), ("E", "vector.setStart"), ("J", "vector.join"), ("Delete", "vector.delete"),
        ("Delete", "loop.delete"),                                       // Loop Edit help: "press the Delete key"; only enabled in the Loop Edit tool
    ];

    /// <summary>A key without Ctrl/Alt/Win that would type a character: canvas-only.</summary>
    public static bool IsPlainKey(KeyGesture g) =>
        (g.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0 &&
        g.Key is (>= Key.A and <= Key.Z) or (>= Key.D0 and <= Key.D9) or Key.Space;

    public static void Apply(CommandRegistry registry, IEnumerable<(string Gesture, string CommandId)> map)
    {
        foreach (var (gesture, id) in map) registry.Get(id).Gestures.Add(KeyGesture.Parse(gesture));
    }
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, AppCommand> _commands = [];

    public void Add(AppCommand c) => _commands.Add(c.Id, c);
    public AppCommand Get(string id) => _commands[id];
    public IEnumerable<AppCommand> All => _commands.Values;

    /// <summary>Runs the command bound to the key, if any. Plain letter keys only count when <paramref name="canvasFocused"/>.</summary>
    public bool TryHandle(KeyEventArgs e, bool canvasFocused)
    {
        foreach (var c in _commands.Values)
        {
            var g = c.Gestures.FirstOrDefault(g => g.Matches(e));
            if (g is null || (!canvasFocused && KeyMap.IsPlainKey(g)) || !c.CanExecute()) continue;
            c.Execute();
            return true;
        }
        return false;
    }
}
/// <summary>Menu tree: a node is a command reference, a separator ("-"), or a submenu.</summary>
public sealed record MenuNode(string Header, string? CommandId = null, MenuNode[]? Children = null)
{
    public static MenuNode Menu(string header, params object[] items) =>
        new(header, null, items.Select(i => i is MenuNode n ? n : Cmd((string)i)).ToArray());

    public static MenuNode Cmd(string id) => id == "-" ? new MenuNode("-") : new MenuNode("", id);

    public bool IsSeparator => Header == "-";

    public static Control Build(MenuNode node, CommandRegistry registry)
    {
        if (node.IsSeparator) return new Separator();
        if (node.CommandId is { } id)
        {
            var c = registry.Get(id);
            var item = new MenuItem { Header = c.Header, InputGesture = c.Gesture, Tag = c };
            // The check state is read when the menu opens (the document may not exist yet while menus are built).
            if (c.IsChecked is not null) item.ToggleType = MenuItemToggleType.CheckBox;
            item.Click += (_, _) =>
            {
                if (c.CanExecute()) c.Execute();
                if (c.IsChecked is { } check) item.IsChecked = check();   // the menu item flips itself; the command state decides
            };
            return item;
        }

        var menu = new MenuItem { Header = node.Header };
        var children = node.Children ?? [];
        if (children.Length == 0)
            menu.Items.Add(new MenuItem { Header = "(not implemented yet)", IsEnabled = false });
        foreach (var child in children) menu.Items.Add(Build(child, registry));
        // Refresh enabled state each time the menu opens.
        menu.SubmenuOpened += (_, _) =>
        {
            foreach (var mi in menu.Items.OfType<MenuItem>())
            {
                if (mi.Tag is not AppCommand ac) continue;
                mi.IsEnabled = ac.CanExecute();
                if (ac.IsChecked is { } check) mi.IsChecked = check();
            }
        };
        return menu;
    }
}
