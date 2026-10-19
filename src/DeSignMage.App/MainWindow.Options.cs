using Avalonia;
using Avalonia.Controls;
using DeSignMage.App.Shell;

namespace DeSignMage.App;

/// <summary>Tools ▸ Options and View ▸ Interface Text Size (requirement R12).</summary>
public partial class MainWindow
{
    /// <summary>Menu ids of View ▸ Interface Text Size, in order.</summary>
    private static string[] TextSizeMenuIds =>   // a property: static field initializers in different partial files run in no fixed order
        [.. UiScale.Presets.Select(p => $"ui.size.{(int)Math.Round(p * 100)}"), "-", "ui.size.custom"];

    /// <summary>The Options window opened last (for tests).</summary>
    internal OptionsWindow? LastOptionsWindow { get; private set; }

    private void RegisterOptionsCommands()
    {
        foreach (var p in UiScale.Presets)
        {
            double scale = p;
            int pct = (int)Math.Round(p * 100);
            _commands.Add(new AppCommand($"ui.size.{pct}", $"{pct}%", () => UiScale.SetScale(scale))
            {
                IsChecked = () => UiScale.IsPreset(scale, UiScale.Scale),
            });
        }
        // Anything between the presets (set with the slider): checked, and it opens the slider.
        _commands.Add(new AppCommand("ui.size.custom", "Other size...", () => ShowOptions(OptionsPages.AccessibilityId))
        {
            IsChecked = () => !UiScale.Presets.Any(p => UiScale.IsPreset(p, UiScale.Scale)),
        });
        _commands.Add(new AppCommand("tools.options", "_Options...", () => ShowOptions(null)));
    }

    private void ShowOptions(string? pageId)
    {
        var window = new OptionsWindow(pageId);
        LastOptionsWindow = window;
        _ = window.ShowDialog(this);
    }

    /// <summary>Sizes that belong to this window rather than to a control (the right dock column), re-applied when the text size changes.</summary>
    private void InitUiScale()
    {
        void Apply()
        {
            ApplyDockLayout();   // the right dock's width follows the text size; a closed dock takes none
            PaletteStripBorder.Width = 22 * UiScale.IconScale;
            // The Context Bar is one line, or two when its controls don't fit side by side (its height follows); the Dock's tab band beside it
            // takes the same height.
            ContextBarBorder.MinHeight = ContextBars.OneLineHeight;

            // Padding, margins and gaps grow at a third of the text's rate (UiScale.SpaceScale); text and icons keep their own scales.
            double sp = UiScale.SpaceScale;
            TopRowBorder.Height = Math.Max(28 * UiScale.IconScale, 28 * UiScale.Scale) + UiScale.Sp(4);   // one fixed height for menus + toolbar
            MainToolbar.Margin = UiScale.Space(8, 0);
            MainToolbar.Spacing = 0;
            FeedbackButton.Margin = UiScale.Space(4, 0, 8, 0);
            ContextBarBorder.Padding = UiScale.Space(8, ContextBars.BarPaddingY);
            ProjectColorBarHost.Margin = UiScale.Space(8, 2);
            ProjectColorBarHost.Spacing = UiScale.Sp(8);
            ProjectColorBarHost.LineSpacing = UiScale.Sp(4);
            InfoBar.Spacing = UiScale.Sp(12);
            InfoBar.Margin = new Thickness(0, 0, UiScale.Sp(8), 0);
            MeasureHost.Margin = new Thickness(0);
            ToolboxHost.Margin = UiScale.Space(4, 8);
            ToolboxHost.Spacing = UiScale.Sp(2);
            ColorMixerPanel.Padding = LayersPanel.Padding = HistoryPanel.Padding = UiScale.Space(8, 8, 8, 4);
            ColorMixerHost.Spacing = UiScale.Sp(6);
            ColorMixerHost.Margin = new Thickness(0, 0, 0, UiScale.Sp(4));        }
        Apply();
        UiScale.Changed += Apply;
        Closed += (_, _) => UiScale.Changed -= Apply;
        // The window may open smaller than the maximum width assumed above; re-check once its real size is known.
        Opened += (_, _) => Apply();

        // Options ▸ Performance & memory: a lowered undo limit trims the open design's history at once, and the page can show what it uses.
        void TrimHistory() => _session?.History.EnforceLimits();
        PerformanceSettings.Changed += TrimHistory;
        PerformanceSettings.UsageProvider = () => (_session.History.UndoList.Count, _session.History.UndoMemoryBytes);
        Closed += (_, _) =>
        {
            PerformanceSettings.Changed -= TrimHistory;
            PerformanceSettings.UsageProvider = null;
        };
    }
}
