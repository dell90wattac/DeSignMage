using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Text ▸ Change Font...: the menu item, its enabled state and the dialog that sets one font for many texts.</summary>
public class ChangeFontShellTests
{
    private static MainWindow Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static void Pump(Func<bool> done, int timeoutMs = 15000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Strip(object? header) => ((string)header!).Replace("_", "");

    private static IEnumerable<MenuItem> TextMenu(MainWindow w) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => Strip(m.Header) == "Text").Items.OfType<MenuItem>();

    private static AppCommand ChangeFont(MainWindow w) => (AppCommand)TextMenu(w).Single(m => m.Tag is AppCommand { Id: "text.changeFont" }).Tag!;

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TextObject AddText(MainWindow w, string text, IObjectContainer? into = null, double size = 36, double tracking = 0.1)
    {
        var t = new TextObject(text, "Arial", size) { Tracking = tracking, Alignment = TextAlignment.Center };
        w.Session.History.Execute(new AddObjectCommand(into ?? w.Session.Document.ActiveLayer, t));
        return t;
    }

    private static GroupObject AddGroup(MainWindow w, IObjectContainer? into = null)
    {
        var g = new GroupObject();
        w.Session.History.Execute(new AddObjectCommand(into ?? w.Session.Document.ActiveLayer, g));
        return g;
    }

    [AvaloniaFact]
    public void The_Text_menu_lists_Change_Font_after_the_Text_Tool_and_has_no_shortcut()
    {
        var w = Open();
        var items = TextMenu(w).Select(m => Strip(m.Header)).ToList();

        Assert.Equal("Change Font...", items[items.IndexOf("Text Tool") + 1]);
        Assert.Empty(ChangeFont(w).Gestures);
        w.Close();
    }

    [AvaloniaFact]
    public void It_is_enabled_only_when_the_selection_holds_live_text_at_any_depth()
    {
        var w = Open();
        var command = ChangeFont(w);
        var outer = AddGroup(w);
        var inner = AddGroup(w, outer);
        var text = AddText(w, "Deep", inner);
        var rect = new RectangleObject(10, 10);
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, rect));

        w.Session.Selection.Clear();
        Assert.False(command.CanExecute());                     // nothing selected
        w.Session.Selection.Set([rect]);
        Assert.False(command.CanExecute());                     // only a shape
        w.Session.Selection.Set([outer]);
        Assert.True(command.CanExecute());                      // a group with text two levels down (the Context Bar's font controls stay hidden for it)
        w.Session.Selection.Set([text]);
        w.Session.ConvertSelectionToCurves();
        Assert.False(command.CanExecute());                     // converted to curves: no longer text
        w.Close();
    }

    [AvaloniaFact]
    public void The_dialog_sets_one_font_on_every_text_in_the_selection_as_one_undo_step()
    {
        var w = Open();
        var outer = AddGroup(w);
        var inner = AddGroup(w, outer);
        var deep = AddText(w, "Deep", inner, size: 20, tracking: 0.3);
        var mid = AddText(w, "Middle", outer, size: 48);
        var top = AddText(w, "Top", size: 72, tracking: -0.02);
        var rect = new RectangleObject(10, 10);
        w.Session.History.Execute(new AddObjectCommand(outer, rect));
        w.Session.Selection.Set([outer, top]);
        int steps = w.Session.History.UndoList.Count;

        ChangeFont(w).Execute();
        Pump(() => w.OpenChangeFontDialog is not null);
        var dialog = w.OpenChangeFontDialog!;

        Assert.Contains("3 texts", dialog.SummaryText.Text);                       // how many change
        Assert.Contains("1 other selected object", dialog.SkippedText.Text);        // and how many are left alone
        Assert.True(dialog.SkippedText.IsVisible);
        Assert.False(dialog.OkButton.IsEnabled);                                    // nothing picked yet
        Assert.Equal("Deep", dialog.Picker.Sample);                                 // rows show the user's own text (R12): the first selected text's
        Assert.True(dialog.Picker.Items.Count > 5);
        Assert.NotNull(dialog.Picker.List.ItemsSource);

        dialog.Picker.Choose(new FontItem("Times New Roman", false));
        Assert.True(dialog.OkButton.IsEnabled);
        Assert.Equal("Times New Roman", dialog.PreviewName.Text);
        Click(dialog.OkButton);
        Pump(() => w.OpenChangeFontDialog is null && w.LastChangeFont is not null);

        Assert.All(new[] { deep, mid, top }, t => Assert.Equal("Times New Roman", t.FontFamily));
        Assert.Equal((20.0, 0.3), (deep.FontSize, deep.Tracking));
        Assert.Equal((48.0, TextAlignment.Center), (mid.FontSize, mid.Alignment));
        Assert.Equal((72.0, -0.02), (top.FontSize, top.Tracking));
        Assert.Equal(new ChangeFontResult(3, 0, 1), w.LastChangeFont);
        Assert.Equal(steps + 1, w.Session.History.UndoList.Count);
        Assert.Equal("Change Font", w.Session.History.UndoDescription);

        w.Session.Undo();
        Assert.All(new[] { deep, mid, top }, t => Assert.Equal("Arial", t.FontFamily));
        w.Close();
    }

    [AvaloniaFact]
    public void Cancel_changes_nothing_and_a_single_text_gets_the_singular_wording()
    {
        var w = Open();
        var t = AddText(w, "Solo");
        w.Session.Selection.Set([t]);
        int steps = w.Session.History.UndoList.Count;

        ChangeFont(w).Execute();
        Pump(() => w.OpenChangeFontDialog is not null);
        var dialog = w.OpenChangeFontDialog!;
        Assert.StartsWith("The font of the selected text will change", dialog.SummaryText.Text);
        Assert.False(dialog.SkippedText.IsVisible);                                  // nothing is skipped: no note
        dialog.Picker.Choose(new FontItem("Verdana", false));
        Click(dialog.CancelButton);
        Pump(() => w.OpenChangeFontDialog is null);

        Assert.Equal("Arial", t.FontFamily);
        Assert.Equal(steps, w.Session.History.UndoList.Count);
        Assert.Null(w.LastChangeFont);
        w.Close();
    }

    /// <summary>Developer tool, not a test: with DSM_SHOTS set to a folder it writes a screenshot of the dialog; otherwise it does nothing.</summary>
    [AvaloniaFact]
    public void Write_a_screenshot_when_asked()
    {
        var folder = Environment.GetEnvironmentVariable("DSM_SHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var w = Open();
        var group = AddGroup(w);
        AddText(w, "Sample sign text", group);
        AddText(w, "professional pet sitting", group);
        var rect = new RectangleObject(10, 10);
        w.Session.History.Execute(new AddObjectCommand(group, rect));
        w.Session.Selection.Set([group]);
        ChangeFont(w).Execute();
        Pump(() => w.OpenChangeFontDialog is not null);
        var dialog = w.OpenChangeFontDialog!;
        dialog.Picker.Choose(new FontItem("Georgia", false));
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload is not needed for a plain PNG
        dialog.CaptureRenderedFrame()!.Save(Path.Combine(folder, "change-font-dialog.png"));
#pragma warning restore CS0618
        Click(dialog.CancelButton);
        Pump(() => w.OpenChangeFontDialog is null);
        w.Close();
    }

    [AvaloniaFact]
    public void Texts_that_share_a_font_start_with_it_selected_in_the_list()
    {
        var w = Open();
        var a = AddText(w, "One");
        var b = AddText(w, "Two");
        w.Session.Selection.Set([a, b]);

        ChangeFont(w).Execute();
        Pump(() => w.OpenChangeFontDialog is not null);
        var dialog = w.OpenChangeFontDialog!;
        Assert.Equal("Arial", dialog.Picker.SelectedItem?.Name);
        Assert.False(dialog.OkButton.IsEnabled);                                     // choosing the font they already have is not a change
        Click(dialog.CancelButton);
        Pump(() => w.OpenChangeFontDialog is null);

        b.FontFamily = "Georgia";
        ChangeFont(w).Execute();
        Pump(() => w.OpenChangeFontDialog is not null);
        Assert.Null(w.OpenChangeFontDialog!.Picker.SelectedItem);                    // mixed fonts: none is preselected
        Click(w.OpenChangeFontDialog!.CancelButton);
        Pump(() => w.OpenChangeFontDialog is null);
        w.Close();
    }
}
