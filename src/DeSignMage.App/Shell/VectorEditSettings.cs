using System.Runtime.InteropServices;
using DeSignMage.Core.Editing;
using DeSignMage.Feedback;

namespace DeSignMage.App.Shell;

/// <summary>
/// The "Vector editing" choices (Tools ▸ Options, View menu): the small-vector threshold, the Tangency setting, "Marquee with Shift selects
/// vectors" and View ▸ Highlight Small Vectors. They are remembered in the settings store and pushed into Core's <see cref="SmallVectors"/>
/// and <see cref="VectorEditOptions"/>, which the Vector Edit tool, the Info Line and the highlight follow.
/// </summary>
public static class VectorEditSettings
{
    public const string SmallThresholdKey = "vector.smallThresholdInches";
    public const string TangencyKey = "vector.tangencyDegrees";
    public const string MarqueeShiftSelectsKey = "vector.marqueeShiftSelects";
    public const string HighlightSmallKey = "vector.highlightSmall";

    /// <summary>Reads the remembered choices and applies them (call once at startup).</summary>
    public static void Load(SettingsStore store)
    {
        SmallVectors.SetInches(store.Get(SmallThresholdKey, SmallVectors.ThresholdInches));
        VectorEditOptions.SetTangencyDegrees(store.Get(TangencyKey, VectorEditOptions.DefaultTangencyDegrees));
        VectorEditOptions.MarqueeShiftSelects = store.Get(MarqueeShiftSelectsKey, true);
        VectorEditOptions.HighlightSmallVectors = store.Get(HighlightSmallKey, false);
    }

    /// <summary>Applies and remembers a threshold in inches (kept within the allowed range); returns what was applied.</summary>
    public static double SetSmallThresholdInches(double inches)
    {
        double applied = SmallVectors.SetInches(inches);
        AppSettings.Store.Set(SmallThresholdKey, applied);
        return applied;
    }

    public static double SetTangencyDegrees(double degrees)
    {
        double applied = VectorEditOptions.SetTangencyDegrees(degrees);
        AppSettings.Store.Set(TangencyKey, applied);
        return applied;
    }

    public static void SetMarqueeShiftSelects(bool on)
    {
        VectorEditOptions.MarqueeShiftSelects = on;
        AppSettings.Store.Set(MarqueeShiftSelectsKey, on);
    }

    public static void SetHighlightSmallVectors(bool on)
    {
        VectorEditOptions.HighlightSmallVectors = on;
        AppSettings.Store.Set(HighlightSmallKey, on);
    }
}

/// <summary>The system's default beep (what Windows plays for a refused action).</summary>
internal static class SystemBeep
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    public static void Play()
    {
        try { if (OperatingSystem.IsWindows()) MessageBeep(0); }
        catch (Exception) { /* no sound device or no user32: nothing to do */ }
    }
}
