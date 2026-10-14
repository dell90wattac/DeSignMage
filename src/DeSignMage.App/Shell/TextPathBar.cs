using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;
using TextAlignment = DeSignMage.Core.Model.TextAlignment;

namespace DeSignMage.App.Shell;

/// <summary>
/// Text on Path Context Bar (R26: only choices that apply are shown). Row 1: Done and Remove, how the letters are spread (Fit the whole path or keep their natural
/// spacing), where natural spacing starts (the text's alignment, shown only for natural spacing), the start offset and raise. Row 2: the side, flip, the curve
/// compensation (hidden for a path that is a single arc, where it is forced to 0), then the path's own commands: Add Curve, Delete Curve, Bezier and Arc (each only
/// when it can act on the selected vector). Every change is one undo step and the text re-flows at once.
/// </summary>
public sealed class TextPathContextBar : ContextBarBase, IRefreshable
{
    private static readonly string[] SpacingNames = ["Fit the whole path", "Natural spacing"];
    private static readonly string[] SideNames = ["Above the path", "Below the path"];
    private static readonly string[] StartNames = ["Start of the path", "Middle of the path", "End of the path"];

    private readonly EditorSession _session;
    private readonly TextPathTool _tool;
    private readonly EffectBarParts.Field _offset, _raise, _compensation;
    private readonly ComboBox _spacing, _side, _start;
    private readonly CheckBox _flip;
    private readonly Button _done, _remove, _addCurve, _deleteCurve, _bezier, _arc;
    private readonly Control _startGroup, _compensationGroup;
    private bool _refreshing;

    public TextPathContextBar(EditorSession session, TextPathTool tool)
    {
        _session = session;
        _tool = tool;

        _spacing = Combo(SpacingNames, "How the letters are spread: over the whole path (the first letter at its start, the last at its end), or with their own spacing", i =>
            tool.Update(s => s with { Spacing = i == 1 ? TextPathSpacing.Natural : TextPathSpacing.Fit }, "Text on Path Spacing"));
        _side = Combo(SideNames, "Which side of the path the letters sit on: standing on it, or hanging from it", i =>
            tool.Update(s => s with { Side = i == 1 ? TextPathSide.Below : TextPathSide.Above }, "Text on Path Side"));
        _start = Combo(StartNames, "Where the text starts when it keeps its own spacing (the text's alignment)", i =>
            session.ApplyTextFormat(new TextFormatChange(Alignment: i switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left })));
        _flip = PathBarParts.Check("Flip", "Run the text along the path backwards (it also turns to the other side)", v =>
        {
            if (!_refreshing) tool.Update(s => s with { Flip = v }, "Text on Path Flip");
        });

        _offset = new EffectBarParts.Field(v => tool.Update(s => s with { StartOffset = Units.FromInches(v) }, "Text on Path Offset"));
        _raise = new EffectBarParts.Field(v => tool.Update(s => s with { Raise = Units.FromInches(v) }, "Text on Path Raise"));
        _compensation = new EffectBarParts.Field(v => tool.Update(s => s with { CurveCompensation = Math.Clamp(v, 0, 10) }, "Text on Path Compensation"));
        ToolTip.SetTip(_offset.Box, "Start offset: slides the whole text along the path (inches; negative moves it back)");
        ToolTip.SetTip(_raise.Box, "Raise: lifts the text off the path, toward the tops of the letters (inches; negative sinks it)");
        ToolTip.SetTip(_compensation.Box, "Curve compensation: evens out the gaps around bends. 0 is off; 0.5 is the usual setting");

        _done = PathBarParts.Action("✓ Done", "Leave the tool; the text stays on its path (Enter)", () => { tool.Done(); ContextBars.FocusCanvas?.Invoke(); });
        _remove = PathBarParts.Action("Remove", "Take the path off: the text goes straight again (undoable)", () => { tool.Remove(); ContextBars.FocusCanvas?.Invoke(); });
        _addCurve = PathBarParts.Action("Add Curve", "Add a Bezier at the end of the path", () => { tool.AddCurve(); ContextBars.FocusCanvas?.Invoke(); });
        _deleteCurve = PathBarParts.Action("Delete Curve", "Delete the last vector of the path", () => { tool.DeleteCurve(); ContextBars.FocusCanvas?.Invoke(); });
        _bezier = PathBarParts.Action("Bezier", "Make the selected vector a Bezier curve", () => { tool.ToBezier(); ContextBars.FocusCanvas?.Invoke(); });
        _arc = PathBarParts.Action("Arc", "Make the selected vector a circular arc", () => { tool.ToArc(); ContextBars.FocusCanvas?.Invoke(); });

        _startGroup = ContextBars.Group(ContextBars.Label("From"), _start);
        _compensationGroup = ContextBars.Field("Compensation", _compensation.Box);

        // The left half of the Context Bar is narrow (the menu strip has the right half), so the two rows share the load.
        foreach (var c in new Control[]
                 {
                     _done, _remove, PathBarParts.Gap(),
                     _spacing, _startGroup, ContextBars.Field("Offset", _offset.Box, "in"), ContextBars.Field("Raise", _raise.Box, "in"),
                 })
            Row1.Children.Add(c);
        foreach (var c in new Control[]
                 {
                     _side, _flip, _compensationGroup, PathBarParts.Gap(),
                     _addCurve, _deleteCurve, _bezier, _arc,
                 })
            Row2.Children.Add(c);

        EffectBarParts.Bind(this, session, Refresh, h => tool.Changed += h, h => tool.Changed -= h);
        Refresh();
    }

    private ComboBox Combo(string[] items, string tip, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = items, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(box, tip);
        box.SelectionChanged += (_, _) =>
        {
            if (_refreshing || box.SelectedIndex < 0) return;
            set(box.SelectedIndex);
            ContextBars.FocusCanvas?.Invoke();
        };
        return box;
    }

    public void Refresh()
    {
        _refreshing = true;
        var s = _tool.Settings;
        if (s is not null && _tool.Target is { } text)
        {
            _spacing.SelectedIndex = s.Spacing == TextPathSpacing.Natural ? 1 : 0;
            _side.SelectedIndex = s.Side == TextPathSide.Below ? 1 : 0;
            _start.SelectedIndex = text.Alignment switch { TextAlignment.Center => 1, TextAlignment.Right => 2, _ => 0 };
            _flip.IsChecked = s.Flip;
            _offset.Show(ContextBars.Fmt(Units.ToInches(s.StartOffset)));
            _raise.Show(ContextBars.Fmt(Units.ToInches(s.Raise)));
            _compensation.Show(ContextBars.Fmt(s.CurveCompensation));
            ContextBars.Valid(_startGroup, s.Spacing == TextPathSpacing.Natural);      // in Fit the text always fills the path: there is no start to choose
            ContextBars.Valid(_compensationGroup, !s.PathIsSingleArc);                // forced to 0 on a single arc
        }
        ContextBars.Valid(_addCurve, _tool.CanAddCurve);
        ContextBars.Valid(_deleteCurve, _tool.CanDeleteCurve);
        ContextBars.Valid(_bezier, _tool.CanToBezier);
        ContextBars.Valid(_arc, _tool.CanToArc);
        _refreshing = false;
    }

    // ---- For tests ----

    internal ComboBox SpacingBox => _spacing;
    internal ComboBox SideBox => _side;
    internal ComboBox StartBox => _start;
    internal CheckBox FlipBox => _flip;
    internal TextBox OffsetBox => _offset.Box;
    internal TextBox RaiseBox => _raise.Box;
    internal TextBox CompensationBox => _compensation.Box;
    internal Control StartGroup => _startGroup;
    internal Control CompensationGroup => _compensationGroup;
    internal Button Done => _done;
    internal Button Remove => _remove;
    internal Button AddCurve => _addCurve;
    internal Button DeleteCurve => _deleteCurve;
    internal Button Bezier => _bezier;
    internal Button Arc => _arc;
}
