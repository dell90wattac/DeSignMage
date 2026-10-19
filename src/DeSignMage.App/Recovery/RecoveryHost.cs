using DeSignMage.Feedback;

namespace DeSignMage.App.Recovery;

/// <summary>
/// The program-wide recovery store. Null until <see cref="StartForReal"/> (from <c>Program.Main</c>) sets it, so every test and every
/// headless run is without auto-save and never touches the real folder; a test that wants it assigns its own store in a temp folder.
/// </summary>
internal static class RecoveryHost
{
    public static RecoveryStore? Current { get; set; }

    public static RecoveryStore StartForReal(AppPaths paths)
    {
        Current = new RecoveryStore(paths.Recovery, paths.Crash);
        return Current;
    }
}
