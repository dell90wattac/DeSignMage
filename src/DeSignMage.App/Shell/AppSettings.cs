using DeSignMage.Feedback;

namespace DeSignMage.App.Shell;

/// <summary>
/// The program's remembered values (interface text size, font-list size, performance choices, ...). One store for the whole
/// program: <c>AppSettings.Store.Get("section.name", fallback)</c> / <c>.Set("section.name", value)</c>. It is the same file the
/// feedback name lives in (<see cref="AppPaths.SettingsFile"/>); until the program sets it up it keeps values in memory only.
/// </summary>
public static class AppSettings
{
    public static SettingsStore Store { get; set; } = new(null);
}
