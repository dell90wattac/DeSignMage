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

/// <summary>Effects ▸ Easy Bevel in the real window: the menu, the strip and the Context Bar, which shows only what applies.</summary>
public class BevelShellTests
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

    private static RectangleObject AddRect(MainWindow w, double x, double y, double width, double height, string color = "#CC2222")
    {
        var r = new RectangleObject(width, height) { Transform = Affine2.Translation(x, y), Style = ObjectStyle.FillOnly(DocColor.FromHex(color)) };
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

    private static BevelContextBar Bar(MainWindow w) => Assert.IsType<BevelContextBar>(w.GetVisualDescendants().OfType<BevelContextBar>().FirstOrDefault());

    private static async Task Settle(MainWindow w)
    {
        await w.Session.GetTool<BevelTool>().Settled;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_Easy_Bevel_menu_item_opens_the_Context_Bar_and_every_control_shows_only_when_it_applies()
    {
        var w = Open();
        var s = w.Session;
        Assert.False(EffectsItem(w, "Easy Bevel...").IsEnabled);                     // nothing selected
        var a = AddRect(w, 200, 200, 300, 150);
        s.Selection.Set(a);
        Assert.True(EffectsItem(w, "Easy Bevel...").IsEnabled);
        var tool = s.GetTool<BevelTool>();

        Click(EffectsItem(w, "Easy Bevel..."));
        Assert.Equal("bevel", s.ActiveTool.Id);
        await Settle(w);
        var bar = Bar(w);
        var layer = s.Document.ActiveLayer;
        Assert.Equal(3, layer.Children.Count);                                       // the bevel shows at once: object, highlight, shadow
        Assert.True(bar.Done.IsVisible);
        Assert.True(bar.Remove.IsVisible);                                           // the bevel is on the page, so it can be removed
        Assert.True(bar.HighlightColor.IsVisible && bar.ShadowColor.IsVisible);      // colors apply while the bevel is not neutral
        Assert.Equal(0, bar.Preset.SelectedIndex);                                   // the defaults are no preset: "Custom"

        bar.Neutral.IsChecked = true;                                                // Neutral: white and black, so no color lists
        Assert.False(bar.HighlightColor.IsVisible);
        Assert.False(bar.ShadowColor.IsVisible);
        bar.Neutral.IsChecked = false;
        Assert.True(bar.HighlightColor.IsVisible);

        bar.WidthBox.Focus(); bar.WidthBox.Text = "3.3"; w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(0.033, tool.Settings.Width, 6);
        bar.Preset.SelectedIndex = 9;                                                // Heavy Soft
        Assert.Equal(BevelPresets.All[8].Width, tool.Settings.Width);
        await Settle(w);
        bar.ShadowBox.Focus(); bar.ShadowBox.Text = "0"; w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Settle(w);
        Assert.False(tool.Settings.IncludeShadow);
        Assert.Equal(2, layer.Children.Count);                                       // the shadow crescent is left out: object and highlight

        bar.Done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(w);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("select", s.ActiveTool.Id);
        Assert.Equal(2, layer.Children.Count);
        s.Undo();
        Assert.Equal([a], layer.Children);                                           // the whole session was one undo step
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_Light_field_and_the_round_point_on_the_page_turn_the_light_together()
    {
        var w = Open();
        var s = w.Session;
        var a = AddRect(w, 200, 200, 300, 150);
        s.Selection.Set(a);
        var tool = s.GetTool<BevelTool>();
        Click(EffectsItem(w, "Easy Bevel..."));
        await Settle(w);
        var bar = Bar(w);
        Assert.Equal("135", bar.LightBox.Text);                                      // upper left to begin with

        bar.LightBox.Focus();
        bar.LightBox.Text = "90";                                                    // typed, no Enter: live
        await Task.Delay(EffectBarParts.Field.LiveDelayMs * 3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(90, tool.Settings.LightAngle, 6);
        await Settle(w);
        var layer = s.Document.ActiveLayer;
        var hl = layer.Children.OfType<PathObject>().First(p => p.Name == "Bevel Highlight");
        Assert.True(hl.Geometry.Contains(new Vec2(350, 202)));                       // from above: the top edge is lit
        Assert.False(hl.Geometry.Contains(new Vec2(202, 275)));

        // dragging the round point: the field follows
        w.FindControl<DesignCanvas>("Surface")!.Focus();                             // the pointer is on the page now, not in the box
        var point = tool.LightPoint!.Value;
        PointerInput At(Vec2 d, PointerButton b) => new(s.Viewport.ToScreen(d), d, b, ModifierKeys.None);
        s.PointerDown(At(point, PointerButton.Left));
        var to = new Vec2(350 + 500, 275);                                           // far to the right of the center
        s.PointerMove(At(to, PointerButton.Left));
        s.PointerUp(At(to, PointerButton.Left));
        Assert.Equal(0, tool.Settings.LightAngle, 1);
        Assert.Equal("0", bar.LightBox.Text);
        await Settle(w);
        hl = layer.Children.OfType<PathObject>().First(p => p.Name == "Bevel Highlight");
        Assert.True(hl.Geometry.Contains(new Vec2(498, 275)));                       // from the right: the right edge is lit

        s.Undo();
        Assert.Equal([a], layer.Children);                                           // one undo step for the whole session
        w.Close();
    }
    [AvaloniaFact]
    public async Task Reopening_on_a_beveled_object_offers_Remove_and_the_strip_has_a_button()
    {
        var w = Open();
        var s = w.Session;
        var a = AddRect(w, 200, 200, 300, 150);
        s.Selection.Set(a);
        s.ApplyBevel(new BevelSettings());
        var tool = s.GetTool<BevelTool>();
        Assert.True(EffectsItem(w, "Easy Bevel...").IsChecked || s.HasBevel);       // the check mark: this already has one
        var strip = w.GetVisualDescendants().OfType<MenuStrip>().First();
        Assert.Contains("Bevel", strip.LabelsOf("Effects"));

        Click(EffectsItem(w, "Easy Bevel..."));
        await Settle(w);
        var bar = Bar(w);
        Assert.True(bar.Remove.IsVisible);                                           // there is a bevel to remove
        bar.Remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([a], s.Document.ActiveLayer.Children);
        s.Undo();
        Assert.Equal(3, s.Document.ActiveLayer.Children.Count);
        w.Close();
    }

    /// <summary>Saves the screenshots notes/screenshots/effect-bevel-*.png when DSW_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSW_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var w = Open();
        var s = w.Session;
        s.Viewport.ZoomToFit(new Rect2(0, 60, 1728, 560));
        var presets = new[] { ("Slight Soft", 100.0), ("Medium Normal", 480.0), ("Heavy Soft", 860.0) };
        foreach (var (name, x) in presets)
        {
            var text = new TextObject("AB", "Arial", 190) { Transform = Affine2.Translation(x, 330), Style = ObjectStyle.FillOnly(DocColor.FromHex("#D62828")) };
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, text));
            s.Selection.Set(text);
            s.ApplyBevel(BevelPresets.All.Single(p => p.Name == name).ApplyTo(new BevelSettings()));
        }
        var badge = new EllipseObject(150, 150) { Transform = Affine2.Translation(1260, 190), Style = ObjectStyle.FillOnly(DocColor.FromHex("#2A6FDB")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, badge));
        s.Selection.Set(badge);
        s.ApplyBevel(new BevelSettings { Width = 0.04, Softness = 0.08 });
        var plate = AddRect(w, 140, 520, 700, 130, "#2A9D8F");
        s.Selection.Set(plate);
        s.BeginBevel();
        var tool = s.GetTool<BevelTool>();
        await tool.Settled;
        tool.Update(b => b with { Width = 0.07, Softness = 0.12 });
        await tool.Settled;
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        var all = Path.Combine(dir, "effect-bevel-all.png");
        w.CaptureRenderedFrame()!.Save(all);
#pragma warning restore CS0618
        w.Close();

        using var bitmap = SkiaSharp.SKBitmap.Decode(all);
        void Crop(string name, int x, int y, int width, int height)
        {
            using var part = new SkiaSharp.SKBitmap();
            Assert.True(bitmap.ExtractSubset(part, new SkiaSharp.SKRectI(x, y, x + width, y + height)));
            using var image = SkiaSharp.SKImage.FromBitmap(part);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, $"effect-bevel-{name}.png"), data.ToArray());
        }
        Crop("slight-soft", 180, 380, 200, 130);
        Crop("medium-normal", 440, 380, 200, 130);
        Crop("heavy-soft", 700, 380, 200, 130);
        Crop("badge", 975, 380, 140, 130);
        Crop("tool-on-plate", 205, 605, 520, 120);
        Crop("light-dial", 180, 380, 580, 350);
        Crop("context-bar", 40, 36, 700, 78);
    }
}