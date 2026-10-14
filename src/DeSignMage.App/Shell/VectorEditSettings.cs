using DeSignMage.Core.Editing;
using DeSignMage.Feedback;

namespace DeSignMage.App.Shell;

/// <summary>
/// The "Vector editing" choices (Tools ▸ Options): the small-vector threshold. It is remembered in the settings store and pushed into
/// <see cref="SmallVectors"/>, which the Vector Edit tool (node sizes, the delete box) and the Info Line count follow.
/// </summary>
public static class VectorEditSettings
{
    public const string SmallThresholdKey = "vector.smallThresholdInches";

    /// <summary>Reads the remembered threshold and applies it (call once at startup).</summary>
    public static void Load(SettingsStore store) =>
        SmallVectors.SetInches(store.Get(SmallThresholdKey, SmallVectors.ThresholdInches));

    /// <summary>Applies and remembers a threshold in inches (kept within the allowed range); returns what was applied.</summary>
    public static double SetSmallThresholdInches(double inches)
    {
        double applied = SmallVectors.SetInches(inches);
        AppSettings.Store.Set(SmallThresholdKey, applied);
        return applied;
    }
}
