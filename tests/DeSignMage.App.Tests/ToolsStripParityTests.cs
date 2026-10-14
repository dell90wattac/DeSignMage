using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Shell;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>R29: the Tools strip in the Context Bar mirrors the Toolbox on the left: same tools, same order, same icons, and the active tool pressed in both.</summary>
[Collection("app-settings")]   // dark mode and the settings store are program-wide: one class at a time
public class ToolsStripParityTests
{
    private static async Task WithTheme(Func<Task> body)
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        AppTheme.Reset();
        try { await body(); }
        finally
        {
            AppTheme.Reset();
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
        }
    }

    private static MainWindow Open(double width = 1600)
    {
        var w = new MainWindow(new StartupOptions()) { Width = width, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void Press(MainWindow w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, Avalonia.Input.MouseButton.Left);
        w.MouseUp(p, Avalonia.Input.MouseButton.Left);
        Flush();
    }

    private static MenuStrip StripOf(MainWindow w) => w.FindControl<MenuStrip>("MenuStrip")!;

    /// <summary>The Toolbox's tool buttons, in order (not its color-mode button).</summary>
    private static ToggleButton[] ToolboxButtons(MainWindow w) =>
        [.. w.FindControl<StackPanel>("ToolboxHost")!.Children.OfType<ToggleButton>().Take(Toolbox.Buttons.Length)];

    /// <summary>What an icon is made of, so two icons can be told to be the same drawing: the shapes, their geometry, sizes and brushes.</summary>
    private static string[] Signature(Control icon) =>
    [
        .. icon.GetVisualDescendants().Prepend(icon).Select(v => v switch
        {
            Avalonia.Controls.Shapes.Path p => $"Path {p.Data?.ToString()} {p.Fill?.GetHashCode()} {p.Stroke?.GetHashCode()} {p.StrokeThickness}",
            Rectangle r => $"Rectangle {r.Width}x{r.Height} {r.Fill?.GetHashCode()} {r.Stroke?.GetHashCode()}",
            Ellipse e => $"Ellipse {e.Width}x{e.Height} {e.Stroke?.GetHashCode()}",
            TextBlock t when t.Text is { Length: > 0 } && !t.Classes.Contains("striplabel") && !t.Classes.Contains("glyph") => $"Text {t.Text} {t.FontFamily} {t.FontWeight}",
            _ => "",
        }).Where(s => s.Length > 0),
    ];

    /// <summary>The icon of a Toolbox button, or of a strip button (inside its content, without the label).</summary>
    private static Control IconOf(Button strip) => ((StackPanel)strip.Content!).Children[0];

    [AvaloniaFact]
    public async Task The_Tools_strip_has_the_Toolbox_tools_first_in_the_same_order_with_the_same_icons()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            var commands = strip.CommandsOf("Tools").Select(c => c.Id).ToList();
            var expected = Toolbox.Buttons.Select(b => w.CommandOfTool(b.ToolId)).ToArray();
            Assert.Equal(expected, commands.Take(expected.Length));                              // the same tools, in the Toolbox's order
            Assert.Equal(["tool.select", "tool.vector", "tool.vectorDraw", "tool.text", "tool.rectangle", "tool.ellipse"], expected);

            Press(w, strip.Tab("Tools"));
            w.CaptureRenderedFrame();
            var buttons = strip.ButtonsOf("Tools");
            var toolbox = ToolboxButtons(w);
            Assert.Equal(Toolbox.Buttons.Length, toolbox.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                var fromToolbox = Signature((Control)toolbox[i].Content!);
                var fromStrip = Signature(IconOf(buttons[i]));
                Assert.NotEmpty(fromToolbox);
                Assert.Equal(fromToolbox, fromStrip);                                             // not a look-alike: the very same drawing
            }
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task What_the_Tools_menu_has_besides_the_Toolbox_tools_follows_them_with_Transparency_between_Loops_and_Options()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            var layout = strip.LayoutOf("Tools");
            int tools = Toolbox.Buttons.Length;
            Assert.Equal("-", layout[tools]);                                                     // a small divider after the tools
            var rest = layout.Skip(tools + 1).ToList();
            Assert.Equal(["tool.loop", "tools.showTransparency", "tools.options"], rest.Where(x => x != "-"));
            Assert.Equal(rest.IndexOf("tool.loop") + 1, rest.IndexOf("tools.showTransparency"));      // R27: after Loops ...
            Assert.True(rest.IndexOf("tools.showTransparency") < rest.IndexOf("tools.options"));      // ... and before Options
            Assert.DoesNotContain(layout, id => id.StartsWith("debug."));
            Assert.DoesNotContain("-", layout.Take(tools));                                       // the tools themselves have no dividers between them
            // nothing is listed twice
            Assert.Equal(layout.Where(x => x != "-").Count(), layout.Where(x => x != "-").Distinct().Count());

            // the Tools drop-down menu itself is as it was
            var menu = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Tools");
            var ids = menu.Items.OfType<MenuItem>().Where(m => m.Tag is AppCommand).Select(m => ((AppCommand)m.Tag!).Id).ToArray();
            Assert.Equal(["tool.select", "tool.vector", "tool.vectorDraw", "tool.loop", "tools.showTransparency", "tools.options", "debug.stress"], ids);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_active_tool_is_pressed_in_the_strip_and_in_the_Toolbox_and_both_follow_each_other()
    {
        await WithTheme(() =>
        {
            var w = Open();
            var strip = StripOf(w);
            Press(w, strip.Tab("Tools"));
            w.CaptureRenderedFrame();
            var commands = strip.CommandsOf("Tools").Select(c => c.Id).ToList();
            var buttons = strip.ButtonsOf("Tools");
            var toolbox = ToolboxButtons(w);

            string[] PressedInStrip() => [.. Enumerable.Range(0, buttons.Count).Where(i => buttons[i].Classes.Contains("on") && commands[i]!.StartsWith("tool.")).Select(i => commands[i]!)];
            string[] PressedInToolbox() => [.. Enumerable.Range(0, toolbox.Length).Where(i => toolbox[i].IsChecked == true).Select(i => w.CommandOfTool(Toolbox.Buttons[i].ToolId))];

            // at the start: the Layout tool, in both
            Assert.Equal(["tool.select"], PressedInStrip());
            Assert.Equal(["tool.select"], PressedInToolbox());

            // clicking a tool in the strip selects it exactly like the Toolbox button does
            for (int i = 0; i < Toolbox.Buttons.Length; i++)
            {
                Press(w, buttons[i]);
                w.CaptureRenderedFrame();
                Assert.Equal(Toolbox.Buttons[i].ToolId, w.Session.ActiveTool.Id);
                Assert.Equal([commands[i]!],PressedInStrip());
                Assert.Equal([commands[i]!],PressedInToolbox());                                  // the Toolbox follows
            }

            // clicking the tool that is already active leaves it pressed in the Toolbox too
            Press(w, toolbox[^1]);
            Assert.Equal(["tool.ellipse"], PressedInToolbox());
            Assert.Equal(["tool.ellipse"], PressedInStrip());

            // clicking in the Toolbox: the strip follows
            for (int i = Toolbox.Buttons.Length - 1; i >= 0; i--)
            {
                Press(w, toolbox[i]);
                w.CaptureRenderedFrame();
                Assert.Equal(Toolbox.Buttons[i].ToolId, w.Session.ActiveTool.Id);
                Assert.Equal([commands[i]!],PressedInStrip());
                Assert.Equal([commands[i]!],PressedInToolbox());
            }

            // any other way of changing the tool (keys, menu, the session itself) is shown in both places as well
            w.Session.SetTool("vectordraw");
            Flush();
            Assert.Equal(["tool.vectorDraw"], PressedInStrip());
            Assert.Equal(["tool.vectorDraw"], PressedInToolbox());
            w.KeyPress(Avalonia.Input.Key.D2, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Digit2, "2");
            Flush();
            Assert.Equal(["tool.vector"], PressedInStrip());
            Assert.Equal(["tool.vector"], PressedInToolbox());

            // the Loop Edit tool has no Toolbox button: its strip button is pressed and no Toolbox button is
            w.Session.SetTool("loop");
            Flush();
            Assert.Equal(["tool.loop"], PressedInStrip());
            Assert.Empty(PressedInToolbox());

            // a strip that was closed while the tool changed shows the right button when it opens
            Press(w, strip.Tab("Tools"));
            Assert.Null(w.OpenMenuStrip);
            w.Session.SetTool("ellipse");
            Flush();
            Press(w, strip.Tab("Tools"));
            w.CaptureRenderedFrame();
            Assert.Equal("Tools", w.OpenMenuStrip);
            Assert.Equal("ellipse", w.Session.ActiveTool.Id);
            Assert.Equal(["tool.ellipse"], PressedInStrip());
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_strip_buttons_keep_their_short_text_labels_and_work_in_dark_menus()
    {
        await WithTheme(() =>
        {
            AppTheme.SetDarkMenus(true);
            var w = Open();
            var strip = StripOf(w);
            Assert.Equal(["Layout", "Vector", "Draw", "Text", "Rectangle", "Ellipse", "Loops", "Transparency", "Options"], strip.LabelsOf("Tools"));
            Press(w, strip.Tab("Tools"));
            w.CaptureRenderedFrame();
            Press(w, strip.ButtonsOf("Tools")[1]);
            Assert.Equal("vector", w.Session.ActiveTool.Id);
            Assert.Contains("on", strip.ButtonsOf("Tools")[1].Classes);
            // every tool button has an icon beside its label
            Assert.All(strip.ButtonsOf("Tools"), b => Assert.IsType<StackPanel>(b.Content));
            Assert.All(strip.ButtonsOf("Tools"), b => Assert.Equal(2, ((StackPanel)b.Content!).Children.Count));
            w.Close();
            return Task.CompletedTask;
        });
    }

    /// <summary>Saves a picture of the Toolbox and the Tools strip side by side, light and dark, when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_of_the_Tools_strip_next_to_the_Toolbox_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        await WithTheme(() =>
        {
            Directory.CreateDirectory(dir);
            var w = new MainWindow(new StartupOptions(Demo: true)) { Width = 1600, Height = 900 };
            w.Show();
            Flush();
            var strip = StripOf(w);
            Press(w, strip.Tab("Tools"));
            w.Session.SetTool("vectordraw");
            Flush();
            foreach (bool dark in new[] { false, true })
            {
                AppTheme.SetDarkMenus(dark, remember: false);
                Flush();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
                w.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, $"tools-strip-parity-{(dark ? "dark" : "light")}.png"));
#pragma warning restore CS0618
            }
            w.Close();
            return Task.CompletedTask;
        });
    }
}
