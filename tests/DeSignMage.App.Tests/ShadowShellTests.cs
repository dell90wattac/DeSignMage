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

/// <summary>Effects ▸ Shadow, Extruded Shadow, Perspective Shadow and Cast Shadow in the real window: the menu, the strip and the Context Bar.</summary>
public class ShadowShellTests
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

    private static RectangleObject AddRect(MainWindow w, double x, double y, double size, string color = "#F2B705")
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

    private static ShadowContextBar Bar(MainWindow w) =>
        Assert.IsType<ShadowContextBar>(w.GetVisualDescendants().OfType<ShadowContextBar>().FirstOrDefault());

    private static async Task Settle(MainWindow w)
    {
        await w.Session.EffectIdle;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_Shadow_menu_item_opens_the_Context_Bar_which_shows_only_what_applies_and_Apply_makes_a_grouped_shadow()
    {
        var w = Open();
        var s = w.Session;
        var a = AddRect(w, 200, 200, 100, "#CC2222");
        s.Selection.Set(a);

        Click(EffectsItem(w, "Shadow..."));
        Assert.Equal("shadow", s.ActiveTool.Id);
        var bar = Bar(w);
        var tool = s.GetTool<ShadowTool>();
        tool.PreviewDelay = TimeSpan.Zero;
        Assert.True(bar.Apply.IsVisible);                                          // a first start has the 3 % shift, so there is something to apply
        Assert.False(bar.DepthGroup.IsVisible);                                    // 3D % belongs to Perspective
        Assert.True(bar.OutlinePercentGroup.IsVisible);

        bar.Form.SelectedIndex = (int)ShadowForm.Perspective;                      // another form from the Form list
        Assert.Equal(ShadowForm.Perspective, tool.Settings.Form);
        Assert.True(bar.DepthGroup.IsVisible);                                     // now 3D % shows
        bar.Form.SelectedIndex = (int)ShadowForm.Flat;
        Assert.False(bar.DepthGroup.IsVisible);

        bar.Hs.Focus(); bar.Hs.Text = "0"; w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        bar.Vs.Focus(); bar.Vs.Text = "0"; w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.False(bar.Apply.IsVisible);                                         // R26: no shift, no Apply button (not greyed, gone)
        bar.Hs.Focus(); bar.Hs.Text = "0.2"; w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(14.4, tool.Settings.ShiftX, 6);
        Assert.True(bar.Apply.IsVisible);
        bar.Color.SelectedIndex = 1;                                               // Black
        Assert.Equal(DocColor.Black, tool.Settings.Color);

        await tool.PreviewSettled;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(tool.PreviewPaths);
        Assert.Single(s.Document.ActiveLayer.Children);                            // nothing is made until Apply

        int steps = s.History.UndoList.Count;
        bar.Apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(w);
        var layer = s.Document.ActiveLayer;
        Assert.Equal(2, layer.Children.Count);
        var shadow = Assert.IsType<PathObject>(layer.Children[0]);
        Assert.Same(a, layer.Children[1]);
        Assert.Equal("Shadow", shadow.Name);
        Assert.Same(a.Parent, shadow.Parent);                                      // grouped with its source
        Assert.IsType<GroupObject>(shadow.Parent);
        Assert.Equal(ObjectStyle.FillOnly(DocColor.Black), shadow.Style);
        Assert.Equal(steps + 1, s.History.UndoList.Count);
        Assert.Equal("select", s.ActiveTool.Id);
        s.Undo();
        Assert.Equal([a], layer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public void The_Effects_strip_has_a_button_for_each_shadow_and_the_forms_open_the_tool()
    {
        var w = Open();
        var a = AddRect(w, 200, 200, 100);
        w.Session.Selection.Set(a);
        var strip = w.GetVisualDescendants().OfType<MenuStrip>().First();
        Assert.True(new[] { "Shadow", "Extruded", "Persp.", "Cast" }.All(l => strip.LabelsOf("Effects").Contains(l)));
        Click(EffectsItem(w, "Cast Shadow..."));
        Assert.Equal(ShadowForm.Cast, w.Session.GetTool<ShadowTool>().Settings.Form);
        Click(EffectsItem(w, "Extruded Shadow..."));
        var tool = w.Session.GetTool<ShadowTool>();
        Assert.Equal(ShadowForm.Extruded, tool.Settings.Form);
        Assert.True(tool.Settings.Outline > 0);                                    // an extruded shadow starts with a 1 % outline
        w.Close();
    }

    /// <summary>Saves the screenshots notes/screenshots/effect-shadow-*.png when DSW_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public async Task Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSW_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        var w = Open();
        var s = w.Session;
        s.Viewport.ZoomToFit(new Rect2(0, 60, 1728, 560));
        var forms = new[]
        {
            (ShadowForm.Flat, ShadowType.Drop, "drop", 100.0), (ShadowForm.Flat, ShadowType.Relief, "relief", 480.0),
            (ShadowForm.Extruded, ShadowType.Drop, "extruded", 860.0), (ShadowForm.Perspective, ShadowType.Drop, "perspective", 1240.0),
        };
        foreach (var (form, type, name, x) in forms)
        {
            var text = new TextObject("AB", "Arial", 170) { Transform = Affine2.Translation(x, 330), Style = ObjectStyle.FillOnly(DocColor.FromHex("#D62828")) };
            s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, text));
            s.Selection.Set(text);
            double d = form == ShadowForm.Flat ? 10 : 36;
            s.Shadow(new ShadowSettings
            {
                Form = form, Type = type, ShiftX = d, ShiftY = d, Outline = type == ShadowType.Relief ? 4 : 0,
                DepthPercent = 55, Color = type == ShadowType.Relief ? DocColor.FromHex("#202020") : DocColor.FromHex("#303030"),
            });
        }
        var cast = new TextObject("AB", "Arial", 170) { Transform = Affine2.Translation(100, 560), Style = ObjectStyle.FillOnly(DocColor.FromHex("#D62828")) };
        s.History.Execute(new AddObjectCommand(s.Document.ActiveLayer, cast));
        s.Selection.Set(cast);
        var bounds = cast.GetWorldBounds();
        s.Shadow(new ShadowSettings { Form = ShadowForm.Cast, ShiftX = 70, ShiftY = bounds.Height * 1.6, Color = DocColor.FromHex("#9A9A9A") });   // Vs = -1.6 H: a floor shadow

        // the Context Bar with the tool open on another object
        var box = AddRect(w, 700, 600, 120, "#2A6FDB");
        s.Selection.Set(box);
        s.BeginShadow(ShadowForm.Perspective);
        var tool = s.GetTool<ShadowTool>();
        tool.PreviewDelay = TimeSpan.Zero;
        tool.Update(o => o with { ShiftX = 90, ShiftY = 50, Color = DocColor.FromHex("#303030") });
        await tool.PreviewSettled;
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        var all = Path.Combine(dir, "effect-shadow-all.png");
        w.CaptureRenderedFrame()!.Save(all);
#pragma warning restore CS0618
        w.Close();

        // close-ups of each kind and of the Context Bar, cut from that picture (same zoom every run)
        using var bitmap = SkiaSharp.SKBitmap.Decode(all);
        void Crop(string name, int x, int y, int width, int height)
        {
            using var part = new SkiaSharp.SKBitmap();
            Assert.True(bitmap.ExtractSubset(part, new SkiaSharp.SKRectI(x, y, x + width, y + height)));
            using var image = SkiaSharp.SKImage.FromBitmap(part);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, $"effect-shadow-{name}.png"), data.ToArray());
        }
        Crop("drop", 175, 385, 220, 135);
        Crop("relief", 435, 385, 220, 135);
        Crop("extruded", 695, 385, 240, 150);
        Crop("perspective", 960, 385, 200, 135);
        Crop("cast", 175, 545, 240, 170);
        Crop("context-bar", 40, 36, 700, 78);
    }
}