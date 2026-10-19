namespace DeSignMage.Core.Editing.Tools;

/// <summary>
/// A tool that is a mode with a natural conclusion but stays open for further adjusting (effects while they are set, node editing, cropping,
/// a path being drawn, text being typed). The Context Bar of such a tool ends with <b>Done</b> and <b>Cancel</b> while the mode is on:
/// <see cref="EditorSession.DoneMode"/> keeps everything and goes to the Layout tool, <see cref="EditorSession.CancelMode"/> takes everything back
/// to how the design was when the mode started (and leaves no undo steps) and goes to the Layout tool with the first selection.
/// Tools that finish by themselves (Knife, shapes, Eyedropper, placing a paste) are not modes in this sense.
/// </summary>
public interface IModeTool
{
    /// <summary>True while the tool is in a mode that Done or Cancel can end (the Context Bar shows Cancel, and Done when <see cref="CanFinish"/>).</summary>
    bool IsInMode { get; }

    /// <summary>True when Done has something to keep or apply (a shadow with no shift, a crop without a box have not).</summary>
    bool CanFinish { get; }

    /// <summary>Done: keeps the work of the mode and leaves the tool for the Layout tool, with the result selected. Stays in the tool when it could not apply.</summary>
    Task FinishMode();

    /// <summary>
    /// Cancel, first part: drops what the tool holds that is not in the undo history (a half-drawn path, text being typed, a preview, the wish to
    /// remember settings). Called before the tool is left; the session then rolls the history back and goes to the Layout tool.
    /// </summary>
    void DiscardPending();
}
