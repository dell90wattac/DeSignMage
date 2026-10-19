using DeSignMage.App.Shell;
using DeSignMage.Feedback;

namespace DeSignMage.App.Recovery;

/// <summary>What the auto-save engine is allowed to do right now.</summary>
public sealed record AutoSaveOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(AutoSaveSettings.DefaultMinutes);

    /// <summary>A design whose file would be bigger than this gets no recovery copy (0 = no limit).</summary>
    public long LargestBytes { get; init; } = AutoSaveSettings.DefaultLargestMb * 1024L * 1024L;
}

/// <summary>
/// The "Auto-save" choices (Tools ▸ Options): on or off, how many minutes between recovery copies, and the largest design that gets one.
/// Remembered in the settings store; the engine reads <see cref="Current"/> at every check, so a change applies at once.
/// </summary>
public static class AutoSaveSettings
{
    public const string EnabledKey = "autosave.enabled", MinutesKey = "autosave.minutes", LargestMbKey = "autosave.largestMb";

    /// <summary>Minutes between recovery copies unless the person chooses another number.</summary>
    public const int DefaultMinutes = 5;
    public const int MinMinutes = 1, MaxMinutes = 60;

    /// <summary>
    /// The copy is written in the background, so a design's size does not freeze the window; the limit is only a guard against filling the disk
    /// with huge designs every few minutes.
    /// </summary>
    public const int DefaultLargestMb = 200;
    public const int MinLargestMb = 5, MaxLargestMb = 4096;

    public static bool Enabled { get; private set; } = true;
    public static int Minutes { get; private set; } = DefaultMinutes;
    public static int LargestMb { get; private set; } = DefaultLargestMb;

    /// <summary>Raised after a choice changed.</summary>
    public static event Action? Changed;

    public static AutoSaveOptions Current => new() { Enabled = Enabled, Interval = TimeSpan.FromMinutes(Minutes), LargestBytes = LargestMb * 1024L * 1024L };

    public static void Load(SettingsStore store)
    {
        Enabled = store.Get(EnabledKey, true);
        Minutes = Math.Clamp(store.Get(MinutesKey, DefaultMinutes), MinMinutes, MaxMinutes);
        LargestMb = Math.Clamp(store.Get(LargestMbKey, DefaultLargestMb), MinLargestMb, MaxLargestMb);
    }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        AppSettings.Store.Set(EnabledKey, on);
        Changed?.Invoke();
    }

    /// <summary>Applies and remembers the minutes between copies (kept within 1 to 60); returns what was applied.</summary>
    public static int SetMinutes(int minutes)
    {
        Minutes = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        AppSettings.Store.Set(MinutesKey, Minutes);
        Changed?.Invoke();
        return Minutes;
    }

    public static int SetLargestMb(int mb)
    {
        LargestMb = Math.Clamp(mb, MinLargestMb, MaxLargestMb);
        AppSettings.Store.Set(LargestMbKey, LargestMb);
        Changed?.Invoke();
        return LargestMb;
    }

    /// <summary>Back to the program defaults (tests).</summary>
    internal static void Reset()
    {
        Enabled = true;
        Minutes = DefaultMinutes;
        LargestMb = DefaultLargestMb;
    }
}
