using DeSignMage.Core.Commands;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

public sealed partial class EditorSession
{
    /// <summary>True if any selected object is a path with two or more loops, or a combined object that has loops to split.</summary>
    public bool CanBreakIntoLoops => Selection.Items.Any(BreakIntoLoopsCommand.CanBreak);

    /// <summary>
    /// Arrange ▸ Break Into Loops: every selected multi-loop graphic becomes one object per loop (one undo step for the whole
    /// selection); afterwards nothing is selected, like Ungroup and Break Apart. Objects that have only one loop stay as they are.
    /// </summary>
    public void BreakIntoLoopsSelection()
    {
        var targets = Selection.Items.Where(BreakIntoLoopsCommand.CanBreak).ToList();
        if (targets.Count == 0) return;
        using (History.BeginTransaction("Break Into Loops"))
        {
            foreach (var t in targets)
                History.Execute(new BreakIntoLoopsCommand(t));
        }
        Selection.Clear();
    }
}
