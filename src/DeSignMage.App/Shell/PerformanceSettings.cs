using DeSignMage.Core.Commands;
using DeSignMage.Feedback;

namespace DeSignMage.App.Shell;

/// <summary>
/// The "Performance &amp; memory" choices (Tools ▸ Options): how much memory the undo history may use (oldest steps are dropped
/// beyond it), and an optional step limit. Remembered in the settings store and pushed into <see cref="CommandHistory"/>'s defaults,
/// which every open and new document follows.
/// </summary>
public static class PerformanceSettings
{
    public const string UndoMemoryKey = "perf.undoMemoryMb", UndoStepsKey = "perf.undoMaxSteps";

    /// <summary>The offered undo memory limits in megabytes.</summary>
    public static readonly int[] MemoryChoicesMb = [256, 512, 1024, 2048, 4096];

    public const int DefaultMemoryMb = 1024;
    public const int MinSteps = 5;

    /// <summary>Raised after a setting changed and was applied (the window then trims the open document's history).</summary>
    public static event Action? Changed;

    /// <summary>Current undo memory limit in megabytes (one of <see cref="MemoryChoicesMb"/>).</summary>
    public static int UndoMemoryMb { get; private set; } = DefaultMemoryMb;

    /// <summary>Most undo steps kept, or null for unlimited (the default).</summary>
    public static int? UndoMaxSteps { get; private set; }

    /// <summary>What the open document's history uses right now (steps, bytes), for the Options page; set by the main window.</summary>
    public static Func<(int Steps, long Bytes)>? UsageProvider { get; set; }

    /// <summary>Reads the remembered choices and applies them (call once at startup, before the first document).</summary>
    public static void Load(SettingsStore store)
    {
        UndoMemoryMb = Snap(store.Get(UndoMemoryKey, DefaultMemoryMb));
        int steps = store.Get(UndoStepsKey, 0);
        UndoMaxSteps = steps >= MinSteps ? steps : null;
        Apply();
    }

    public static void SetUndoMemoryMb(int mb)
    {
        UndoMemoryMb = Snap(mb);
        AppSettings.Store.Set(UndoMemoryKey, UndoMemoryMb);
        Apply();
        Changed?.Invoke();
    }

    /// <summary>Null (or anything below <see cref="MinSteps"/>) means unlimited.</summary>
    public static void SetUndoMaxSteps(int? steps)
    {
        UndoMaxSteps = steps is { } s && s >= MinSteps ? s : null;
        AppSettings.Store.Set(UndoStepsKey, UndoMaxSteps ?? 0);
        Apply();
        Changed?.Invoke();
    }

    private static int Snap(int mb) => MemoryChoicesMb.OrderBy(c => Math.Abs(c - mb)).First();

    private static void Apply()
    {
        CommandHistory.DefaultMemoryLimitBytes = UndoMemoryMb * 1024L * 1024L;
        CommandHistory.DefaultMaxSteps = UndoMaxSteps;
    }

    /// <summary>Back to the program defaults (tests).</summary>
    internal static void Reset()
    {
        UndoMemoryMb = DefaultMemoryMb;
        UndoMaxSteps = null;
        UsageProvider = null;
        Apply();
    }
}
