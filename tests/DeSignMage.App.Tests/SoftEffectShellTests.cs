using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Effects ▸ Soft Shadow, Glow and Feather in the real window: the menu, the check marks, the Context Bars and the undo steps.</summary>
public class SoftEffectShellTests
{
    private static MainWindow Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        w.FindControl<DesignCanvas>("Surface")!.Focus();
        return w;
    }

    private static RectangleObject AddRect(MainWindow w, double x, double y, double size, string color = "#CC2222")
    {
        var r = new RectangleObject(size, size) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex(color)) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        return r;
    }

    private static MenuItem EffectsItem(MainWindow w, string text)
    {
        var menu = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Effects");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        return menu.Items.OfType<MenuItem>().Single(i => ((string)i.Header!).Replace("_", "") == text);
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void Type(MainWindow w, TextBox box, string text)
    {
        box.Focus();
        box.Text = text;
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
    }

    private static T Bar<T>(MainWindow w) where T : Visual => Assert.IsType<T>(w.GetVisualDescendants().OfType<T>().FirstOrDefault());

    [AvaloniaFact]
    public void The_three_menu_items_are_there_disabled_without_a_selection_and_open_their_tools_with_the_effect_showing()
    {
        var w = Open();
        var s = w.Session;
        Assert.False(EffectsItem(w, "Soft Shadow...").IsEnabled);
        Assert.False(EffectsItem(w, "Glow...").IsEnabled);
        Assert.False(EffectsItem(w, "Feather...").IsEnabled);

        var a = AddRect(w, 200, 200, 100);
        s.Selection.Set(a);
        int steps = s.History.UndoList.Count;
        Assert.True(EffectsItem(w, "Soft Shadow...").IsEnabled);

        Click(EffectsItem(w, "Soft Shadow..."));

        Assert.Equal("softeffect", s.ActiveTool.Id);
        Assert.Equal(steps + 1, s.History.UndoList.Count);
        var bar = Bar<SoftEffectContextBar>(w);
        Assert.True(bar.HsGroup.IsVisible);
        Assert.False(bar.ThicknessGroup.IsVisible);                                // a soft shadow has no thickness (R26)
        Assert.Equal("50", bar.OpacityBox.Text);
        Assert.Equal("50", bar.Softness.Text);

        Type(w, bar.OpacityBox, "80");
        Type(w, bar.Softness, "120");
        Assert.Equal(steps + 1, s.History.UndoList.Count);                          // typing edits the same undo step
        var shadow = SoftEffect.Find((GroupObject)a.Parent!, SoftKind.SoftShadow)!;
        Assert.Equal(0.8, shadow.Style.Blur!.Opacity);
        Assert.Equal(1.2, shadow.Style.Blur.Softness);

        Click(EffectsItem(w, "Soft Shadow..."));                                    // choosing it again while the tool is on does not stack another
        Assert.Equal(steps + 1, s.History.UndoList.Count);

        w.Close();
    }

    [AvaloniaFact]
    public void The_Glow_bar_shows_thickness_instead_of_the_shift_and_Remove_and_Done_work()
    {
        var w = Open();
        var s = w.Session;
        var a = AddRect(w, 200, 200, 100, "#202020");
        s.Selection.Set(a);

        Click(EffectsItem(w, "Glow..."));
        var bar = Bar<SoftEffectContextBar>(w);
        Assert.False(bar.HsGroup.IsVisible);
        Assert.True(bar.ThicknessGroup.IsVisible);
        Type(w, bar.Thickness, "200");
        var glow = SoftEffect.Find((GroupObject)a.Parent!, SoftKind.Glow)!;
        Assert.Equal(-2, glow.Style.Blur!.Inset, 6);

        bar.Done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("select", s.ActiveTool.Id);
        s.Selection.Set((GroupObject)a.Parent!);
        Assert.True(EffectsItem(w, "Glow...").IsChecked);                            // the check mark: the selection has a glow

        Click(EffectsItem(w, "Glow..."));
        bar = Bar<SoftEffectContextBar>(w);
        bar.Remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(SoftEffect.Find((GroupObject)a.Parent!, SoftKind.Glow));
        Assert.Equal("select", s.ActiveTool.Id);

        w.Close();
    }

    [AvaloniaFact]
    public void The_Feather_bar_changes_the_feather_live_and_Escape_takes_it_back()
    {
        var w = Open();
        var s = w.Session;
        var a = AddRect(w, 200, 200, 100);
        s.Selection.Set(a);
        int steps = s.History.UndoList.Count;

        Click(EffectsItem(w, "Feather..."));
        var bar = Bar<FeatherContextBar>(w);
        Assert.Equal("4", bar.Amount.Text);
        Assert.Equal("38", bar.Inset.Text);
        Assert.Equal(new FeatherStyle(0.04, 0.38), a.Style.Feather);

        Type(w, bar.Amount, "12");
        Type(w, bar.Inset, "50");
        Assert.Equal(new FeatherStyle(0.12, 0.5), a.Style.Feather);
        Assert.Equal(steps + 1, s.History.UndoList.Count);

        s.ActiveTool.KeyDown(EditorKey.Escape, ModifierKeys.None);
        Assert.Null(a.Style.Feather);
        Assert.Equal(steps, s.History.UndoList.Count);
        Assert.Equal("select", s.ActiveTool.Id);

        w.Close();
    }

    /// <summary>Saves notes/screenshots/effect-{softshadow,glow,feather}-*.png when DSW_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSW_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;

        TextObject Word(double x, double y, string text, string color) =>
            new(text, "Arial", 150) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex(color)) };

        static void SaveFrame(MainWindow w, string path)
        {
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
            w.CaptureRenderedFrame()!.Save(path);
#pragma warning restore CS0618
        }

        // ---- Soft Shadow: the window with the tool open, then the printed result at 150 dpi
        var w = Open();
        var s = w.Session;
        s.Viewport.ZoomToFit(new Rect2(0, 100, 1000, 500));
        var a = Word(60, 200, "Soft", "#D62828");
        var b = Word(520, 200, "Shadow", "#2A6FDB");
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, a));
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, b));
        s.Selection.Set(a);
        s.ApplySoftEffect(new SoftSettings { Opacity = 0.7, Softness = 0.5, ShiftX = 14, ShiftY = 14 });
        s.Selection.Set(b);
        s.BeginSoftEffect(SoftKind.SoftShadow);
        s.GetTool<SoftEffectTool>().Update(o => o with { Opacity = 0.8, Softness = 0.9, ShiftX = 22, ShiftY = 22 });
        Dispatcher.UIThread.RunJobs();
        SaveFrame(w, Path.Combine(dir, "effect-softshadow-ui.png"));
        w.Close();
        SaveExport(s.Document, Path.Combine(dir, "effect-softshadow-export.png"), 150);

        // ---- Glow on a dark sign
        w = Open();
        s = w.Session;
        s.Viewport.ZoomToFit(new Rect2(0, 100, 1000, 500));
        var sign = new RectangleObject(1000, 500) { Transform = Affine2.Translation(0, 100), Style = ObjectStyle.FillOnly(DocColor.FromHex("#101820")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, sign));
        var g1 = Word(60, 200, "Glow", "#2AA8FF");
        var g2 = Word(480, 200, "Neon", "#FF4FA0");
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, g1));
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, g2));
        s.Selection.Set(g1);
        s.ApplySoftEffect(new SoftSettings { Kind = SoftKind.Glow, Opacity = 0.9, Softness = 0.5, Thickness = 0.5, Color = DocColor.FromHex("#9FD8FF") });
        s.Selection.Set(g2);
        s.BeginSoftEffect(SoftKind.Glow);
        s.GetTool<SoftEffectTool>().Update(o => o with { Opacity = 1, Softness = 0.7, Thickness = 1.2, Color = DocColor.FromHex("#FFD400") });
        Dispatcher.UIThread.RunJobs();
        SaveFrame(w, Path.Combine(dir, "effect-glow-ui.png"));
        w.Close();
        SaveExport(s.Document, Path.Combine(dir, "effect-glow-export.png"), 150);

        // ---- Feather: a plain edge, then three amounts and an inset
        w = Open();
        s = w.Session;
        s.Viewport.ZoomToFit(new Rect2(0, 100, 1000, 500));
        var tiles = new List<RectangleObject>();
        foreach (var (x, amount, inset) in new[] { (40.0, 0.0, 0.0), (270.0, 0.06, 0.0), (500.0, 0.12, 0.0), (730.0, 0.12, 0.38) })
        {
            var tile = new RectangleObject(200, 260) { Transform = Affine2.Translation(x, 180), Style = ObjectStyle.FillOnly(DocColor.FromHex("#2A6FDB")) };
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, tile));
            tiles.Add(tile);
            if (amount > 0) { s.Selection.Set(tile); s.SetFeather(amount, inset); }
        }
        s.Selection.Set(tiles[3]);
        s.BeginFeather();
        s.GetTool<FeatherTool>().Update(f => new FeatherStyle(0.12, 0.38));
        Dispatcher.UIThread.RunJobs();
        SaveFrame(w, Path.Combine(dir, "effect-feather-ui.png"));
        w.Close();
        SaveExport(s.Document, Path.Combine(dir, "effect-feather-export.png"), 150);
    }

    private static void SaveExport(Document doc, string path, double dpi) =>
        File.WriteAllBytes(path, DeSignMage.Rendering.Export.DocumentExporter.RenderPng(doc, new DeSignMage.Rendering.Export.ExportOptions { Dpi = dpi }));
}
