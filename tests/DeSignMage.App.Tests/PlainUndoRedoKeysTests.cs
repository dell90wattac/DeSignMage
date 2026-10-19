using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeSignMage.App.Canvas;
using DeSignMage.App.Reporting;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Undo and Redo on the plain letters Z and Y (user, 2026-10-13), next to Ctrl+Z / Ctrl+Y. Same rules as the plain X / C / V.</summary>
public class PlainUndoRedoKeysTests
{
    private static (MainWindow W, DesignCanvas C, RectangleObject R) Open()
    {
        var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var c = w.FindControl<DesignCanvas>("Surface")!;
        c.Focus();
        var r = new RectangleObject(100, 60) { Transform = DeSignMage.Core.Geometry.Affine2.Translation(200, 200), Style = ObjectStyle.FillOnly(DocColor.FromHex("#E53935")) };
        w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, r));
        w.Session.Selection.Set(r);
        Dispatcher.UIThread.RunJobs();
        return (w, c, r);
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        PhysicalKey physical = key switch { Key.Z => PhysicalKey.Z, Key.Y => PhysicalKey.Y, _ => PhysicalKey.None };
        w.KeyPress(key, mods, physical, key.ToString().ToLowerInvariant());
        Dispatcher.UIThread.RunJobs();
    }

    private static Point P(DesignCanvas c, double x, double y) => c.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    [AvaloniaFact]
    public void Plain_Z_undoes_and_plain_Y_redoes()
    {
        var (w, _, r) = Open();
        Press(w, Key.Z);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        Assert.False(w.Session.History.CanUndo);
        Press(w, Key.Y);
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));
        w.Close();
    }

    [AvaloniaFact]
    public void The_Ctrl_versions_still_work()
    {
        var (w, _, r) = Open();
        Press(w, Key.Z, RawInputModifiers.Control);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        Press(w, Key.Y, RawInputModifiers.Control);
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));
        Press(w, Key.Z, RawInputModifiers.Control);
        Press(w, Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);      // Ctrl+Shift+Z is Redo, as before
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));
        w.Close();
    }

    [AvaloniaFact]
    public void Shift_Alt_and_Win_combinations_do_not_trigger_them()
    {
        var (w, _, r) = Open();
        foreach (var mods in new[] { RawInputModifiers.Shift, RawInputModifiers.Alt, RawInputModifiers.Meta })
        {
            Press(w, Key.Z, mods);
            Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));     // nothing was undone
        }
        Press(w, Key.Z, RawInputModifiers.Control);                                      // now something can be redone
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        foreach (var mods in new[] { RawInputModifiers.Shift, RawInputModifiers.Alt, RawInputModifiers.Meta })
        {
            Press(w, Key.Y, mods);
            Assert.Empty(w.Session.Document.ActiveLayer.Children);                       // and nothing was redone
        }
        Assert.True(w.Session.History.CanRedo);
        w.Close();
    }

    [AvaloniaFact]
    public void The_plain_letters_are_ignored_while_text_is_being_edited()
    {
        var (w, c, r) = Open();
        var s = w.Session;
        s.Selection.Clear();
        s.SetTool("text");
        w.MouseDown(P(c, 300, 300), MouseButton.Left);
        w.MouseUp(P(c, 300, 300), MouseButton.Left);
        Assert.True(s.IsTextEditing);
        foreach (var key in new[] { Key.Z, Key.Y })
        {
            Press(w, key);                                                               // the key goes to the text, not to Undo / Redo
            w.KeyTextInput(key.ToString().ToLowerInvariant());
        }
        Assert.True(s.IsTextEditing);
        var t = Assert.Single(s.Document.ActiveLayer.Children.OfType<TextObject>());     // the text was typed, and nothing was undone
        Assert.Equal("zy", t.Text);
        Assert.Contains(r, s.Document.ActiveLayer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public void The_plain_letters_are_ignored_in_a_focused_text_field_and_drop_down()
    {
        var (w, _, r) = Open();
        w.Session.NudgeSelection(new DeSignMage.Core.Geometry.Vec2(10, 0));              // something to undo that keeps r selected
        var moved = r.GetWorldBounds();
        w.MeasureFields.X.Focus();                                                       // the X field of the Info Line
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<TextBox>(w.FocusManager!.GetFocusedElement());
        foreach (var key in new[] { Key.Z, Key.Y }) Press(w, key);
        Assert.Equal(moved, r.GetWorldBounds());                                         // Z did not undo
        Assert.True(w.Session.History.CanUndo);

        // a drop-down that has the focus (the Color Mixer's color model list) takes plain letters for itself as well
        w.Session.Undo();                                                                // now there is something to redo
        var undone = r.GetWorldBounds();
        Assert.NotEqual(moved, undone);
        w.ToggleDockPanel("colorMixer");
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var combo = w.GetVisualDescendants().OfType<ComboBox>().First(b => b.IsVisible);
        combo.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(w.FindControl<DesignCanvas>("Surface"), w.FocusManager!.GetFocusedElement());   // the focus is on the drop-down, not on the canvas
        Press(w, Key.Y);
        Assert.Equal(undone, r.GetWorldBounds());                                        // Y did not redo
        Assert.True(w.Session.History.CanRedo);
        w.Close();
    }

    [AvaloniaFact]
    public void The_plain_letters_are_ignored_in_a_dialog()
    {
        var (w, _, r) = Open();
        var dialog = new FeedbackDialog(canSend: false, savedName: null, _ => Task.FromResult(new SubmitResult(SubmitStatus.Queued, "x")));
        dialog.Show(w);
        Dispatcher.UIThread.RunJobs();
        var box = dialog.GetVisualDescendants().OfType<TextBox>().First();
        box.Focus();
        foreach (var key in new[] { Key.Z, Key.Y }) dialog.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, key.ToString().ToLowerInvariant());
        Dispatcher.UIThread.RunJobs();
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));
        dialog.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void The_Edit_menu_shows_Z_and_Y_and_the_key_map_keeps_both_forms()
    {
        var (w, _, _) = Open();
        var edit = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Edit");
        string Gesture(string header) => edit.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == header).InputGesture!.ToString();
        Assert.Equal("Z", Gesture("_Undo"));
        Assert.Equal("Y", Gesture("_Redo"));
        Assert.Equal(["Z", "Ctrl+Z"], KeyMap.Default.Where(k => k.CommandId == "edit.undo").Select(k => k.Gesture).ToArray());
        Assert.Equal(["Y", "Ctrl+Y", "Ctrl+Shift+Z"], KeyMap.Default.Where(k => k.CommandId == "edit.redo").Select(k => k.Gesture).ToArray());
        Assert.True(KeyMap.IsPlainKey(KeyGesture.Parse("Z")));                           // so they only fire with the canvas focused
        Assert.True(KeyMap.IsPlainKey(KeyGesture.Parse("Y")));
        // nothing else uses these letters
        Assert.DoesNotContain(KeyMap.Default, k => k.Gesture is "Z" or "Y" && k.CommandId is not ("edit.undo" or "edit.redo"));
        w.Close();
    }
}
