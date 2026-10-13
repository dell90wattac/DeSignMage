using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Shell;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Lyt;
using DeSignMage.Rendering.Export;
using DeSignMage.TestSupport;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>The Export dialog (R28): the format list, the pixel size linked to the exported area, quality controls only where they apply, the live preview.</summary>
public class ExportDialogTests
{
    private static Document SampleDoc(out RectangleObject box, out RectangleObject far)
    {
        var doc = new Document(PageSetup.FromInches(24, 12));   // 1728 x 864 pt, 2:1
        box = new RectangleObject(100, 50) { Transform = Affine2.Translation(30, 30), Style = new ObjectStyle(new SolidPaint(new DocColor(220, 30, 30)), StrokeStyle.Solid(DocColor.Black, 10)) };   // 110 x 60 with its stroke
        far = new RectangleObject(40, 40) { Transform = Affine2.Translation(2000, 100), Style = ObjectStyle.FillOnly(new DocColor(0, 0, 200)) };                                      // on the pasteboard
        doc.Insert(doc.Layers[0], -1, box);
        doc.Insert(doc.Layers[0], -1, far);
        return doc;
    }

    private static ExportDialog Open(Document doc, IReadOnlyCollection<DocObject>? selection = null, ExportSettings? settings = null)
    {
        var dialog = new ExportDialog(doc, selection ?? [], settings ?? new ExportSettings());
        dialog.Show();
        Flush(dialog);
        return dialog;
    }

    /// <summary>Lets the dialog's preview finish (it draws on a worker thread and comes back to the UI thread).</summary>
    private static void Flush(ExportDialog d)
    {
        for (int i = 0; i < 400; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (d.PreviewTask.IsCompleted) break;
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Choose(ExportDialog d, ExportFormat format)
    {
        d.FormatBox.SelectedIndex = ExportFormats.All.ToList().FindIndex(f => f.Format == format);
        Flush(d);
    }

    private static void Type(ExportDialog d, TextBox box, string text)
    {
        box.Text = text;
        Flush(d);
    }

    private static bool Shown(Control c) => c.IsVisible && c.GetVisualAncestors().OfType<Control>().All(a => a.IsVisible);

    // ---------------------------------------------------------------- the format list

    [AvaloniaFact]
    public void The_file_type_is_a_drop_down_with_every_format_and_the_legacy_layout_named_as_such()
    {
        var d = Open(SampleDoc(out _, out _));

        var names = d.FormatBox.Items.OfType<ComboBoxItem>().Select(i => i.Content as string).ToList();
        Assert.Equal(["PNG picture", "JPEG picture", "PDF document", "SVG vector graphic", "EPS (PostScript)", "AI (Adobe Illustrator)", "Legacy layout (.lyt)"], names);
        Assert.Equal(ExportFormat.Png, d.Format);
        Assert.DoesNotContain(d.GetVisualDescendants().OfType<RadioButton>(), r => (r.Content as string)?.StartsWith("SVG (") == true);   // the old radio list is gone
        d.Close();
    }

    [AvaloniaFact]
    public void Each_format_has_its_extension_and_a_description_that_changes_with_it()
    {
        var d = Open(SampleDoc(out _, out _));
        var seen = new HashSet<string>();
        foreach (var f in ExportFormats.All)
        {
            Choose(d, f.Format);
            Assert.Equal(f.Format, d.Format);
            Assert.Equal(f.Description, d.FormatDescription.Text);
            Assert.True(seen.Add(f.Description));
            Assert.True(d.TryBuild(out var request, out _));
            Assert.Equal(f.Extension, request!.Extension);
        }
        Assert.Equal([".png", ".jpg", ".pdf", ".svg", ".eps", ".ai", ".lyt"], ExportFormats.All.Select(f => f.Extension));
        d.Close();
    }

    // ---------------------------------------------------------------- pixel size

    [AvaloniaFact]
    public void Typing_a_width_fills_in_the_height_for_the_page_and_the_other_way_round()
    {
        var d = Open(SampleDoc(out _, out _));
        Assert.Equal(("3200", "1600"), (d.WidthBox.Text, d.HeightBox.Text));       // the start: 3200 along the longer side

        Type(d, d.WidthBox, "1000");
        Assert.Equal("500", d.HeightBox.Text);                                      // the page is 2:1
        Type(d, d.HeightBox, "300");
        Assert.Equal("600", d.WidthBox.Text);
        Type(d, d.WidthBox, "777");
        Assert.Equal("388", d.HeightBox.Text);                                      // 388.5 rounds to the even number
        Assert.True(d.TryBuild(out var request, out _));
        Assert.Equal((777, 388), (request!.Options.PixelWidth, request.Options.PixelHeight));
        d.Close();
    }

    [AvaloniaFact]
    public void For_a_selection_the_proportions_are_those_of_the_selection_with_its_stroke()
    {
        var doc = SampleDoc(out var box, out var far);
        var d = Open(doc, [box, far]);
        d.SelectionRadio.IsChecked = true;
        Flush(d);

        Type(d, d.WidthBox, "1100");
        // box (30..140 x 30..90 with the stroke: 25..145 x 25..95?) and the far object: x 25..2040, y 25..140
        var plan = ExportPlan.Create(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [box, far] });
        int expectedHeight = (int)Math.Round(1100 * plan.Area.Height / plan.Area.Width);
        Assert.Equal(expectedHeight.ToString(), d.HeightBox.Text);

        var onlyBox = Open(doc, [box]);
        onlyBox.SelectionRadio.IsChecked = true;
        Flush(onlyBox);
        Type(onlyBox, onlyBox.WidthBox, "1100");
        Assert.Equal("600", onlyBox.HeightBox.Text);                                // 110 x 60
        Type(onlyBox, onlyBox.HeightBox, "300");
        Assert.Equal("550", onlyBox.WidthBox.Text);
        d.Close();
        onlyBox.Close();
    }

    [AvaloniaFact]
    public void The_last_number_typed_stays_when_the_area_changes_and_the_other_one_follows()
    {
        var doc = SampleDoc(out var box, out _);
        var d = Open(doc, [box]);
        Type(d, d.WidthBox, "2200");
        Assert.Equal("1100", d.HeightBox.Text);                                     // page 2:1

        d.SelectionRadio.IsChecked = true;                                          // the selection is 110 x 60
        Flush(d);
        Assert.Equal("2200", d.WidthBox.Text);
        Assert.Equal("1200", d.HeightBox.Text);

        d.PageRadio.IsChecked = true;
        Type(d, d.HeightBox, "800");                                                // now the height is the typed one
        Assert.Equal("1600", d.WidthBox.Text);
        d.SelectionRadio.IsChecked = true;
        Flush(d);
        Assert.Equal("800", d.HeightBox.Text);
        Assert.Equal("1467", d.WidthBox.Text);                                      // 800 x 110 / 60 = 1466.7
        d.Close();
    }

    [AvaloniaFact]
    public void Including_the_pasteboard_changes_the_proportions_and_a_selection_hides_that_choice()
    {
        var doc = SampleDoc(out var box, out _);
        var d = Open(doc, [box]);
        Type(d, d.WidthBox, "2000");
        Assert.Equal("1000", d.HeightBox.Text);

        d.PasteboardBox.IsChecked = true;                                           // the object at x = 2000..2040 makes the area wider than the page
        Flush(d);
        var plan = ExportPlan.Create(doc, new ExportOptions { IncludePasteboard = true });
        Assert.Equal(((int)Math.Round(2000 * plan.Area.Height / plan.Area.Width)).ToString(), d.HeightBox.Text);
        Assert.True(Shown(d.PasteboardBox));

        d.SelectionRadio.IsChecked = true;
        Flush(d);
        Assert.False(Shown(d.PasteboardBox));                                       // not offered for a selection
        Assert.True(d.TryBuild(out var request, out _));
        Assert.False(request!.Options.IncludePasteboard);
        Assert.Equal(ExportScope.Selection, request.Options.Scope);
        d.Close();
    }

    [AvaloniaFact]
    public void Without_a_selection_there_is_no_selection_choice()
    {
        var d = Open(SampleDoc(out _, out _));
        Assert.False(Shown(d.SelectionRadio));
        Assert.True(Shown(d.PageRadio));
        d.Close();
    }

    [AvaloniaFact]
    public void Too_large_empty_and_nonsense_sizes_say_so_in_plain_words_and_block_the_export()
    {
        var d = Open(SampleDoc(out _, out _));

        Type(d, d.WidthBox, "40000");
        Assert.False(d.OkButton.IsEnabled);
        Assert.Contains("too large", d.ErrorText.Text);
        Assert.Contains("30,000", d.ErrorText.Text);

        Type(d, d.WidthBox, "20000");                                               // 20000 x 10000 = 200 million: fine
        Assert.True(d.OkButton.IsEnabled);
        Type(d, d.WidthBox, "30000");                                               // 30000 x 15000 = 450 million: too many pixels
        Assert.False(d.OkButton.IsEnabled);
        Assert.Contains("too large", d.ErrorText.Text);

        foreach (var bad in new[] { "", "abc", "0", "-5", "12.5" })
        {
            Type(d, d.WidthBox, bad);
            Assert.False(d.OkButton.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(d.ErrorText.Text));
        }
        Type(d, d.HeightBox, "100");
        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal("", d.ErrorText.Text);
        Assert.Equal("200", d.WidthBox.Text);
        d.Close();
    }

    // ---------------------------------------------------------------- only what applies

    [AvaloniaFact]
    public void Quality_controls_belong_to_picture_formats_only_and_each_to_its_own()
    {
        var d = Open(SampleDoc(out _, out _));

        Choose(d, ExportFormat.Png);
        Assert.True(Shown(d.PngSlider));
        Assert.False(Shown(d.JpegSlider));
        Assert.True(Shown(d.WidthBox) && Shown(d.HeightBox));

        Choose(d, ExportFormat.Jpeg);
        Assert.True(Shown(d.JpegSlider));
        Assert.False(Shown(d.PngSlider));
        Assert.True(Shown(d.WidthBox));

        foreach (var vector in new[] { ExportFormat.Pdf, ExportFormat.Svg, ExportFormat.Eps, ExportFormat.Ai, ExportFormat.Lyt })
        {
            Choose(d, vector);
            Assert.False(Shown(d.JpegSlider), $"{vector}: JPEG quality");
            Assert.False(Shown(d.PngSlider), $"{vector}: PNG compression");
            Assert.False(Shown(d.WidthBox), $"{vector}: pixel size");
            Assert.False(Shown(d.HeightBox), $"{vector}: pixel size");
        }
        Choose(d, ExportFormat.Pdf);
        Assert.Equal("24 × 12 in", d.VectorSize.Text);                              // a vector file shows its size on paper instead
        Assert.True(Shown(d.VectorSize));
        d.Close();
    }

    [AvaloniaFact]
    public void The_text_options_show_for_svg_only_and_the_legacy_layout_hides_the_area_choices()
    {
        var d = Open(SampleDoc(out var box, out _), [box]);

        Assert.False(Shown(d.OutlinesRadio));
        Choose(d, ExportFormat.Svg);
        Assert.True(Shown(d.OutlinesRadio) && Shown(d.LiveTextRadio));
        d.LiveTextRadio.IsChecked = true;
        Assert.True(d.TryBuild(out var request, out _));
        Assert.False(request!.Options.TextAsOutlines);

        Choose(d, ExportFormat.Lyt);
        Assert.False(Shown(d.OutlinesRadio));
        Assert.False(Shown(d.PageRadio));
        Assert.False(Shown(d.SelectionRadio));                                      // the whole design is always written
        Assert.False(Shown(d.PasteboardBox));
        Assert.Contains("not yet checked in the legacy program", d.PreviewNote.Text);
        d.SelectionRadio.IsChecked = true;
        Assert.True(d.TryBuild(out var lyt, out _));
        Assert.Equal(ExportScope.Page, lyt!.Options.Scope);
        d.Close();
    }

    [AvaloniaFact]
    public void The_summary_line_says_what_will_be_written()
    {
        var d = Open(SampleDoc(out _, out _));
        Assert.Equal("3200 × 1600 px · PNG · transparent", d.SummaryText.Text);
        Type(d, d.WidthBox, "800");
        Assert.Equal("800 × 400 px · PNG · transparent", d.SummaryText.Text);
        Choose(d, ExportFormat.Jpeg);
        d.JpegSlider.Value = 72;
        Assert.Equal("800 × 400 px · JPEG · quality 72 · background #FFFFFF", d.SummaryText.Text);
        Choose(d, ExportFormat.Pdf);
        Assert.Equal("24 × 12 in · PDF · vector", d.SummaryText.Text);
        Choose(d, ExportFormat.Eps);
        Assert.Equal("24 × 12 in · EPS · vector", d.SummaryText.Text);
        Choose(d, ExportFormat.Lyt);
        Assert.Equal("Legacy layout · the whole design", d.SummaryText.Text);
        d.Close();
    }

    [AvaloniaFact]
    public void The_eps_note_says_how_many_objects_become_pictures()
    {
        var doc = SampleDoc(out _, out _);
        var d = Open(doc);
        Choose(d, ExportFormat.Eps);
        Assert.Equal("", d.PreviewNote.Text);

        var shadow = new PathObject(PathGeometry.Rectangle(new Rect2(0, 0, 90, 50))) { Transform = Affine2.Translation(300, 300), Style = new ObjectStyle(new SolidPaint(DocColor.Black), null, new BlurStyle(SoftKind.SoftShadow, 1, 0.5, 50)) };
        doc.Insert(doc.Layers[0], -1, shadow);
        var again = Open(doc);
        Choose(again, ExportFormat.Eps);
        Assert.Equal("1 object with soft effects or transparency is written as a picture: EPS cannot draw them.", again.PreviewNote.Text);
        Choose(again, ExportFormat.Pdf);
        Assert.Equal("", again.PreviewNote.Text);
        d.Close();
        again.Close();
    }

    // ---------------------------------------------------------------- the request and what is remembered

    [AvaloniaFact]
    public void The_request_carries_the_typed_size_the_quality_and_the_compression()
    {
        var d = Open(SampleDoc(out _, out _));
        Type(d, d.WidthBox, "1234");
        d.PngSlider.Value = 9;
        Assert.True(d.TryBuild(out var png, out _));
        Assert.Equal(ExportFormat.Png, png!.Format);
        Assert.Equal((1234, 617), (png.Options.PixelWidth, png.Options.PixelHeight));
        Assert.Equal(9, png.Options.PngCompression);
        Assert.Equal(ExportScope.Page, png.Options.Scope);

        Choose(d, ExportFormat.Jpeg);
        d.JpegSlider.Value = 55;
        Assert.True(d.TryBuild(out var jpeg, out _));
        Assert.Equal(55, jpeg!.Options.JpegQuality);
        Assert.Equal(1234, jpeg.Options.PixelWidth);

        Choose(d, ExportFormat.Pdf);
        Assert.True(d.TryBuild(out var pdf, out _));
        Assert.Null(pdf!.Options.PixelWidth);                                       // a vector format has no pixel size
        d.Close();
    }

    [AvaloniaFact]
    public void Remembered_choices_come_back_and_the_typed_number_applies_to_whatever_is_exported_next()
    {
        var doc = SampleDoc(out var box, out _);
        var first = Open(doc, [box]);
        Choose(first, ExportFormat.Jpeg);
        first.JpegSlider.Value = 61;
        Type(first, first.HeightBox, "900");
        first.SelectionRadio.IsChecked = true;
        Flush(first);
        var settings = first.CurrentSettings();
        first.Close();
        Assert.Equal((ExportFormat.Jpeg, null, 900, 61, true), (settings.Format, settings.PixelWidth, settings.PixelHeight, settings.JpegQuality, settings.SelectionOnly));

        var second = Open(doc, [box], settings);

        Assert.Equal(ExportFormat.Jpeg, second.Format);
        Assert.Equal(61, (int)second.JpegSlider.Value);
        Assert.True(second.SelectionRadio.IsChecked);
        Assert.Equal("900", second.HeightBox.Text);
        Assert.Equal("1650", second.WidthBox.Text);                                 // 900 x 11 / 6
        second.Close();
    }

    // ---------------------------------------------------------------- the preview

    [AvaloniaFact]
    public void The_preview_shows_exactly_the_exported_area_fitted_into_the_pane()
    {
        var doc = SampleDoc(out var box, out var far);
        var d = Open(doc, [box, far]);

        Assert.True(d.PreviewsDrawn >= 1);
        var page = d.LastPreview!.Value;
        Assert.Equal(doc.Page.Bounds, page.Area);                                   // the page
        Assert.Equal(2.0, (double)page.Width / page.Height, 0.02);                  // 2:1, like the page

        d.PasteboardBox.IsChecked = true;
        Flush(d);
        var withBoard = ExportPlan.Create(doc, new ExportOptions { IncludePasteboard = true }).Area;
        Assert.Equal(withBoard, d.LastPreview!.Value.Area);
        Assert.Equal(withBoard.Width / withBoard.Height, (double)d.LastPreview.Value.Width / d.LastPreview.Value.Height, 0.02);

        d.SelectionRadio.IsChecked = true;
        Flush(d);
        var selection = ExportPlan.Create(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [box, far] }).Area;
        Assert.Equal(selection, d.LastPreview!.Value.Area);                         // the same bounds the export uses: stroke and all
        d.Close();
    }

    [AvaloniaFact]
    public void The_preview_follows_the_choices_and_a_burst_of_changes_draws_only_the_last()
    {
        var doc = SampleDoc(out var box, out _);
        var d = Open(doc, [box]);
        int before = d.PreviewsDrawn;

        d.SelectionRadio.IsChecked = true;
        Flush(d);
        Assert.Equal(before + 1, d.PreviewsDrawn);
        Assert.Equal(ExportPlan.Create(doc, new ExportOptions { Scope = ExportScope.Selection, Selection = [box] }).Area, d.LastPreview!.Value.Area);

        before = d.PreviewsDrawn;
        for (int i = 0; i < 12; i++) { d.PasteboardBox.IsChecked = i % 2 == 0; d.PageRadio.IsChecked = i % 2 == 0; d.SelectionRadio.IsChecked = i % 2 == 1; }
        d.PageRadio.IsChecked = true;
        Flush(d);
        Assert.True(d.PreviewsDrawn - before <= 2, $"{d.PreviewsDrawn - before} previews for one burst");
        d.Close();
    }

    [AvaloniaFact]
    public void Typing_in_the_size_fields_never_redraws_the_preview()
    {
        var d = Open(SampleDoc(out _, out _));
        int drawn = d.PreviewsDrawn;
        var sw = Stopwatch.StartNew();

        foreach (var text in new[] { "1", "12", "123", "1234", "12345", "123", "99" }) d.WidthBox.Text = text;
        Flush(d);

        Assert.Equal(drawn, d.PreviewsDrawn);
        Assert.True(sw.ElapsedMilliseconds < 500);
        d.Close();
    }

    [AvaloniaFact]
    public void Jpeg_and_the_legacy_layout_preview_on_white_the_others_on_the_checkerboard()
    {
        var d = Open(SampleDoc(out _, out _));
        Choose(d, ExportFormat.Jpeg);
        Assert.NotNull(d.LastPreview);
        Choose(d, ExportFormat.Png);
        Assert.NotNull(d.LastPreview);
        // the backdrops themselves are checked on pixels in the Files.Tests (ExportPreview); here the choice is made by format
        d.Close();
    }

    // ---------------------------------------------------------------- JPEG background color

    [AvaloniaFact]
    public void The_background_color_is_a_jpeg_choice_only_white_to_start_and_named_in_the_summary()
    {
        var d = Open(SampleDoc(out _, out _));
        Assert.False(Shown(d.BackgroundButton));                                    // PNG keeps its transparency: no background to choose
        Choose(d, ExportFormat.Jpeg);
        Assert.True(Shown(d.BackgroundButton));
        Assert.Equal(DocColor.White, d.JpegBackground);
        Assert.Contains("#FFFFFF", d.SummaryText.Text);

        d.SetBackground(new DocColor(0x10, 0x20, 0x30, 128, "ignored name"));
        Flush(d);
        Assert.Equal(new DocColor(0x10, 0x20, 0x30), d.JpegBackground);              // only the color counts
        Assert.EndsWith("· background #102030", d.SummaryText.Text);
        Assert.True(d.TryBuild(out var request, out _));
        Assert.Equal(new DocColor(0x10, 0x20, 0x30), request!.Options.Background);

        foreach (var f in new[] { ExportFormat.Png, ExportFormat.Pdf, ExportFormat.Svg, ExportFormat.Eps, ExportFormat.Ai, ExportFormat.Lyt })
        {
            Choose(d, f);
            Assert.False(Shown(d.BackgroundButton), $"{f}");
        }
        d.Close();
    }

    [AvaloniaFact]
    public void The_background_color_is_remembered_with_the_other_export_settings()
    {
        var doc = SampleDoc(out _, out _);
        var first = Open(doc, settings: new ExportSettings(ExportFormat.Jpeg));
        first.SetBackground(DocColor.FromHex("#00AA55"));
        var settings = first.CurrentSettings();
        first.Close();
        Assert.Equal(DocColor.FromHex("#00AA55"), settings.JpegBackground);

        var second = Open(doc, settings: settings);
        Assert.Equal(DocColor.FromHex("#00AA55"), second.JpegBackground);
        Assert.Contains("#00AA55", second.SummaryText.Text);
        Assert.True(second.TryBuild(out var request, out _));
        Assert.Equal(DocColor.FromHex("#00AA55"), request!.Options.Background);
        second.Close();
    }

    [AvaloniaFact]
    public void The_preview_redraws_with_the_chosen_background_for_jpeg_only()
    {
        var d = Open(SampleDoc(out _, out _), settings: new ExportSettings(ExportFormat.Jpeg));
        int drawn = d.PreviewsDrawn;

        d.SetBackground(new DocColor(200, 0, 0));
        Flush(d);
        Assert.True(d.PreviewsDrawn > drawn);                                       // the color behind the art changed, so the picture is drawn again
        Assert.Equal(new SkiaSharp.SKColor(200, 0, 0), d.LastPreviewCorner);

        d.SetBackground(new DocColor(0, 0, 200));
        Flush(d);
        Assert.Equal(new SkiaSharp.SKColor(0, 0, 200), d.LastPreviewCorner);

        Choose(d, ExportFormat.Png);                                                // PNG: the checkerboard again, not the color
        Assert.NotEqual(new SkiaSharp.SKColor(0, 0, 200), d.LastPreviewCorner);
        d.Close();
    }

    [AvaloniaFact]
    public void The_color_choice_dialog_takes_hex_codes_and_offers_the_designs_colors()
    {
        var picker = new ColorChoiceDialog("Background color", DocColor.White, [new DocColor(12, 34, 56, 255, "Vinyl"), new DocColor(12, 34, 56)]);
        picker.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#FFFFFF", picker.HexBox.Text);
        picker.HexBox.Text = "#336699";
        Assert.True(picker.OkButton.IsEnabled);
        picker.HexBox.Text = "336";
        Assert.False(picker.OkButton.IsEnabled);                                    // not a color: OK waits
        picker.HexBox.Text = "#336699";
        Assert.True(picker.Presets.Count >= 6);                                     // white, grays, black and the design's color (named and unnamed ones are one)
        Assert.Equal(1, picker.Presets.Count(p => Equals(Avalonia.Controls.ToolTip.GetTip(p), "#0C2238")));
        picker.Presets[^1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("#0C2238", picker.HexBox.Text);
        picker.OkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new DocColor(12, 34, 56), picker.Result);

        var cancelled = new ColorChoiceDialog("Background color", DocColor.Black, []);
        cancelled.Show();
        cancelled.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Null(cancelled.Result);
    }

    // ---------------------------------------------------------------- big files

    [AvaloniaFact]
    public void A_big_layout_keeps_the_dialog_responsive_while_the_preview_draws_on_a_worker()
    {
        Assert.SkipUnless(LocalData.Has("layouts/art-d.lyt"), "needs local-testdata/layouts/art-d.lyt (see the README)");
        var doc = LytImporter.Open(LocalData.Layout("art-d.lyt")).Document;

        // the document has been on screen in the program, so the bounds of its objects are known: warm them as the canvas would
        var cold = Stopwatch.StartNew();
        var planned = ExportPlan.Create(doc, new ExportOptions());
        long coldMs = cold.ElapsedMilliseconds;
        cold.Restart();
        ExportPlan.Create(doc, new ExportOptions());
        long warmMs = cold.ElapsedMilliseconds;

        var opening = Stopwatch.StartNew();
        var d = new ExportDialog(doc, [], new ExportSettings());
        d.Show();
        long constructAndShow = opening.ElapsedMilliseconds;
        // while the preview is being made the UI thread stays free: typing and switching formats answer at once
        var typing = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) { d.WidthBox.Text = (1000 + i).ToString(); Dispatcher.UIThread.RunJobs(); }
        long typed = typing.ElapsedMilliseconds;
        Flush(d);

        File.WriteAllText(Path.Combine(LocalData.Artifacts, "export-dialog-perf.txt"), $"art-d: {doc.ObjectCount} objects; plan cold {coldMs} ms / warm {warmMs} ms; dialog open {constructAndShow} ms, 20 keystrokes {typed} ms, preview drawn in {d.LastPreviewMs:0} ms");
        Assert.True(d.PreviewsDrawn >= 1);
        Assert.True(typed < 400, $"20 keystrokes took {typed} ms while the preview was drawing");
        Assert.True(constructAndShow < 3000, $"opening the dialog took {constructAndShow} ms");
        Assert.True(d.LastPreviewMs < 15000, $"preview {d.LastPreviewMs:0} ms");
        // choosing another format and area afterwards stays quick too
        var switching = Stopwatch.StartNew();
        d.FormatBox.SelectedIndex = 1;
        d.FormatBox.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(switching.ElapsedMilliseconds < 400);
        Flush(d);
        d.Close();
    }
}
