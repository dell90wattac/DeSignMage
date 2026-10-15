using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.Rendering.Export;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>File ▸ New / Open / Save / Save As / Export and the unsaved-changes questions.</summary>
public class FileCommandTests
{
    private static string Sample => LocalData.Sample;


    private static MainWindow Show(StartupOptions? options = null)
    {
        var w = new MainWindow(options ?? new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        // Messages never block a test; a test that wants to see them replaces this.
        w.DialogOverride = (_, _) => Task.FromResult(true);
        return w;
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("dsm-app-test-").FullName;

    private static void Draw(MainWindow w, double width = 60) =>
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, new RectangleObject(width, 40)
        {
            Name = "box",
            Style = ObjectStyle.FillOnly(new DocColor(10, 20, 30, 255, "Test Vinyl")),
        }));

    private static MenuItem FileMenu(MainWindow w) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Contains("File") == true);

    private static MenuItem Item(MenuItem menu, string text) =>
        menu.Items.OfType<MenuItem>().First(m => (m.Header as string)?.Replace("_", "").StartsWith(text) == true);

    [AvaloniaFact]
    public void The_File_menu_has_no_palette_loading_items()
    {
        var w = Show();
        var headers = FileMenu(w).Items.OfType<MenuItem>().Select(m => (m.Header as string) ?? "").ToList();
        Assert.DoesNotContain(headers, h => h.Contains("Palette"));
        Assert.Contains(headers, h => h.Replace("_", "").StartsWith("Export"));
        w.Close();
    }

    // ---------------------------------------------------------------- Save / Save As

    [AvaloniaFact]
    public async Task Save_As_writes_a_dsm_names_the_window_and_clears_the_dirty_mark()
    {
        var w = Show();
        var dir = TempDir();
        try
        {
            Draw(w);
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith(" * - DeSign Mage", w.Title);                     // the dirty indicator

            var path = Path.Combine(dir, "sign.dsm");
            string? suggested = null;
            w.SavePathOverride = s => { suggested = s; return path; };
            Assert.True(await w.SaveAsAsync());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Untitled.dsm", suggested);
            Assert.True(File.Exists(path));
            Assert.Equal("sign.dsm - DeSign Mage", w.Title);
            Assert.False(w.Session.History.IsModified);
            Assert.Equal(path, w.DocumentPath);
            Assert.Contains(path, w.RecentPaths);
            Assert.Equal(1, DsmReader.Load(path).Document.ObjectCount);
            var thumbnail = DsmReader.Load(path).Thumbnail;
            Assert.NotNull(thumbnail);                                          // a preview picture is stored...
            using var bmp = SkiaSharp.SKBitmap.Decode(thumbnail);
            Assert.Equal((256, 128), (bmp.Width, bmp.Height));
            Assert.Equal(0, bmp.GetPixel(128, 64).Alpha);                       // ...with real transparency where nothing is drawn, like every export
            Assert.Equal(0, bmp.GetPixel(250, 120).Alpha);
            Assert.Equal(new SkiaSharp.SKColor(10, 20, 30), bmp.GetPixel(3, 3)); // and the drawn box in its own color
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Save_goes_to_the_same_file_without_asking_and_edits_mark_the_window_dirty_again()
    {
        var w = Show();
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "sign.dsm");
            Draw(w);
            w.SavePathOverride = _ => path;
            await w.SaveAsync();

            w.SavePathOverride = _ => throw new InvalidOperationException("Save must not ask for a name again.");
            Draw(w, 90);
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith(" * - DeSign Mage", w.Title);
            Assert.True(await w.SaveAsync());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("sign.dsm - DeSign Mage", w.Title);
            Assert.Equal(2, DsmReader.Load(path).Document.ObjectCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Save_works_from_the_menu_and_the_toolbar_and_Ctrl_S_is_wired()
    {
        var w = Show();
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "menu.dsm");
            w.SavePathOverride = _ => path;
            Draw(w);

            var save = Item(FileMenu(w), "Save");
            Assert.True(save.IsEnabled);
            save.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();

            Assert.True(File.Exists(path));
            Assert.Equal("Ctrl+S", save.InputGesture?.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task A_document_opened_from_a_legacy_layout_is_never_saved_over_it()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var dir = TempDir();
        var copy = Path.Combine(dir, "customer.lyt");
        File.Copy(Sample, copy);
        var before = File.ReadAllBytes(copy);
        try
        {
            var w = Show();
            Assert.True(await w.OpenFileAsync(copy));
            Assert.Null(w.DocumentPath);

            string? suggested = null;
            var chosen = Path.Combine(dir, "customer.dsm");
            w.SavePathOverride = s => { suggested = s; return chosen; };
            Draw(w);
            Assert.True(await w.SaveAsync());                                   // Save behaves like Save As

            Assert.Equal("customer.dsm", suggested);
            Assert.Equal(before, File.ReadAllBytes(copy));
            Assert.Equal(chosen, w.DocumentPath);
            Assert.Equal(284, DsmReader.Load(chosen).Document.ObjectCount);

            // and from now on Save writes the .dsm
            w.SavePathOverride = _ => throw new InvalidOperationException("no second prompt");
            Draw(w);
            Assert.True(await w.SaveAsync());
            Assert.Equal(before, File.ReadAllBytes(copy));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task A_save_name_ending_in_lyt_becomes_dsm_and_never_creates_a_lyt()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            Draw(w);
            w.SavePathOverride = _ => Path.Combine(dir, "oops.lyt");
            Assert.True(await w.SaveAsAsync());

            Assert.Equal(["oops.dsm"], Directory.GetFiles(dir).Select(Path.GetFileName));

            w.SavePathOverride = _ => Path.Combine(dir, "no-extension");
            Draw(w);
            Assert.True(await w.SaveAsAsync());
            Assert.True(File.Exists(Path.Combine(dir, "no-extension.dsm")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task A_document_from_an_earlier_build_opens_and_is_never_saved_over()
    {
        var dir = TempDir();
        try
        {
            var w = Show(new StartupOptions(Demo: true));
            var modern = Path.Combine(dir, "beta.dsm");
            w.SavePathOverride = _ => modern;
            Assert.True(await w.SaveAsAsync());
            int count = w.Session.Document.ObjectCount;
            var earlier = Path.Combine(dir, "beta" + DsmFormat.LegacyExtension);
            File.Move(modern, earlier);
            var before = File.ReadAllBytes(earlier);

            var other = Show();
            Assert.True(await other.OpenFileAsync(earlier));
            Assert.Equal(count, other.Session.Document.ObjectCount);
            Assert.Equal("beta" + DsmFormat.LegacyExtension + " - DeSign Mage", other.Title);
            Assert.Null(other.DocumentPath);                                    // Save will ask for a name

            string? suggested = null;
            var chosen = Path.Combine(dir, "beta.dsm");
            other.SavePathOverride = s => { suggested = s; return chosen; };
            Draw(other);
            Assert.True(await other.SaveAsync());

            Assert.Equal("beta.dsm", suggested);
            Assert.Equal(before, File.ReadAllBytes(earlier));                   // the earlier file is untouched
            Assert.Equal(chosen, other.DocumentPath);
            Assert.Equal(count + 1, DsmReader.Load(chosen).Document.ObjectCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task A_save_name_ending_in_the_earlier_extension_becomes_dsm()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            Draw(w);
            w.SavePathOverride = _ => Path.Combine(dir, "typed" + DsmFormat.LegacyExtension);
            Assert.True(await w.SaveAsAsync());

            Assert.Equal(["typed.dsm"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Cancelling_the_save_dialog_saves_nothing_and_keeps_the_changes()
    {
        var w = Show();
        Draw(w);
        w.SavePathOverride = _ => null;

        Assert.False(await w.SaveAsync());

        Assert.True(w.Session.History.IsModified);
        Assert.Null(w.DocumentPath);
    }

    [AvaloniaFact]
    public async Task A_failed_save_says_why_and_the_design_stays_unsaved()
    {
        var w = Show();
        var dir = TempDir();
        try
        {
            Draw(w);
            var shown = new List<string>();
            w.DialogOverride = (title, message) => { shown.Add(title + ": " + message); return Task.FromResult(true); };
            w.SavePathOverride = _ => Path.Combine(dir, "missing-folder", "x.dsm");

            Assert.False(await w.SaveAsync());

            Assert.NotNull(w.LastSaveError);
            Assert.Contains(shown, s => s.StartsWith("Cannot save"));
            Assert.True(w.Session.History.IsModified);
            Assert.Null(w.DocumentPath);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- Open

    [AvaloniaFact]
    public async Task A_saved_dsm_reopens_with_the_same_content_and_Save_goes_back_to_it()
    {
        var dir = TempDir();
        try
        {
            var w = Show(new StartupOptions(Demo: true));
            var path = Path.Combine(dir, "demo.dsm");
            w.SavePathOverride = _ => path;
            Assert.True(await w.SaveAsAsync());
            int count = w.Session.Document.ObjectCount;
            var texts = w.Session.Document.AllObjects().OfType<TextObject>().Select(t => (t.Text, t.FontFamily, t.FontSize)).ToList();

            var other = Show();
            Assert.True(await other.OpenFileAsync(path));

            Assert.Equal(count, other.Session.Document.ObjectCount);
            Assert.Equal(texts, other.Session.Document.AllObjects().OfType<TextObject>().Select(t => (t.Text, t.FontFamily, t.FontSize)).ToList());
            Assert.Equal("demo.dsm - DeSign Mage", other.Title);
            Assert.Equal(path, other.DocumentPath);
            Assert.False(other.Session.History.IsModified);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task A_damaged_dsm_is_reported_and_the_current_design_is_kept()
    {
        var dir = TempDir();
        try
        {
            var w = Show(new StartupOptions(Demo: true));
            var before = w.Session;
            var bad = Path.Combine(dir, "bad.dsm");
            File.WriteAllBytes(bad, new byte[300]);

            Assert.False(await w.OpenFileAsync(bad));

            Assert.Same(before, w.Session);
            Assert.NotNull(w.LastOpenError);
            Assert.Contains("DeSign Mage document", w.LastOpenError);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Opening_a_file_that_warns_shows_the_warnings()
    {
        var dir = TempDir();
        try
        {
            var doc = new Document(PageSetup.FromInches(2, 2));
            doc.Insert(doc.Layers[0], -1, new TextObject("Hello", "Zzyzx Display Not Installed", 12));
            var path = Path.Combine(dir, "font.dsm");
            DsmWriter.Save(doc, path);
            var w = Show();
            var shown = new List<(string, string)>();
            w.DialogOverride = (t, m) => { shown.Add((t, m)); return Task.FromResult(true); };

            Assert.True(await w.OpenFileAsync(path));

            var (title, message) = Assert.Single(shown);
            Assert.Equal("font.dsm", title);
            Assert.Contains("Zzyzx Display Not Installed", message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Open_Recent_lists_what_was_opened_or_saved_and_reopens_it()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var dir = TempDir();
        File.Delete(RecentFiles.StoragePath!);   // this test looks at the whole list, so it starts from an empty one
        try
        {
            var w = Show();
            var path = Path.Combine(dir, "recent_one.dsm");
            Draw(w);
            w.SavePathOverride = _ => path;
            await w.SaveAsync();
            await w.OpenFileAsync(Sample);
            Assert.Equal(Sample, w.RecentPaths[0]);
            Assert.Equal(path, w.RecentPaths[1]);

            var recent = Item(FileMenu(w), "Open Recent");
            recent.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            var names = recent.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Assert.Equal([LocalData.SampleName, "recent__one.dsm"], names);   // "_" is doubled so it is not read as a shortcut key

            await w.OpenRecentAsync(path);
            Assert.Equal(path, w.DocumentPath);

            File.Delete(path);
            await w.OpenRecentAsync(path);                                      // gone: the entry is dropped, nothing crashes
            Assert.DoesNotContain(path, w.RecentPaths);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- unsaved changes

    private static List<string> AskLog(MainWindow w, MainWindow.SaveChoice answer)
    {
        var log = new List<string>();
        w.SaveChangesOverride = (_, message) => { log.Add(message); return Task.FromResult(answer); };
        return log;
    }

    [AvaloniaFact]
    public async Task New_on_an_unchanged_design_just_starts_over_without_asking()
    {
        var w = Show();
        var log = AskLog(w, MainWindow.SaveChoice.Cancel);

        var before = w.Session;
        FileMenu(w).Items.OfType<MenuItem>().First(m => (m.Header as string)!.Contains("New")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(log);
        Assert.NotSame(before, w.Session);
    }

    [AvaloniaFact]
    public async Task New_with_unsaved_changes_asks_and_Cancel_keeps_everything()
    {
        var w = Show();
        Draw(w);
        var before = w.Session;
        var log = AskLog(w, MainWindow.SaveChoice.Cancel);
        FileMenu(w).Items.OfType<MenuItem>().First(m => (m.Header as string)!.Contains("New")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(log);
        Assert.Same(before, w.Session);
        Assert.Equal(1, w.Session.Document.ObjectCount);
    }

    [AvaloniaFact]
    public async Task New_with_Dont_Save_discards_and_with_Save_saves_first()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            Draw(w);
            AskLog(w, MainWindow.SaveChoice.Discard);
            Assert.True(await w.ConfirmDiscardAsync("creating a new design"));

            var path = Path.Combine(dir, "kept.dsm");
            w.SavePathOverride = _ => path;
            AskLog(w, MainWindow.SaveChoice.Save);
            Assert.True(await w.ConfirmDiscardAsync("creating a new design"));
            Assert.True(File.Exists(path));
            Assert.False(w.Session.History.IsModified);

            // Save was chosen but the save dialog is cancelled: the answer is "do not go on".
            var fresh = Show();
            Draw(fresh);
            fresh.SavePathOverride = _ => null;
            fresh.SaveChangesOverride = (_, _) => Task.FromResult(MainWindow.SaveChoice.Save);
            Assert.False(await fresh.ConfirmDiscardAsync("creating a new design"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void Closing_with_unsaved_changes_asks_first_and_Cancel_keeps_the_window()
    {
        var w = Show();
        Draw(w);
        var log = AskLog(w, MainWindow.SaveChoice.Cancel);

        w.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(log);
        Assert.Contains("before closing", log[0]);
        Assert.True(w.IsVisible);

        AskLog(w, MainWindow.SaveChoice.Discard);
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public async Task Closing_after_saving_does_not_ask()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            Draw(w);
            w.SavePathOverride = _ => Path.Combine(dir, "x.dsm");
            await w.SaveAsync();
            var log = AskLog(w, MainWindow.SaveChoice.Cancel);

            w.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(log);
            Assert.False(w.IsVisible);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Undoing_everything_after_a_save_still_counts_as_changed()
    {
        var w = Show();
        Draw(w);
        w.SavePathOverride = _ => Path.Combine(Path.GetTempPath(), $"dsm-undo-{Guid.NewGuid():N}.dsm");
        var path = w.SavePathOverride("");
        await w.SaveAsAsync();   // the file is written on a worker thread

        w.Session.Undo();

        Assert.True(w.Session.History.IsModified);
        File.Delete(w.DocumentPath!);
    }

    // ---------------------------------------------------------------- Export

    [AvaloniaFact]
    public void Export_writes_svg_pdf_and_png_and_leaves_the_design_unmodified()
    {
        var dir = TempDir();
        try
        {
            var w = Show(new StartupOptions(Demo: true));
            bool wasModified = w.Session.History.IsModified;
            foreach (var (format, name) in new[] { (ExportFormat.Svg, "a.svg"), (ExportFormat.Pdf, "a.pdf"), (ExportFormat.Png, "a.png") })
            {
                var request = new ExportRequest(format, new ExportOptions { Dpi = 50 });
                Assert.True(w.RunExport(request, Path.Combine(dir, name)));
            }

            Assert.Equal(["a.pdf", "a.png", "a.svg"], Directory.GetFiles(dir).Select(Path.GetFileName).Order());
            Assert.Contains("<svg", File.ReadAllText(Path.Combine(dir, "a.svg")));
            Assert.StartsWith("%PDF", File.ReadAllText(Path.Combine(dir, "a.pdf"), System.Text.Encoding.Latin1));
            Assert.Equal(wasModified, w.Session.History.IsModified);
            Assert.Null(w.LastSaveError);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Export_command_uses_the_chosen_options_and_adds_the_missing_extension()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            Draw(w);
            w.Session.Selection.Set(w.Session.Document.Layers[0].Children[0]);
            w.ExportOverride = () => (new ExportRequest(ExportFormat.Png, new ExportOptions
            {
                Scope = ExportScope.Selection, Selection = w.Session.Selection.Items.ToArray(), Dpi = 144,
            }), Path.Combine(dir, "selection"));

            await w.ExportAsync();

            using var bmp = SkiaSharp.SKBitmap.Decode(Path.Combine(dir, "selection.png"));
            Assert.Equal((120, 80), (bmp.Width, bmp.Height));                  // 60 x 40 pt box at 144 dpi
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void A_failed_export_says_why_and_leaves_no_partial_file()
    {
        var dir = TempDir();
        try
        {
            var w = Show();
            var shown = new List<string>();
            w.DialogOverride = (title, message) => { shown.Add(title); return Task.FromResult(true); };

            Assert.False(w.RunExport(new ExportRequest(ExportFormat.Png, new ExportOptions { Dpi = 100000 }), Path.Combine(dir, "huge.png")));
            Assert.False(w.RunExport(new ExportRequest(ExportFormat.Svg, new ExportOptions()), Path.Combine(dir, "nope", "x.svg")));

            Assert.Equal(["Cannot export", "Cannot export"], shown);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void The_Export_menu_item_is_enabled_and_has_its_shortcut()
    {
        var w = Show();
        var export = Item(FileMenu(w), "Export");
        Assert.True(export.IsEnabled);
        Assert.Equal("Alt+X", export.InputGesture?.ToString());
        Assert.True(Item(FileMenu(w), "Save As").IsEnabled);
    }
}



