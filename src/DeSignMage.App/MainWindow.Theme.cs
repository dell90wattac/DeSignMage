using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DeSignMage.App.Shell;
using AvCanvas = Avalonia.Controls.Canvas;

namespace DeSignMage.App;

/// <summary>Dark mode (R19): View ▸ Dark Menus / Dark Canvas, and the two one-click switches at the right end of the top row.</summary>
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

    private void InitThemeButtons()
    {
        Setup(DarkMenusButton, "view.darkMenus", filledBand: true, () => AppTheme.DarkMenus);
        Setup(DarkCanvasButton, "view.darkCanvas", filledBand: false, () => AppTheme.DarkCanvas);

        void Setup(ToggleButton button, string commandId, bool filledBand, Func<bool> isOn)
        {
            var c = _commands.Get(commandId);
            button.Content = UiIcons.Box(WindowIcon(filledBand), 16, 16);
            ToolTip.SetTip(button, c.ToolTip);
            button.IsChecked = isOn();
            button.Click += (_, _) =>
            {
                c.Execute();
                button.IsChecked = isOn();   // the command decides; the button just shows it
            };
            void Sync() => button.IsChecked = isOn();
            AppTheme.Changed += Sync;
            Closed += (_, _) => AppTheme.Changed -= Sync;
        }
    }

    /// <summary>
    /// A small window: Dark Menus fills its top band (the menus), Dark Canvas fills the area below it (the canvas). The drawing uses the
    /// icon brush, so it shows in both modes.
    /// </summary>
    private static Control WindowIcon(bool filledBand)
    {
        var ink = AppTheme.Brush("Ui.Icon");
        var frame = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M1.5,2.5 L14.5,2.5 L14.5,13.5 L1.5,13.5 Z"), Stroke = ink, StrokeThickness = 1 };
        var fill = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse(filledBand ? "M1.5,2.5 L14.5,2.5 L14.5,6 L1.5,6 Z" : "M1.5,6 L14.5,6 L14.5,13.5 L1.5,13.5 Z"),
            Fill = ink,
        };
        return new AvCanvas { Width = 16, Height = 16, Children = { frame, fill } };
    }
}
