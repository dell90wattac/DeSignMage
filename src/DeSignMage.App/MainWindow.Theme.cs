using DeSignMage.App.Shell;

namespace DeSignMage.App;

/// <summary>Dark mode (R19): View ▸ Dark Menus / Dark Canvas (the two one-click buttons that were in the top row are gone, R38).</summary>
public partial class MainWindow
{
    private void RegisterThemeCommands()
    {
        // No shortcuts: the user approves shortcuts before they are wired.
        _commands.Add(new AppCommand("view.darkMenus", "Dark _Menus", () => AppTheme.SetDarkMenus(!AppTheme.DarkMenus))
        {
            IsChecked = () => AppTheme.DarkMenus,
        });
        _commands.Add(new AppCommand("view.darkCanvas", "Dark _Canvas", () => AppTheme.SetDarkCanvas(!AppTheme.DarkCanvas))
        {
            IsChecked = () => AppTheme.DarkCanvas,
        });
    }

    /// <summary>Tools ▸ Show Transparency (R27): the page as a checkerboard. Display only; no shortcut.</summary>
    private void RegisterTransparencyCommand()
    {
        _commands.Add(new AppCommand("tools.showTransparency", "Show _Transparency", () => AppTheme.SetShowTransparency(!AppTheme.ShowTransparency))
        {
            IsChecked = () => AppTheme.ShowTransparency,
        });
    }

}
