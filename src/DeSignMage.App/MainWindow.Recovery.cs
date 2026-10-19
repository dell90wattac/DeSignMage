using Avalonia.Threading;
using DeSignMage.App.Recovery;
using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Feedback;
using DeSignMage.Formats.Native;

namespace DeSignMage.App;

/// <summary>
/// Auto-save and restore: while the design has unsaved changes a recovery copy is written in the background (<see cref="AutoSaveEngine"/>);
/// a normal Save, New, Open or close removes it; and at the next start, copies that a run left behind (it was killed, crashed or lost power)
/// are offered back. See notes/autosave.md.
/// </summary>
public partial class MainWindow
{
    /// <summary>How often the engine looks whether a copy is due (the interval in Options is how often one is made).</summary>
    internal static TimeSpan AutoSaveCheckEvery { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Tests that only need a window to leave copies behind switch the start-up question off.</summary>
    internal static bool AskToRestoreAtStart { get; set; } = true;

    /// <summary>Tests replace the engine's clock (null = real time).</summary>
    internal static Func<DateTime>? AutoSaveClock { get; set; }

    private AutoSaveEngine? _autoSave;
    private DispatcherTimer? _autoSaveTimer;
    private readonly TaskCompletionSource _recoveryDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _restorePromptShown;

    /// <summary>Completes when the start-up restore question is over (answered, or there was nothing to ask). The crash-report question waits for it.</summary>
    internal Task RecoveryPromptDone => _recoveryDone.Task;

    internal AutoSaveEngine? AutoSave => _autoSave;

    /// <summary>The restore prompt opened last (for tests).</summary>
    internal RestorePromptDialog? LastRestorePrompt { get; private set; }

    /// <summary>What the last restore said when it could not read a copy (null after a success). For tests.</summary>
    internal string? LastRestoreError { get; private set; }

    private void InitRecovery()
    {
        var store = RecoveryHost.Current;
        if (store is null)
        {
            _recoveryDone.TrySetResult();
            return;
        }
        _autoSave = new AutoSaveEngine(store, CurrentAutoSaveState, () => AutoSaveSettings.Current, AutoSaveClock);
        _autoSave.WriteFailed += message => Dispatcher.UIThread.Post(() =>
            Flash("A recovery copy of your design could not be written (the disk may be full). Saving your design now is a good idea."));

        _autoSaveTimer = new DispatcherTimer { Interval = AutoSaveCheckEvery };
        _autoSaveTimer.Tick += (_, _) => _autoSave.Tick();
        _autoSaveTimer.Start();

        Opened += (_, _) => _ = RestoreAtStartupAsync();
        Closed += (_, _) => FinishRecovery();
    }

    /// <summary>The open design for the engine. Null while there is no session (the window is still being built).</summary>
    private AutoSaveState? CurrentAutoSaveState()
    {
        var session = _session;
        if (session is null) return null;
        var document = session.Document;
        return new AutoSaveState(document, session.History.IsModified, document.Revision, CurrentRecoveryMeta(),
            () => DsmWriter.Snapshot(document, new DsmSaveOptions { Compact = true }));
    }

    private RecoveryMeta CurrentRecoveryMeta() => new()
    {
        Title = _documentTitle,
        DocumentPath = _documentPath,
        SourcePath = _sourceLayoutPath,
        ObjectCount = _session.Document.ObjectCount,
    };

    /// <summary>Called when the design was replaced or saved: its recovery copy is no longer needed.</summary>
    private void DropRecoveryCopy() => _autoSave?.Clear();

    /// <summary>The emergency copy written at a crash also leaves a note, so the next start can offer it for restoring.</summary>
    private void NoteEmergencyCopy(string dsmPath)
    {
        if (!_session.History.IsModified) return;   // a saved design needs no restoring
        RecoveryHost.Current?.WriteCrashSidecar(dsmPath, AutoSaveEngine.DefaultKey, CurrentRecoveryMeta());
    }

    /// <summary>The window closed for good (saved, or the person chose not to save): no copy is left behind, so the next start asks nothing.</summary>
    private void FinishRecovery()
    {
        _autoSaveTimer?.Stop();
        var store = RecoveryHost.Current;
        if (store is null || _autoSave is null) return;
        // A copy being written right now would end up in a folder that is about to go.
        try { _autoSave.Pending?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        store.CloseCleanly();
        AppLog.Info("autosave", "closed cleanly; recovery copies removed");
    }

    // ---------------------------------------------------------------- the question at the next start

    private async Task RestoreAtStartupAsync()
    {
        try
        {
            var store = RecoveryHost.Current;
            if (store is null || !AskToRestoreAtStart) return;
            if (_options.OpenPath is not null)
            {
                // A file given on the command line wins; the copies are still there for the next plain start.
                AppLog.Info("autosave", "started with a file: the restore question waits for the next start");
                return;
            }
            var found = await Task.Run(() => store.FindRecoverable());
            if (found.Count == 0) return;

            // Another copy of the program starting at the same moment must not ask about the same design.
            var claims = new List<IDisposable>();
            var mine = new List<RecoverableDesign>();
            foreach (var design in found)
            {
                if (store.TryClaim(design) is { } claim) { claims.Add(claim); mine.Add(design); }
            }
            try
            {
                if (mine.Count == 0) return;
                AppLog.Info("autosave", $"{mine.Count} unsaved design(s) left by an earlier run; asking");
                _restorePromptShown = true;
                var chosen = await AskToRestoreAsync(mine);
                if (chosen is not null) await RestoreAsync(chosen);
            }
            finally
            {
                foreach (var claim in claims) claim.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("autosave", "The restore question failed", ex);
        }
        finally
        {
            _recoveryDone.TrySetResult();
        }
    }

    private async Task<RecoverableDesign?> AskToRestoreAsync(IReadOnlyList<RecoverableDesign> designs)
    {
        var store = RecoveryHost.Current!;
        var dialog = new RestorePromptDialog(designs, d =>
        {
            store.Discard(d);
            AppLog.Info("autosave", $"discarded the recovery copy of {d.Meta.Title}");
        });
        LastRestorePrompt = dialog;
        await dialog.ShowDialog(this);
        Surface.Focus();
        return dialog.Chosen;
    }

    /// <summary>
    /// Opens a recovery copy as the current design, unsaved and remembering its original file, so Save goes back to the real file. The copy
    /// is moved into this run's folder at once, so the design is protected from the first moment.
    /// </summary>
    internal async Task<bool> RestoreAsync(RecoverableDesign design)
    {
        var store = RecoveryHost.Current!;
        // The emergency copy is the newer one; if it cannot be read the background copy of the same design is the next best.
        var paths = new List<string> { design.FilePath };
        if (design.SlotDsm is not null && design.SlotDsm != design.FilePath && File.Exists(design.SlotDsm)) paths.Add(design.SlotDsm);

        DsmLoadResult? loaded = null;
        string? error = null;
        foreach (var path in paths)
        {
            try
            {
                loaded = await BackgroundWork.RunAsync(this, "Restore", $"Restoring {design.Meta.Title}...", _ => DsmReader.Load(path));
                break;
            }
            catch (Exception e) when (e is DsmFormatException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                AppLog.Warn("autosave", $"could not read a recovery copy: {e.GetType().Name}: {e.Message}");
                error = e.Message;
            }
        }
        if (loaded is null)
        {
            LastRestoreError = error ?? "The recovery copy could not be read.";
            store.SetAsideUnreadable(design);
            await ShowDialogAsync("Cannot restore", $"The recovery copy of {design.Meta.Title} could not be read.{Environment.NewLine}{Environment.NewLine}{LastRestoreError}", "OK", null);
            return false;
        }
        LastRestoreError = null;

        var meta = design.Meta;
        Surface.Session = null;
        // Save may only go straight back to a .dsm; anything else (a legacy layout) only suggests the name.
        bool dsm = meta.DocumentPath is { } d && Path.GetExtension(d).Equals(DsmFormat.Extension, StringComparison.OrdinalIgnoreCase);
        _documentPath = dsm ? meta.DocumentPath : null;
        _sourceLayoutPath = dsm ? meta.SourcePath : meta.DocumentPath ?? meta.SourcePath;
        _documentTitle = meta.DocumentPath is { } p ? Path.GetFileName(p) : meta.Title;
        LoadSession(new EditorSession(loaded.Document));
        _session.History.MarkUnsaved();   // not on disk yet: Save writes it, closing asks
        store.Adopt(design, AutoSaveEngine.DefaultKey);
        RefreshPanels();
        AppLog.Info("autosave", $"restored {_documentTitle} ({loaded.Document.ObjectCount} objects{(design.FromCrashCopy ? ", from the emergency copy" : "")})");
        Flash($"Restored {_documentTitle}. It has not been saved yet.");

        if (loaded.Warnings.Count > 0)
        {
            string text = string.Join(Environment.NewLine, loaded.Warnings.Take(12).Select(w => "• " + w));
            _ = ShowDialogAsync(_documentTitle, $"Restored, but some things were skipped or approximated:{Environment.NewLine}{Environment.NewLine}{text}", "OK", null);
        }
        return true;
    }
}
