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

/// <summary>Cut, Copy and Paste on the plain letters X, C and V (user, 2026-10-13), next to Ctrl+X / Ctrl+C / Ctrl+V.</summary>
public class PlainClipboardKeysTests
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
        PhysicalKey physical = key switch { Key.X => PhysicalKey.X, Key.C => PhysicalKey.C, _ => PhysicalKey.V };
        w.KeyPress(key, mods, physical, key.ToString().ToLowerInvariant());
        Dispatcher.UIThread.RunJobs();
    }

    private static Point P(DesignCanvas c, double x, double y) => c.TranslatePoint(new Point(x, y), TopLevel.GetTopLevel(c)!).GetValueOrDefault();

    /// <summary>The next left click places what a paste hung on the cursor.</summary>
    private static void Place(MainWindow w, DesignCanvas c, double x, double y)
    {
        w.MouseMove(P(c, x, y));
        w.MouseDown(P(c, x, y), MouseButton.Left);
        w.MouseUp(P(c, x, y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Plain_C_copies_and_plain_V_pastes_with_a_click_to_place()
    {
        var (w, c, _) = Open();
        w.MouseMove(P(c, 600, 400));
        Press(w, Key.C);
        Assert.True(w.Session.CanPaste);
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        Press(w, Key.V);                                                     // hangs on the cursor, as Ctrl+V does
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        Place(w, c, 600, 400);
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        w.Close();
    }

    [AvaloniaFact]
    public void Plain_X_cuts_and_plain_V_pastes_it_back()
    {
        var (w, c, r) = Open();
        Press(w, Key.X);
        Assert.Empty(w.Session.Document.ActiveLayer.Children);
        Assert.True(w.Session.CanPaste);
        Press(w, Key.V);
        Place(w, c, 500, 350);
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        Assert.NotSame(r, w.Session.Document.ActiveLayer.Children[0]);
        w.Close();
    }

    [AvaloniaFact]
    public void The_Ctrl_versions_still_work()
    {
        var (w, c, _) = Open();
        Press(w, Key.C, RawInputModifiers.Control);
        Assert.True(w.Session.CanPaste);
        Press(w, Key.V, RawInputModifiers.Control);
        Place(w, c, 600, 400);
        Assert.Equal(2, w.Session.Document.ActiveLayer.Children.Count);
        w.Session.Selection.Set(w.Session.Document.ActiveLayer.Children[0]);
        Press(w, Key.X, RawInputModifiers.Control);
        Assert.Single(w.Session.Document.ActiveLayer.Children);
        w.Close();
    }

    [AvaloniaFact]
    public void Shift_Alt_and_Win_combinations_do_not_trigger_them()
    {
        var (w, c, r) = Open();
        foreach (var mods in new[] { RawInputModifiers.Shift, RawInputModifiers.Alt, RawInputModifiers.Meta, RawInputModifiers.Shift | RawInputModifiers.Control })
        {
            foreach (var key in new[] { Key.C, Key.V })
            {
                if (mods == (RawInputModifiers.Shift | RawInputModifiers.Control) && key == Key.V) continue;   // not asked: only plain and plain Ctrl
                Press(w, key, mods);
            }
            if (mods != RawInputModifiers.Alt) Press(w, Key.X, mods);                  // Alt+X is Export (its own shortcut), not tested here
        }
        Assert.False(w.Session.CanPaste);                                          // nothing was copied or cut
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));    // and nothing was removed
        Assert.Single(w.Session.Document.ActiveLayer.Children);
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
        foreach (var key in new[] { Key.X, Key.C, Key.V })
        {
            Press(w, key);                                                         // the key goes to the text, not to Cut / Copy / Paste
            w.KeyTextInput(key.ToString().ToLowerInvariant());
        }
        Assert.True(s.IsTextEditing);
        Assert.False(s.CanPaste);
        var t = Assert.Single(s.Document.ActiveLayer.Children.OfType<TextObject>());   // the text was typed, and was not cut or replaced
        Assert.Equal("xcv", t.Text);
        Assert.Contains(r, s.Document.ActiveLayer.Children);
        // Ctrl+C / Ctrl+X / Ctrl+V mid-edit still do nothing to the object either
        Press(w, Key.C, RawInputModifiers.Control);
        Assert.False(s.CanPaste);
        w.Close();
    }

    [AvaloniaFact]
    public void The_plain_letters_are_ignored_in_a_focused_text_field_and_drop_down()
    {
        var (w, _, r) = Open();
        w.MeasureFields.X.Focus();                                                       // the X field of the Info Line
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<TextBox>(w.FocusManager!.GetFocusedElement());
        foreach (var key in new[] { Key.X, Key.C, Key.V }) Press(w, key);
        Assert.False(w.Session.CanPaste);
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));

        // a drop-down that has the focus (the Color Mixer's color model list) takes plain letters for itself as well
        w.ToggleDockPanel("colorMixer");
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        var combo = w.GetVisualDescendants().OfType<ComboBox>().First(b => b.IsVisible);
        combo.Focus();
        Dispatcher.UIThread.RunJobs();
        foreach (var key in new[] { Key.X, Key.C, Key.V }) Press(w, key);
        Assert.False(w.Session.CanPaste);
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));
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
        foreach (var key in new[] { Key.X, Key.C, Key.V }) { dialog.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, key.ToString().ToLowerInvariant()); }
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.Session.CanPaste);
        Assert.Same(r, Assert.Single(w.Session.Document.ActiveLayer.Children));
        dialog.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void The_Edit_menu_shows_the_plain_letters_and_the_key_map_keeps_both()
    {
        var (w, _, _) = Open();
        var edit = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Edit");
        string Gesture(string header) => edit.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == header).InputGesture!.ToString();
        Assert.Equal("X", Gesture("Cu_t"));
        Assert.Equal("C", Gesture("_Copy"));
        Assert.Equal("V", Gesture("_Paste"));
        foreach (var (letter, ctrl, command) in new[] { ("X", "Ctrl+X", "edit.cut"), ("C", "Ctrl+C", "edit.copy"), ("V", "Ctrl+V", "edit.paste") })
        {
            var gestures = KeyMap.Default.Where(k => k.CommandId == command).Select(k => k.Gesture).ToArray();
            Assert.Equal([letter, ctrl], gestures);                                // the plain letter first: that is the one the menu shows
            Assert.True(KeyMap.IsPlainKey(KeyGesture.Parse(letter)));              // so it only fires with the canvas focused
        }
        // nothing else uses these letters
        Assert.DoesNotContain(KeyMap.Default, k => k.Gesture is "X" or "C" or "V" && k.CommandId is not ("edit.cut" or "edit.copy" or "edit.paste"));
        w.Close();
    }
}
