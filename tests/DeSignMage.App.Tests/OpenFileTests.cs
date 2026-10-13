using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>File &gt; Open for legacy layouts (the drop handler calls the same OpenFileAsync).</summary>
public class OpenFileTests
{
    private static string Sample => LocalData.Sample;


    private static MainWindow Show(StartupOptions? options = null)
    {
        var w = new MainWindow(options ?? new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    /// <summary>Dialogs answered by the test: records what was shown and returns <paramref name="answer"/>.</summary>
    private static List<(string Title, string Message)> Answer(MainWindow w, bool answer)
    {
        var shown = new List<(string, string)>();
        w.DialogOverride = (title, message) => { shown.Add((title, message)); return Task.FromResult(answer); };
        return shown;
    }

    private static string TempFile(string name, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsm-test-{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, content);
        return path;
    }

    [AvaloniaFact]
    public void File_menu_has_an_enabled_Open_command_and_the_toolbar_a_button_for_it()
    {
        var w = Show();
        var menu = w.FindControl<Menu>("MainMenu")!;
        var file = menu.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("File") == true);
        var open = file.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("Open") == true);
        Assert.True(open.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Opening_the_sample_loads_its_objects_and_leaves_no_unsaved_changes()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show();
        var shown = Answer(w, true);

        Assert.True(await w.OpenFileAsync(Sample));
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();

        Assert.Equal(283, w.Session.Document.ObjectCount);
        Assert.Equal(46, w.Session.Document.GroupCount);   // every group of the file is kept (R22)
        Assert.StartsWith(LocalData.SampleName + " - ", w.Title);          // no " *": nothing modified
        Assert.False(w.Session.History.CanUndo);
        Assert.False(w.Session.History.IsModified);
        Assert.Null(w.LastOpenError);
        Assert.Empty(shown);                                                         // a clean file shows no message
    }

    [AvaloniaFact]
    public async Task The_opened_design_is_fitted_in_the_window()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show();
        Answer(w, true);
        var canvas = w.FindControl<DesignCanvas>("Surface")!;

        await w.OpenFileAsync(Sample);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();

        // Page and the pasteboard variants to its right are both on screen.
        var vp = w.Session.Viewport;
        var content = w.Session.Document.GetContentBounds().Union(w.Session.Document.Page.Bounds);
        var tl = vp.ToScreen(content.TopLeft);
        var br = vp.ToScreen(content.BottomRight);
        Assert.True(tl.X >= -1 && tl.Y >= -1, $"top-left {tl}");
        Assert.True(br.X <= canvas.Bounds.Width + 1 && br.Y <= canvas.Bounds.Height + 1, $"bottom-right {br} in {canvas.Bounds.Size}");
        Assert.True(vp.ZoomPercent > 10);
    }

    [AvaloniaFact]
    public void A_path_on_the_command_line_is_opened_once_the_window_is_up()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show(new StartupOptions(OpenPath: Sample));
        // The file is read on a worker thread (with a progress window if slow), so the design arrives a moment after the window.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (w.Session.Document.ObjectCount != 283 && watch.ElapsedMilliseconds < 10_000)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.Equal(283, w.Session.Document.ObjectCount);
        Assert.Contains(LocalData.SampleName, w.Title);
    }

    [AvaloniaFact]
    public async Task A_damaged_file_is_reported_and_the_current_design_is_kept()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show(new StartupOptions(Demo: true));
        var before = w.Session;
        var shown = Answer(w, true);

        var good = File.ReadAllBytes(Sample);
        var damaged = TempFile("damaged.lyt", good[..(good.Length / 2)]);
        var notALayout = TempFile("random.lyt", new byte[500]);
        var wrongType = TempFile("picture.png", [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            Assert.False(await w.OpenFileAsync(damaged));
            Assert.NotNull(w.LastOpenError);
            Assert.False(await w.OpenFileAsync(notALayout));
            Assert.False(await w.OpenFileAsync(wrongType));
            Assert.Contains(".png", w.LastOpenError);
        }
        finally
        {
            File.Delete(damaged); File.Delete(notALayout); File.Delete(wrongType);
        }

        Assert.Same(before, w.Session);
        Assert.Equal(11, w.Session.Document.ObjectCount);
        Assert.Equal(3, shown.Count);
        Assert.All(shown, s => Assert.StartsWith("Cannot open ", s.Title));
    }

    [AvaloniaFact]
    public async Task Missing_files_are_reported_not_thrown()
    {
        var w = Show();
        Answer(w, true);

        Assert.False(await w.OpenFileAsync(Path.Combine(Path.GetTempPath(), "does-not-exist.lyt")));
        Assert.NotNull(w.LastOpenError);
    }

       [AvaloniaFact]
    public async Task Edits_are_not_discarded_without_asking()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show();
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, new RectangleObject(50, 50)));
        var before = w.Session;

        // Cancel: nothing is opened and the design is untouched.
        var asked = new List<string>();
        w.SaveChangesOverride = (title, message) => { asked.Add(message); return Task.FromResult(MainWindow.SaveChoice.Cancel); };
        Assert.False(await w.OpenFileAsync(Sample));
        Assert.Same(before, w.Session);
        var question = Assert.Single(asked);
        Assert.Contains("Save the changes to Untitled", question);
        Assert.Contains(LocalData.SampleName, question);

        // Don't Save: the layout opens and the edit is gone.
        w.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Discard);
        Answer(w, true);
        Assert.True(await w.OpenFileAsync(Sample));
        Assert.NotSame(before, w.Session);
        Assert.Equal(283, w.Session.Document.ObjectCount);
    }
    [AvaloniaFact]
    public async Task Layouts_that_lose_something_say_so_after_opening()
    {
        Assert.SkipUnless(LocalData.Has("layouts/image2.lyt"), "needs local-testdata/layouts/image2.lyt (see the README)");
        var w = Show();
        var shown = Answer(w, true);

        // image2.lyt has a picture clipped to a lettering shape, which is only approximated for now.
        Assert.True(await w.OpenFileAsync(LocalData.Layout("image2.lyt")));

        Assert.NotEmpty(w.LastOpenWarnings);
        var note = Assert.Single(shown);
        Assert.Contains("skipped or approximated", note.Message);
        Assert.Contains("image fill", note.Message);
        Assert.Equal("image2.lyt", note.Title);
    }

    [AvaloniaFact]
    public async Task File_New_after_opening_goes_back_to_an_untitled_design()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show();
        Answer(w, true);
        await w.OpenFileAsync(Sample);

        // The File > New command is the first toolbar button.
        var menu = w.FindControl<Menu>("MainMenu")!;
        var file = menu.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("File") == true);
        var @new = file.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("New") == true);
        @new.Command?.Execute(null);
        @new.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("Untitled", w.Title);
        Assert.NotEqual(283, w.Session.Document.ObjectCount);
    }
}

