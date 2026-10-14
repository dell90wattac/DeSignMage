using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;

namespace DeSignMage.App;

/// <summary>Text ▸ Change Font...: one font for all the live text in the selection, groups included.</summary>
public partial class MainWindow
{
    /// <summary>The Change Font dialog that is open (for tests); null otherwise.</summary>
    internal ChangeFontDialog? OpenChangeFontDialog { get; private set; }

    /// <summary>What the last Change Font did (for tests); null before the first one.</summary>
    internal ChangeFontResult? LastChangeFont { get; private set; }

    /// <summary>
    /// Opens the dialog for the selection's live texts and, on OK, applies the chosen family to all of them as one undo step.
    /// Does nothing without live text in the selection (the command is disabled then).
    /// </summary>
    private async Task ChangeFontAsync()
    {
        _session.FinishTextEditing();   // the edited text counts as selected text; its typing is one step of its own
        var texts = _session.SelectedTexts();
        if (texts.Count == 0) return;

        var first = texts[0];
        bool shared = texts.All(t => string.Equals(t.FontFamily, first.FontFamily, StringComparison.OrdinalIgnoreCase));
        var dialog = new ChangeFontDialog(_session.GetFontScope(), first.Text, shared ? new FontItem(first.FontFamily, first.IsFontMissing) : null);
        OpenChangeFontDialog = dialog;
        try { await dialog.ShowDialog(this); }
        finally { OpenChangeFontDialog = null; }
        if (dialog.Result is not { } family) return;
        LastChangeFont = _session.ChangeFont(family);
    }
}
