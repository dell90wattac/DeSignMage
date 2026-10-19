using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Reporting;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Interface text size and the font list (requirement R12).</summary>
[Collection("app-settings")]   // these swap the program-wide settings store and text size: one class at a time
public class UiScaleShellTests
{
    /// <summary>Runs with a private settings store and puts everything back afterwards (the scale is program-wide state).</summary>
    private static async Task WithScale(Func<Task> body)
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        UiScale.Store = AppSettings.Store;
        UiScale.Reset();
        try { await body(); }
        finally
        {
            UiScale.Reset();
            UiScale.Store = null;
            AppSettings.Store = oldStore;
            FontCatalog.SetInstalledForTests(null);
        }
    }

    private static MainWindow Open(bool demo = false)
    {
        var w = new MainWindow(new StartupOptions(Demo: demo)) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        return w;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static double ThemeSize(string key) => Application.Current!.TryGetResource(key, null, out var v) ? (double)v! : double.NaN;

    private static MenuItem TopMenu(MainWindow w, string header) =>
        w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == header);

    // ---- Remembering the setting ----

    [Fact]
    public void The_settings_store_remembers_values_and_keeps_the_ones_it_does_not_know()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsm-settings-{Guid.NewGuid():N}.json");
        try
        {
            var a = new SettingsStore(path);
            Assert.Equal(1.0, a.Get("ui.textScale", 1.0));
            a.Set("name", "Mum");
            a.Set("ui.textScale", 1.75);
            a.Set("ui.scaleIcons", false);

            var b = new SettingsStore(path);   // a new run of the program
            Assert.Equal(1.75, b.Get("ui.textScale", 1.0));
            Assert.False(b.Get("ui.scaleIcons", true));
            Assert.Equal("Mum", b.Get<string?>("name", null));
            b.Set("ui.fontListSize", 30.0);
            Assert.Equal(1.75, new SettingsStore(path).Get("ui.textScale", 1.0));   // earlier keys survive a later write

            File.WriteAllText(path, "{ this is not json");
            Assert.Equal(1.0, new SettingsStore(path).Get("ui.textScale", 1.0));      // a damaged file just means defaults
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task The_text_size_is_remembered_between_sessions_and_kept_in_range()
    {
        await WithScale(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"dsm-scale-{Guid.NewGuid():N}.json");
            try
            {
                UiScale.Store = new SettingsStore(path);
                UiScale.SetScale(1.75);
                UiScale.SetScaleIcons(false);

                UiScale.Reset();
                Assert.Equal(1.0, UiScale.Scale);
                UiScale.Load(new SettingsStore(path));   // next start
                Assert.Equal(1.75, UiScale.Scale);
                Assert.False(UiScale.ScaleIcons);
                Assert.Equal(1.0, UiScale.IconScale);   // icons stay put when they don't scale

                UiScale.SetScale(9);
                Assert.Equal(2.5, UiScale.Scale);
                UiScale.SetScale(0.2);
                Assert.Equal(1.0, UiScale.Scale);

                UiScale.SetScale(2.0, remember: false);   // the development flag: not written down
                Assert.Equal(1.0, new SettingsStore(path).Get("ui.textScale", 0.0));
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        });
    }

    // ---- The scale reaches the interface ----

    [AvaloniaFact]
    public async Task A_new_text_size_reaches_menus_their_popups_the_status_line_and_tooltips()
    {
        await WithScale(() =>
        {
            var w = Open();
            var file = TopMenu(w, "_File");
            double menuBar100 = file.FontSize;
            var status = w.FindControl<TextBlock>("CursorText")!;   // the Info Line's pointer position
            double status100 = status.FontSize, tooltip100 = ThemeSize("ToolTipContentThemeFontSize");
            Assert.Equal(12, status100);

            UiScale.SetScale(2.0);
            Flush();
            Assert.Equal(menuBar100 * 2, file.FontSize, 3);        // menu bar item
            Assert.Equal(status100 * 2, status.FontSize, 3);       // status line

            file.IsSubMenuOpen = true;                              // the popup is a separate window; the items inside follow too
            Flush();
            var item = file.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "_Open...");
            Assert.Equal(menuBar100 * 2, item.FontSize, 3);
            file.IsSubMenuOpen = false;

            Assert.Equal(tooltip100 * 2, ThemeSize("ToolTipContentThemeFontSize"), 3);   // tooltips read this resource

            // The canvas is not text: its size is whatever the window gives it, and rulers are drawn by us at their own size.
            UiScale.SetScale(1.0);
            Flush();
            Assert.Equal(menuBar100, file.FontSize, 3);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task A_new_text_size_reaches_toolbars_panels_and_open_dialogs()
    {
        await WithScale(() =>
        {
            var w = Open();
            var toolbarGlyph = w.FindControl<BarRow>("MainToolbar")!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("glyph"));
            var palette = w.FindControl<StackPanel>("ToolboxHost")!;
            var firstTool = (Control)palette.Children[0];
            var sideTab = w.FindControl<ToggleButton>("DockTabColorMixer")!;
            double glyph100 = toolbarGlyph.FontSize, tool100 = firstTool.Width, tab100 = sideTab.FontSize;
            var picker = w.GetVisualDescendants().OfType<FontPicker>().First();
            Assert.Equal(210, picker.Width);

            var dialog = new ExportDialog(w.Session.Document, [], new ExportSettings());
            dialog.Show(w);
            Flush();
            var dialogText = dialog.GetVisualDescendants().OfType<TextBlock>().First();
            double dialogFont100 = dialogText.FontSize, dialogWidth100 = dialog.Width;

            UiScale.SetScale(1.5);
            Flush();
            Assert.Equal(glyph100 * 1.5, toolbarGlyph.FontSize, 3);   // toolbar icons (Scale icons with text is on)
            Assert.Equal(tool100 * 1.5, firstTool.Width, 3);          // Toolbox buttons
            Assert.Equal(210 * 1.5, picker.Width, 3);                 // fixed-width fields grow with the text
            Assert.Equal(tab100 * 1.5, sideTab.FontSize, 3);          // Dock Tabs
            Assert.Equal(dialogFont100 * 1.5, dialogText.FontSize, 3);   // a dialog that was already open
            Assert.Equal(dialogWidth100 * 1.5, dialog.Width, 3);

            UiScale.SetScaleIcons(false);                              // text keeps growing, icons go back
            Flush();
            Assert.Equal(glyph100, toolbarGlyph.FontSize, 3);
            Assert.Equal(tool100, firstTool.Width, 3);
            Assert.Equal(tab100 * 1.5, sideTab.FontSize, 3);

            var feedback = new FeedbackDialog(canSend: false, savedName: null, _ => Task.FromResult(new SubmitResult(SubmitStatus.Queued, "x")));
            feedback.Show(w);
            Flush();
            Assert.Equal(12 * 1.5, feedback.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.StartsWith("Tell the developer") == true).FontSize, 3);
            feedback.Close();
            dialog.Close();
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Interface_Text_Size_menu_checks_the_current_size_and_changes_it()
    {
        await WithScale(() =>
        {
            var w = Open();
            var view = TopMenu(w, "_View");
            var sizeMenu = view.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "Interface Text _Size");
            view.IsSubMenuOpen = true; Flush();
            sizeMenu.IsSubMenuOpen = true; Flush();
            var items = sizeMenu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["100%", "125%", "150%", "175%", "200%", "Other size..."], items.Select(i => i.Header?.ToString()));
            Assert.True(items[0].IsChecked);
            Assert.Equal(1, items.Count(i => i.IsChecked));

            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));   // 150%
            Assert.Equal(1.5, UiScale.Scale);
            Flush();
            sizeMenu.IsSubMenuOpen = false; sizeMenu.IsSubMenuOpen = true; Flush();
            Assert.True(items[2].IsChecked);
            Assert.False(items[0].IsChecked);

            UiScale.SetScale(1.6);   // between presets: the "Other size" line is the checked one
            sizeMenu.IsSubMenuOpen = false; sizeMenu.IsSubMenuOpen = true; Flush();
            Assert.True(items[5].IsChecked);
            Assert.Equal(1, items.Count(i => i.IsChecked));
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Tools_Options_has_an_Accessibility_section_with_presets_slider_and_preview()
    {
        await WithScale(() =>
        {
            var w = Open();
            var tools = TopMenu(w, "_Tools");
            tools.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Options...").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Flush();
            var options = Assert.IsType<OptionsWindow>(w.LastOptionsWindow);
            // The list is whatever OptionsPages holds (other features add pages); it starts with Accessibility and has these in this order.
            Assert.Equal(OptionsPages.All.Select(p => p.Title), options.Sections.Items.Cast<string>());
            Assert.Equal("Accessibility", options.Sections.Items.Cast<string>().First());
            var titles = options.Sections.Items.Cast<string>().ToList();
            Assert.True(titles.IndexOf("Vector editing") < titles.IndexOf("Auto-save") && titles.IndexOf("Auto-save") < titles.IndexOf("Performance & memory"));
            var page = Assert.IsType<AccessibilityOptionsPage>(options.CurrentPage);

            Assert.Equal([1.0, 1.25, 1.5, 1.75, 2.0], page.Presets.Select(p => (double)p.Tag!));
            Assert.True(page.Presets[0].IsChecked);
            Assert.Equal(100, page.SizeSlider.Minimum);
            Assert.Equal(250, page.SizeSlider.Maximum);

            page.Presets[3].IsChecked = true;                // 175%
            Assert.Equal(1.75, UiScale.Scale);
            Flush();
            Assert.Equal(175, page.SizeSlider.Value);
            Assert.Equal(12 * 1.75, page.PreviewText.FontSize, 3);

            page.SizeSlider.Value = 235;                      // keyboard-style change applies at once
            Assert.Equal(2.35, UiScale.Scale);
            Assert.All(page.Presets, p => Assert.False(p.IsChecked));

            page.IconsCheck.IsChecked = false;
            Assert.False(UiScale.ScaleIcons);
            options.Close();
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Options_sections_can_be_added_by_other_features()
    {
        await WithScale(() =>
        {
            OptionsPages.Register(new OptionsPage("test-extra", "Extra", () => new TextBlock { Text = "extra page" }));
            try
            {
                var o = new OptionsWindow("test-extra");
                o.Show();
                Flush();
                Assert.Equal(OptionsPages.All.Select(p => p.Title), o.Sections.Items.Cast<string>());
                Assert.Equal("Extra", o.Sections.Items.Cast<string>().Last());   // a registered page comes last
                Assert.Equal("extra page", Assert.IsType<TextBlock>(o.CurrentPage).Text);
                o.Close();
            }
            finally
            {
                OptionsPages.Unregister("test-extra");   // leave the shared list as it was
            }
            return Task.CompletedTask;
        });
    }

    // ---- Font list ----

    private static TextObject AddText(MainWindow w, string text, string family = "Arial")
    {
        var t = new TextObject(text, family, Units.FromInches(1));
        w.Session.History.Execute(new DeSignMage.Core.Commands.AddObjectCommand(w.Session.Document.ActiveLayer, t));
        Dispatcher.UIThread.RunJobs();
        return t;
    }

    private static List<FontItem> ManyFonts(int count) =>
        Enumerable.Range(0, count).Select(i => new FontItem($"Test Font {i:00000}", false)).ToList();

    private static IEnumerable<Control> Rows(FontPicker p) => p.Popup.Child!.GetVisualDescendants().OfType<ListBoxItem>();

    [AvaloniaFact]
    public async Task The_font_list_is_virtualized_and_opens_fast_with_thousands_of_fonts()
    {
        await WithScale(() =>
        {
            FontCatalog.SetInstalledForTests(ManyFonts(5000));
            var w = Open();
            var s = w.Session;
            s.Selection.Set(AddText(w, "home"));
            Flush();
            var picker = w.GetVisualDescendants().OfType<FontPicker>().First();

            var clock = Stopwatch.StartNew();
            picker.Open();
            Flush();
            w.CaptureRenderedFrame();
            clock.Stop();

            Assert.True(picker.IsOpen);
            Assert.Equal(5000 + 1, picker.Items.Count);   // "Arial" isn't in this fake list, so it is listed first
            int realized = Rows(picker).Count();
            Assert.InRange(realized, 1, 60);              // only the rows on screen exist
            Assert.True(clock.ElapsedMilliseconds < 200, $"opening took {clock.ElapsedMilliseconds} ms");

            // Rows are bigger than menu text by default.
            Assert.Equal(22, picker.ListSize);
            Assert.True(picker.RowFontSize > w.FindControl<Menu>("MainMenu")!.FontSize);
            picker.Close();
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Font_rows_show_the_selected_text_in_each_font_or_the_font_names_when_nothing_is_selected()
    {
        await WithScale(() =>
        {
            var w = Open();
            var s = w.Session;
            var picker = w.GetVisualDescendants().OfType<FontPicker>().First();

            // Nothing selected: the font name, drawn in its own font.
            picker.Open(); Flush(); w.CaptureRenderedFrame();
            Assert.Null(picker.Sample);
            var first = Rows(picker).First();
            var nameBlock = first.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
            var shown = (FontItem)first.DataContext!;
            Assert.Equal(shown.Name, nameBlock.Text);
            Assert.Equal(shown.Name, nameBlock.FontFamily.Name);
            picker.Close();

            // Text selected: the first ~20 characters of the text in that font, with the font name in small print.
            var t = AddText(w, "Grand Opening Sale This Weekend");
            s.Selection.Set(t);
            Flush();
            picker = w.GetVisualDescendants().OfType<FontPicker>().First(p => p.IsEffectivelyVisible);
            picker.Open(); Flush(); w.CaptureRenderedFrame();
            Assert.Equal("Grand Opening Sale T…", picker.Sample);   // 20 characters, then an ellipsis
            Assert.Equal(21, picker.Sample!.Length);
            var row = Rows(picker).First();
            var blocks = row.GetVisualDescendants().OfType<TextBlock>().Where(b => !string.IsNullOrEmpty(b.Text)).ToList();
            var item = (FontItem)row.DataContext!;
            var sample = blocks.Single(b => b.Text == picker.Sample);
            var small = blocks.Single(b => b.Text == item.Name);
            Assert.Equal(item.Name, sample.FontFamily.Name);                          // the sample is drawn in this row's font
            Assert.True(small.FontSize < sample.FontSize);                            // the name is small print
            Assert.NotEqual(item.Name, small.FontFamily.Name == item.Name ? "" : small.FontFamily.Name);   // and set in the normal font
            picker.Close();
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_font_list_size_slider_resizes_the_rows_and_is_remembered()
    {
        await WithScale(() =>
        {
            var w = Open();
            AddText(w, "x");
            w.Session.Selection.Set(w.Session.Document.ActiveLayer.Children.Last());
            Flush();
            var picker = w.GetVisualDescendants().OfType<FontPicker>().First(p => p.IsEffectivelyVisible);
            picker.Open(); Flush(); w.CaptureRenderedFrame();
            Assert.Equal(12, picker.SizeSlider.Minimum);
            Assert.Equal(48, picker.SizeSlider.Maximum);
            double before = Rows(picker).First().Bounds.Height;

            picker.SizeSlider.Value = 40;
            Flush(); w.CaptureRenderedFrame();
            Assert.Equal(40, picker.ListSize);
            Assert.True(Rows(picker).First().Bounds.Height > before);
            Assert.Equal(40, AppSettings.Store.Get("ui.fontListSize", 0.0));   // remembered
            picker.Close();

            var next = new FontPicker();                                       // a new session starts from it
            Assert.Equal(40, next.ListSize);
            w.Close();
            return Task.CompletedTask;
        });
    }

    /// <summary>Saves screenshots (ui-scale-100.png, ui-scale-200.png, font-list.png) when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        await WithScale(() =>
        {
            Directory.CreateDirectory(dir);
            var w = new MainWindow(new StartupOptions(Demo: true)) { Width = 1920, Height = 1040 };
            w.Show();
            Flush();
            var text = w.Session.Document.ActiveLayer.Children.OfType<TextObject>().First();
            w.Session.Selection.Set(text);
            Flush();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "ui-scale-100.png"));
            UiScale.SetScale(2.0);
            Flush();
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "ui-scale-200.png"));
            TopMenu(w, "_View").IsSubMenuOpen = true;
            Flush();
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "ui-scale-200-menu.png"));
            TopMenu(w, "_View").IsSubMenuOpen = false;
            UiScale.SetScale(2.5);
            Flush();
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "ui-scale-250.png"));
            UiScale.SetScale(1.0);
            Flush();
            var picker = w.GetVisualDescendants().OfType<FontPicker>().First(p => p.IsEffectivelyVisible);
            picker.Open();
            Flush();
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "font-list.png"));
            picker.Close();
#pragma warning restore CS0618
            w.Close();
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public void Symbol_fonts_are_recognised_and_ordinary_ones_are_not()
    {
        Assert.True(FontCatalog.IsSymbol("Wingdings"));
        Assert.True(FontCatalog.IsSymbol("Webdings"));
        Assert.False(FontCatalog.IsSymbol("Arial"));
    }
}
