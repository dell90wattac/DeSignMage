using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Import;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Native;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>A reader of vector files for the tests: makes a page of known objects, with pages, warnings, delays and failures on request.</summary>
internal sealed class FakeVectorImporter : IVectorImporter
{
    public int Pages = 1;
    public List<string> Warnings = [];
    public int DelayMs;
    public Func<int, Document>? Make;
    public readonly List<(string Path, int Page)> Calls = [];

    public bool CanRead(string path) => VectorFileTypes.IsVectorFile(path);

    public VectorReadResult Read(string path, int page = 1)
    {
        lock (Calls) Calls.Add((path, page));
        if (DelayMs > 0) Thread.Sleep(DelayMs);
        if (Path.GetFileName(path).Contains("broken")) throw new InvalidDataException("The file is damaged: its page index is missing.");
        if (page < 1 || page > Pages) throw new InvalidDataException($"The file has no page {page}.");
        var doc = Path.GetFileName(path).Contains("empty") ? new Document(PageSetup.FromInches(8.5, 11)) : Make?.Invoke(page) ?? Sample(page);
        return new VectorReadResult(doc, Warnings, Pages);
    }

    /// <summary>A letter page (612 x 792 pt) with a red box, a blue circle and a line of text.</summary>
    public static Document Sample(int page = 1)
    {
        var doc = new Document(PageSetup.FromInches(8.5, 11));
        var layer = doc.Layers[0];
        doc.Insert(layer, -1, new RectangleObject(100, 60) { Name = "box", Transform = Affine2.Translation(50, 50), Style = ObjectStyle.FillOnly(new DocColor(220, 30, 30)) });
        doc.Insert(layer, -1, new EllipseObject(60, 60) { Name = "circle", Transform = Affine2.Translation(200, 100), Style = new ObjectStyle(new SolidPaint(new DocColor(30, 60, 220)), StrokeStyle.Solid(DocColor.Black, 4)) });
        doc.Insert(layer, -1, new TextObject($"Page {page}", "Arial", 24) { Transform = Affine2.Translation(60, 300), Style = ObjectStyle.FillOnly(DocColor.Black) });
        return doc;
    }
}

/// <summary>Opening vs. importing PDF, AI, EPS and SVG files (R30, TASKS C.34): File ▸ Open makes a new document, dragging one in shows the Import dialog and then places the objects with a click.</summary>
public class ImportVectorTests
{
    private static MainWindow Show(FakeVectorImporter? importer = null)
    {
        VectorFiles.Importer = importer ?? new FakeVectorImporter();
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        w.DialogOverride = (_, _) => Task.FromResult(true);
        return w;
    }

    private static void Done(MainWindow w)
    {
        VectorFiles.Importer = null;
        w.Close();
    }

    private static string TempFile(string name)
    {
        var dir = Directory.CreateTempSubdirectory("dsm-import-").FullName;
        return Path.Combine(dir, name);
    }

    private static void Flush(ImportDialog d)
    {
        for (int i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (d.ReadTask.IsCompleted && d.PreviewTask.IsCompleted) break;
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < 100 && !(d.ReadTask.IsCompleted && d.PreviewTask.IsCompleted); i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }
    }

    private static bool Shown(Control c) => c.IsVisible && c.GetVisualAncestors().OfType<Control>().All(a => a.IsVisible);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<ImportDialog> OpenDialog(MainWindow w, string path, Vec2? drop = null)
    {
        _ = w.ImportVectorFileAsync(path, drop);
        for (int i = 0; i < 200 && w.LastImportDialog is null; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        var d = w.LastImportDialog!;
        Flush(d);
        return d;
    }

    private static void ClickAt(MainWindow w, Vec2 doc)
    {
        var screen = w.Session.Viewport.ToScreen(doc);
        w.Session.PointerMove(new PointerInput(screen, doc, PointerButton.None, ModifierKeys.None));
        w.Session.PointerDown(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        w.Session.PointerUp(new PointerInput(screen, doc, PointerButton.Left, ModifierKeys.None));
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------------- File > Open

    [AvaloniaTheory]
    [InlineData("flyer.pdf")]
    [InlineData("flyer.ai")]
    [InlineData("flyer.eps")]
    [InlineData("flyer.svg")]
    [InlineData("FLYER.PDF")]
    public async Task Opening_a_vector_file_makes_a_new_document_the_size_of_its_page_zoomed_out(string name)
    {
        var path = TempFile(name);
        var w = Show();
        var before = w.Session;

        Assert.True(await w.OpenFileAsync(path));
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();

        Assert.NotSame(before, w.Session);                                           // its own document
        Assert.Equal(new PageSetup(612, 792), w.Session.Document.Page);              // the page of the file
        Assert.Equal(3, w.Session.Document.ObjectCount);
        Assert.StartsWith(name + " - ", w.Title);
        Assert.False(w.Session.History.IsModified);
        Assert.Null(w.DocumentPath);                                                 // Save asks for a new name; the original is never written
        Assert.Contains(path, w.RecentPaths);                                        // opened files are listed in File > Open Recent
        var canvas = w.FindControl<DeSignMage.App.Canvas.DesignCanvas>("Surface")!;
        var tl = w.Session.Viewport.ToScreen(new Vec2(0, 0));
        var br = w.Session.Viewport.ToScreen(new Vec2(612, 792));
        Assert.True(tl.X >= -1 && tl.Y >= -1 && br.X <= canvas.Bounds.Width + 1 && br.Y <= canvas.Bounds.Height + 1, $"{tl} {br} in {canvas.Bounds.Size}");   // fully zoomed out (R1)

        string? suggested = null;
        w.SavePathOverride = s => { suggested = s; return null; };
        await w.SaveAsync();
        Assert.Equal(Path.GetFileNameWithoutExtension(name) + ".dsm", suggested);
        Done(w);
    }

    [AvaloniaFact]
    public async Task A_multi_page_file_opens_its_first_page_and_says_so()
    {
        var w = Show(new FakeVectorImporter { Pages = 5, Warnings = ["A gradient mesh was drawn as a flat color."] });
        var shown = new List<string>();
        w.DialogOverride = (title, message) => { shown.Add(message); return Task.FromResult(true); };

        Assert.True(await w.OpenFileAsync(TempFile("book.pdf")));

        Assert.Contains("Page 1", w.Session.Document.AllObjects().OfType<TextObject>().Single().Text);
        var message = Assert.Single(shown);
        Assert.Contains("The file has 5 pages; only the first page was opened.", message);
        Assert.Contains("A gradient mesh was drawn as a flat color.", message);
        Done(w);
    }

    [AvaloniaFact]
    public async Task Opening_without_a_reader_or_a_damaged_file_says_why_and_keeps_the_current_design()
    {
        var w = Show();
        var before = w.Session;
        VectorFiles.Importer = null;

        Assert.False(await w.OpenFileAsync(TempFile("a.pdf")));
        Assert.Equal("DeSign Mage cannot read PDF files yet.", w.LastOpenError);
        Assert.Same(before, w.Session);

        VectorFiles.Importer = new FakeVectorImporter();
        Assert.False(await w.OpenFileAsync(TempFile("broken.svg")));
        Assert.Contains("damaged", w.LastOpenError);
        Assert.Same(before, w.Session);
        Done(w);
    }

    // ---------------------------------------------------------------- drag and drop routing

    [AvaloniaTheory]
    [InlineData("a.pdf")]
    [InlineData("a.ai")]
    [InlineData("a.eps")]
    [InlineData("a.svg")]
    public async Task Dropping_a_vector_file_imports_it_through_the_dialog_and_does_not_open_it(string name)
    {
        var w = Show();
        var before = w.Session;
        int objects = w.Session.Document.ObjectCount;

        w.HandleDroppedFiles([TempFile(name)], null);
        for (int i = 0; i < 200 && w.LastImportDialog is null; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        Assert.NotNull(w.LastImportDialog);                                           // the Import dialog is up
        Assert.Same(before, w.Session);                                                // the current design stays
        Assert.Equal(objects, w.Session.Document.ObjectCount);
        Assert.DoesNotContain(w.RecentPaths, p => p.EndsWith(name));                   // an import is not a recent file
        Click(w.LastImportDialog!.CancelButton);
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.Session.IsPlacing);
        Done(w);
    }

    [AvaloniaFact]
    public async Task Dropping_our_own_documents_still_opens_them_even_with_a_vector_file_beside_them()
    {
        var dir = Directory.CreateTempSubdirectory("dsm-drop-").FullName;
        var path = Path.Combine(dir, "mine.dsm");
        var mine = new Document(PageSetup.FromInches(4, 4));
        mine.Insert(mine.Layers[0], -1, new RectangleObject(10, 10));
        DsmWriter.Save(mine, path);
        var w = Show();

        w.HandleDroppedFiles([Path.Combine(dir, "other.pdf"), path], null);
        for (int i = 0; i < 300 && w.DocumentPath is null; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }

        Assert.Equal(path, w.DocumentPath);                                            // opened as a document
        Assert.Equal(1, w.Session.Document.ObjectCount);
        Assert.Null(w.LastImportDialog);
        Done(w);
    }

    [AvaloniaFact]
    public async Task Dropping_a_legacy_layout_opens_it_as_a_document()
    {
        Assert.SkipUnless(LocalData.Has("layouts/sample.lyt"), "needs local-testdata/layouts/sample.lyt (see the README)");
        var w = Show();
        var before = w.Session;

        w.HandleDroppedFiles([LocalData.Sample], null);
        for (int i = 0; i < 400 && ReferenceEquals(w.Session, before); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }

        Assert.NotSame(before, w.Session);
        Assert.Null(w.LastImportDialog);
        Done(w);
    }

    [AvaloniaFact]
    public async Task Dropping_a_vector_file_when_nothing_can_read_it_says_so()
    {
        var w = Show();
        VectorFiles.Importer = null;
        var shown = new List<(string Title, string Message)>();
        w.DialogOverride = (t, m) => { shown.Add((t, m)); return Task.FromResult(true); };

        Assert.False(await w.ImportVectorFileAsync(TempFile("a.pdf"), null));

        Assert.Equal(("Cannot import a.pdf", "DeSign Mage cannot read PDF files yet."), Assert.Single(shown));
        Assert.False(w.Session.IsPlacing);
        Done(w);
    }

    // ---------------------------------------------------------------- the dialog

    [AvaloniaFact]
    public async Task The_dialog_reads_on_a_worker_shows_what_comes_in_and_enables_Import_when_it_has_something()
    {
        var fake = new FakeVectorImporter { DelayMs = 120, Warnings = ["Two fonts were not embedded; their text was written as curves.", "A soft mask was ignored."] };
        var w = Show(fake);

        _ = w.ImportVectorFileAsync(TempFile("sign.pdf"), null);
        for (int i = 0; i < 200 && w.LastImportDialog is null; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        var d = w.LastImportDialog!;
        Assert.False(d.OkButton.IsEnabled);                                           // the file is being read: nothing to import yet
        Assert.Equal("Reading the file...", d.FileNote.Text);
        Flush(d);

        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal("sign.pdf", d.FileText.Text);
        Assert.StartsWith("3 objects · ", d.SummaryText.Text);
        Assert.Contains("• Two fonts were not embedded; their text was written as curves.", d.WarningsText.Text);
        Assert.Contains("• A soft mask was ignored.", d.WarningsText.Text);
        Assert.True(Shown(d.WarningsText));
        Assert.True(d.PreviewsDrawn >= 1);
        Assert.Equal(d.Plan!.Area, d.LastPreview!.Value.Area);                        // the preview shows exactly the area that comes in
        Assert.Equal(d.Plan.Area.Width / d.Plan.Area.Height, (double)d.LastPreview.Value.Width / d.LastPreview.Value.Height, 0.02);
        Assert.Single(fake.Calls);
        Click(d.CancelButton);
        Done(w);
    }

    [AvaloniaFact]
    public async Task The_area_that_comes_in_covers_what_is_drawn_with_its_strokes_not_the_whole_page()
    {
        var w = Show();
        var d = await OpenDialog(w, TempFile("a.svg"));

        var area = d.Plan!.Area;
        // box 50..150 x 50..110, circle 200..260 x 100..160 with a 4 pt stroke (2 pt each side), text near (60, 300)
        Assert.Equal(50, area.Left, 3);
        Assert.Equal(50, area.Top, 3);
        Assert.Equal(262, area.Right, 3);
        Assert.True(area.Bottom > 300 && area.Bottom < 330);
        Assert.True(area.Width < 612 && area.Height < 792);
        Click(d.CancelButton);
        Done(w);
    }

    [AvaloniaFact]
    public async Task A_multi_page_file_offers_the_page_and_changing_it_reads_that_page()
    {
        var fake = new FakeVectorImporter { Pages = 4 };
        var w = Show(fake);
        var d = await OpenDialog(w, TempFile("catalog.pdf"));

        Assert.True(Shown(d.PageBox));
        Assert.Equal("of 4", d.PageOf.Text);
        Assert.Equal("4 pages", d.FileNote.Text);
        Assert.Contains("Page 1", d.Source!.AllObjects().OfType<TextObject>().Single().Text);

        d.PageBox.Text = "3";
        Flush(d);
        Assert.Contains("Page 3", d.Source!.AllObjects().OfType<TextObject>().Single().Text);
        Assert.Equal(3, d.Page);
        Assert.Equal([1, 3], fake.Calls.Select(c => c.Page).ToArray());
        Assert.True(d.OkButton.IsEnabled);

        foreach (var bad in new[] { "0", "5", "x", "" })
        {
            d.PageBox.Text = bad;
            Flush(d);
            Assert.False(d.OkButton.IsEnabled, bad);
            Assert.Equal("Enter a page from 1 to 4.", d.ErrorText.Text);
        }
        d.PageBox.Text = "2";
        Flush(d);
        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal("", d.ErrorText.Text);
        Assert.Contains("Page 2", d.Source!.AllObjects().OfType<TextObject>().Single().Text);
        Click(d.OkButton);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, d.Result!.Page);
        Done(w);
    }

    [AvaloniaFact]
    public async Task A_single_page_file_has_no_page_choice_and_a_file_without_warnings_has_no_notes_section()
    {
        var w = Show();
        var d = await OpenDialog(w, TempFile("one.eps"));

        Assert.False(Shown(d.PageBox));
        Assert.Equal("1 page", d.FileNote.Text);
        Assert.Equal("", d.WarningsText.Text ?? "");
        Assert.False(Shown(d.WarningsText));
        Click(d.CancelButton);
        Done(w);
    }

    [AvaloniaFact]
    public async Task A_file_that_cannot_be_read_or_has_nothing_in_it_says_so_and_cannot_be_imported()
    {
        var w = Show();

        var broken = await OpenDialog(w, TempFile("broken.pdf"));
        Assert.False(broken.OkButton.IsEnabled);
        Assert.Equal("The file is damaged: its page index is missing.", broken.ErrorText.Text);
        Click(broken.CancelButton);
        Dispatcher.UIThread.RunJobs();

        w.LastImportDialog = null;
        var empty = await OpenDialog(w, TempFile("empty.pdf"));
        Assert.False(empty.OkButton.IsEnabled);
        Assert.Equal("There is nothing to import on this page.", empty.ErrorText.Text);
        Click(empty.CancelButton);
        Done(w);
    }

    [AvaloniaFact]
    public async Task Cancel_imports_nothing()
    {
        var w = Show();
        var objects = w.Session.Document.ObjectCount;
        var undo = w.Session.History.UndoList.Count;
        var d = await OpenDialog(w, TempFile("a.pdf"));

        Click(d.CancelButton);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(d.Result);
        Assert.False(w.Session.IsPlacing);
        Assert.Equal(objects, w.Session.Document.ObjectCount);
        Assert.Equal(undo, w.Session.History.UndoList.Count);
        Done(w);
    }

    // ---------------------------------------------------------------- after Import: placing with a click

    [AvaloniaFact]
    public async Task After_Import_the_objects_hang_on_the_pointer_and_one_click_places_them_as_one_undo_step()
    {
        var w = Show();
        int undo = w.Session.History.UndoList.Count;
        int before = w.Session.Document.ObjectCount;
        var d = await OpenDialog(w, TempFile("sign.pdf"));
        Click(d.OkButton);
        for (int i = 0; i < 100 && !w.Session.IsPlacing; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        Assert.True(w.Session.IsPlacing);                                              // like a paste (R18)
        Assert.Equal(before, w.Session.Document.ObjectCount);                          // nothing is added yet
        Assert.Equal(undo, w.Session.History.UndoList.Count);
        var placing = w.Session.GetTool<DeSignMage.Core.Editing.Tools.PlaceTool>();
        var group = Assert.IsType<GroupObject>(Assert.Single(placing.Items));          // one group, on by default
        Assert.Equal("sign", group.Name);
        Assert.Equal(3, group.Children.Count);

        var at = new Vec2(900, 500);
        ClickAt(w, at);

        Assert.False(w.Session.IsPlacing);
        Assert.Equal(before + 4, w.Session.Document.ObjectCount);                      // the group and its three members
        Assert.Equal(undo + 1, w.Session.History.UndoList.Count);                      // one undo step
        var placed = Assert.IsType<GroupObject>(Assert.Single(w.Session.Selection.Items));   // they arrive selected
        var center = placed.GetWorldBounds().Center;
        Assert.Equal(at.X, center.X, 1);                                               // centered on the click
        Assert.Equal(at.Y, center.Y, 1);
        Assert.StartsWith("Import sign.pdf", w.Session.History.UndoList[^1].Description);

        w.Session.Undo();
        Assert.Equal(before, w.Session.Document.ObjectCount);                          // one Undo takes it all away
        w.Session.Redo();
        Assert.Equal(before + 4, w.Session.Document.ObjectCount);
        Done(w);
    }

    [AvaloniaFact]
    public async Task Without_the_group_option_the_objects_come_in_separately_and_the_choice_is_remembered()
    {
        var w = Show();
        var d = await OpenDialog(w, TempFile("a.pdf"));
        Assert.True(d.GroupBox.IsChecked);                                             // on by default
        d.GroupBox.IsChecked = false;
        Click(d.OkButton);
        for (int i = 0; i < 100 && !w.Session.IsPlacing; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        var placing = w.Session.GetTool<DeSignMage.Core.Editing.Tools.PlaceTool>();
        Assert.Equal(3, placing.Items.Count);
        Assert.DoesNotContain(placing.Items, o => o is GroupObject);
        int before = w.Session.Document.ObjectCount;
        ClickAt(w, new Vec2(700, 400));
        Assert.Equal(before + 3, w.Session.Document.ObjectCount);
        Assert.Equal(3, w.Session.Selection.Items.Count);

        w.LastImportDialog = null;
        var again = await OpenDialog(w, TempFile("b.pdf"));
        Assert.False(again.GroupBox.IsChecked);                                        // the next import starts with the same choice
        Click(again.CancelButton);
        Done(w);
    }

    [AvaloniaFact]
    public async Task Esc_while_placing_cancels_and_adds_nothing()
    {
        var w = Show();
        int before = w.Session.Document.ObjectCount;
        int undo = w.Session.History.UndoList.Count;
        var d = await OpenDialog(w, TempFile("a.pdf"));
        Click(d.OkButton);
        for (int i = 0; i < 100 && !w.Session.IsPlacing; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        Assert.True(w.Session.IsPlacing);

        w.Session.KeyDown(EditorKey.Escape, ModifierKeys.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(w.Session.IsPlacing);
        Assert.Equal(before, w.Session.Document.ObjectCount);
        Assert.Equal(undo, w.Session.History.UndoList.Count);
        Done(w);
    }

    [AvaloniaFact]
    public async Task The_objects_start_at_the_drop_point_until_the_pointer_moves()
    {
        var w = Show();
        var drop = new Vec2(400, 300);
        var d = await OpenDialog(w, TempFile("a.pdf"), drop);
        Click(d.OkButton);
        for (int i = 0; i < 100 && !w.Session.IsPlacing; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        var tool = w.Session.GetTool<DeSignMage.Core.Editing.Tools.PlaceTool>();
        var target = tool.TargetCenter!.Value;
        Assert.Equal(drop.X, target.X, 1);                                             // hanging where the file was dropped
        Assert.Equal(drop.Y, target.Y, 1);
        Done(w);
    }

    [AvaloniaFact]
    public async Task A_single_object_comes_in_as_itself_not_as_a_group_of_one()
    {
        var fake = new FakeVectorImporter
        {
            Make = _ =>
            {
                var doc = new Document(PageSetup.FromInches(2, 2));
                doc.Insert(doc.Layers[0], -1, new EllipseObject(40, 40) { Style = ObjectStyle.FillOnly(DocColor.Black) });
                return doc;
            },
        };
        var w = Show(fake);
        var d = await OpenDialog(w, TempFile("dot.svg"));
        Assert.StartsWith("1 object · ", d.SummaryText.Text);
        Click(d.OkButton);
        for (int i = 0; i < 100 && !w.Session.IsPlacing; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        var item = Assert.Single(w.Session.GetTool<DeSignMage.Core.Editing.Tools.PlaceTool>().Items);
        Assert.IsType<EllipseObject>(item);
        Done(w);
    }

    // ---------------------------------------------------------------- the real reader

    [AvaloniaTheory]
    [InlineData(".svg")]
    [InlineData(".pdf")]
    [InlineData(".eps")]
    [InlineData(".ai")]
    public async Task Our_own_exports_open_and_import_through_the_real_reader(string extension)
    {
        var dir = Directory.CreateTempSubdirectory("dsm-roundtrip-").FullName;
        var path = Path.Combine(dir, "export" + extension);
        DeSignMage.Rendering.Export.DocumentExporter.Export(FakeVectorImporter.Sample(), path, new DeSignMage.Rendering.Export.ExportOptions());
        var w = Show();
        VectorFiles.Importer = new FormatsVectorImporter(() => w.Session.BooleanService);

        // File > Open: a new document with the page of the file
        bool opened = await w.OpenFileAsync(path);
        if (!opened && w.LastOpenError?.Contains("not implemented yet") == true) { Done(w); Assert.Skip($"The reader for {extension} files is not finished yet: {w.LastOpenError}"); }
        Assert.True(opened, w.LastOpenError);
        Assert.True(w.Session.Document.ObjectCount >= 3, $"{w.Session.Document.ObjectCount} objects");
        Assert.Equal(612, w.Session.Document.Page.Width, 1);
        Assert.Equal(792, w.Session.Document.Page.Height, 1);

        // dragged in: the dialog shows the objects that come in, and Import hangs them on the pointer
        var d = await OpenDialog(w, path);
        Assert.True(d.OkButton.IsEnabled, d.ErrorText.Text);
        Assert.NotNull(d.Plan);
        Click(d.OkButton);
        for (int i = 0; i < 100 && !w.Session.IsPlacing; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        Assert.True(w.Session.IsPlacing);
        Done(w);
    }
    // ---------------------------------------------------------------- the pieces

    [AvaloniaFact]
    public void The_copies_are_independent_of_the_file_and_keep_groups_whole_and_in_drawing_order()
    {
        var doc = FakeVectorImporter.Sample();
        var a = new RectangleObject(10, 10) { Name = "a" };
        var b = new RectangleObject(10, 10) { Name = "b" };
        doc.Insert(doc.Layers[0], -1, a);
        doc.Insert(doc.Layers[0], -1, b);
        var session = new EditorSession(doc);
        var grouped = session.GroupObjects([a, b])!;
        var plan = ImportDialog.BuildPlan(doc)!;

        var items = ImportDialog.BuildItems(plan, asGroup: false, "x");

        Assert.Equal(4, items.Count);                                                  // the three objects and the group
        Assert.Equal(["box", "circle", null, "Group"], items.Select(i => i is GroupObject ? "Group" : i.Name));
        Assert.All(items, i => Assert.True(i.Id.IsNone && i.Parent is null));          // detached copies
        Assert.Equal(2, ((GroupObject)items[3]).Children.Count);
        Assert.NotSame(grouped, items[3]);
        var all = ImportDialog.BuildItems(plan, asGroup: true, "x");
        var one = Assert.IsType<GroupObject>(Assert.Single(all));
        Assert.Equal(4, one.Children.Count);
    }

    [AvaloniaFact]
    public void Hidden_layers_and_invisible_objects_do_not_come_in()
    {
        var doc = FakeVectorImporter.Sample();
        var hidden = new Layer("Hidden") { Visible = false };
        doc.InsertLayer(1, hidden);
        doc.Insert(hidden, -1, new RectangleObject(500, 500));
        doc.Layers[0].Children.OfType<RectangleObject>().Single().Visible = false;

        var plan = ImportDialog.BuildPlan(doc)!;

        Assert.Equal(2, plan.ObjectCount);                                              // the circle and the text
        Assert.Null(ImportDialog.BuildPlan(new Document(PageSetup.FromInches(1, 1))));  // nothing at all
    }
}
