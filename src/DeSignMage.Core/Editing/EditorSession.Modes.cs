using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

// Done and Cancel for the tools that are modes (IModeTool): effects while they are set, node editing, cropping, drawing and typing.
public sealed partial class EditorSession
{
    /// <summary>Where the design stood when the mode began: the undo history and the selection.</summary>
    private sealed record ModeMark(CommandHistory.Mark History, IReadOnlyList<DocObject> Selection);

    private ModeMark? _modeMark;          // the active tool's mode
    private ModeMark? _pendingModeMark;   // taken before the command that opens a mode did its own first step (Text on Path, text to curves)

    private ModeMark CaptureMode() => new(History.MarkNow(), Selection.Items.ToArray());

    /// <summary>Called by SetTool for the tool that becomes active: a mode remembers how the design stood (or how it stood just before its opening command).</summary>
    private ModeMark? ModeMarkFor(Tool tool)
    {
        var mark = tool is IModeTool ? _pendingModeMark ?? CaptureMode() : null;
        _pendingModeMark = null;
        return mark;
    }

    /// <summary>For a command that changes the design and then opens a mode: remember how things stood before its first step, so Cancel undoes that step too. The next <see cref="SetTool"/> uses it.</summary>
    internal void MarkNextMode() => _pendingModeMark = CaptureMode();

    /// <summary>For a tool whose mode starts again and again while it stays active (the next path being drawn, the next text being typed): the mode begins now.</summary>
    internal void MarkMode()
    {
        if (_activeTool is IModeTool) _modeMark = CaptureMode();
    }

    /// <summary>True while the active tool is in a mode that Done and Cancel end: the Context Bar shows Cancel (and Done, see <see cref="CanFinishMode"/>).</summary>
    public bool InMode => _activeTool is IModeTool { IsInMode: true };

    /// <summary>True when Done has something to keep or apply in the active mode.</summary>
    public bool CanFinishMode => _activeTool is IModeTool { IsInMode: true, CanFinish: true };

    /// <summary>True when the design differs from how it was when the mode began (steps were done or undone since).</summary>
    public bool ModeHasChanges => _modeMark is { } m && !History.IsAt(m.History);

    /// <summary>Done: keeps everything done in the mode and goes to the Layout tool with the result selected. A mode that could not apply (an effect that failed) stays open.</summary>
    public async Task DoneMode()
    {
        if (_activeTool is IModeTool { IsInMode: true } tool) await tool.FinishMode();
    }

    /// <summary>
    /// Cancel: puts the design back exactly as it was when the mode began (every change made in the mode is undone, and forgotten: no undo or redo
    /// step is left behind, live effects follow their sources back), then goes to the Layout tool with the selection the mode started with.
    /// </summary>
    public void CancelMode()
    {
        if (_activeTool is not IModeTool { IsInMode: true } tool) return;   // outside a mode there is nothing to take back (and the mark may be an old one)
        var mark = _modeMark;
        CancelEffect();             // a long effect still being computed is abandoned, as Esc does
        tool.DiscardPending();
        SetTool("select");          // the tool lets go of the drawing before the history is rolled back
        if (mark is not null)
        {
            History.RollBackTo(mark.History);
            Selection.Set(mark.Selection.Where(Document.Contains));
        }
        RequestRedraw();
    }
}
