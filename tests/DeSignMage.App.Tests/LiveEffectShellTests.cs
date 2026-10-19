using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Canvas;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Live effects in the real window: Effects ▸ Make Plain, the Info Line and the Layers Panel say what a live effect follows.</summary>
public class LiveEffectShellTests
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

    private static T Add<T>(MainWindow w, T o) where T : DocObject
    {
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, o));
        return o;
    }

    private static MenuItem EffectsItem(MainWindow w, string text)
    {
        var menu = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).Replace("_", "") == "Effects");
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        return menu.Items.OfType<MenuItem>().Single(i => ((string)i.Header!).Replace("_", "") == text);
    }

    private static string Info(MainWindow w) => w.FindControl<TextBlock>("InfoLine")!.Text ?? "";
    private static List<string> LayerRows(MainWindow w) => w.FindControl<ListBox>("LayersList")!.Items.Cast<string>().ToList();

    private static void Press(MainWindow w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w).GetValueOrDefault();
        w.MouseDown(p, Avalonia.Input.MouseButton.Left);
        w.MouseUp(p, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Make_plain_is_the_last_Effects_item_and_the_Info_Line_and_Layers_Panel_show_live_effects()
    {
        var w = Open();
        var s = w.Session;
        var t = Add(w, new TextObject("SALE", "Arial", 120) { Transform = Affine2.Translation(200, 300), Style = ObjectStyle.FillOnly(DocColor.FromHex("#D62828")) });
        s.Selection.Set(t);
        Dispatcher.UIThread.RunJobs();
        var item = EffectsItem(w, "Make Plain");
        Assert.False(item.IsEnabled);                                                  // nothing live yet

        Assert.True(s.Shadow(new ShadowSettings { ShiftX = 8, ShiftY = 8 }).Applied);
        var shadow = LiveEffects.Find<LiveShadow>(s.Document, t)!;
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        Assert.Contains("1 live effect", Info(w));                                     // the group of the text and its shadow is selected
        Assert.Contains(LayerRows(w), r => r.Contains("Live Shadow of “SALE”"));

        s.Selection.Set(shadow);                                                       // the shadow by itself (as the Layers Panel can pick it)
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        Assert.Contains("Live Shadow of “SALE”", Info(w));

        s.Selection.Set(t.TopLevel);
        Dispatcher.UIThread.RunJobs();
        item = EffectsItem(w, "Make Plain");
        Assert.True(item.IsEnabled);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(shadow.Live);
        Assert.Equal("Make Plain", s.History.UndoDescription);
        Assert.DoesNotContain(LayerRows(w), r => r.Contains("Live"));
        w.Close();
    }

    [AvaloniaFact]
    public void The_Effects_strip_has_a_Plain_button()
    {
        var w = Open();
        var strip = w.FindControl<MenuStrip>("MenuStrip")!;
        Assert.Contains(strip.CommandsOf("Effects"), c => c.Id == "effects.makePlain");
        w.Close();
    }

    /// <summary>Saves notes/screenshots/live-effects-*.png when DSM_SCREENSHOTS names a folder (docs only).</summary>
    [AvaloniaFact]
    public void Screenshots_for_notes()
    {
        var dir = Environment.GetEnvironmentVariable("DSM_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var w = Open();
        var s = w.Session;
        var t = Add(w, new TextObject("SALE", "Arial", 160) { Transform = Affine2.Translation(160, 330), Style = ObjectStyle.FillOnly(DocColor.FromHex("#F2B705")) });
        s.Selection.Set(t);
        Assert.True(s.RunEffect(OutlineEffect.Plan(s, OutlineKind.Outline, new OutlineSettings { First = 6 })!).Applied);
        s.Selection.Set(t);
        Assert.True(s.Shadow(new ShadowSettings { ShiftX = 10, ShiftY = 10, Outline = 6 }).Applied);
        s.Selection.Set(t);
        s.ApplyBevel(new BevelSettings { Width = 0.03 });
        s.Viewport.ZoomToFit(new Rect2(100, 150, 1300, 300));
        if (w.OpenDockPanel != "layers") w.ToggleDockPanel("layers");
        s.Selection.Set(t.TopLevel);
        Dispatcher.UIThread.RunJobs();
#pragma warning disable CS0618 // the replacement overload's options type is not needed for a plain PNG
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "live-effects-before.png"));

        s.History.Execute(new SetTextCommand(t, "SALE TODAY"));                         // retyped: outline, shadow and bevel follow, one undo step
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "live-effects-after-retype.png"));

        var shadow = LiveEffects.Find<LiveShadow>(s.Document, t)!;
        s.Selection.Set(shadow);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "live-effects-info-line.png"));

        var strip = w.FindControl<MenuStrip>("MenuStrip")!;
        if (w.OpenMenuStrip != "Effects") Press(w, strip.Tab("Effects"));
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "live-effects-strip.png"));
#pragma warning restore CS0618
        w.Close();
    }
}
