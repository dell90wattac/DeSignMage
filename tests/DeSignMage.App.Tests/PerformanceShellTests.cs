using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Core.Commands;
using DeSignMage.Feedback;
using Xunit;

namespace DeSignMage.App.Tests;

/// <summary>Tools ▸ Options ▸ Performance &amp; memory: the undo memory limit and the optional step limit.</summary>
[Collection("app-settings")]   // these swap the program-wide settings store and text size: one class at a time
public class PerformanceShellTests
{
    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static async Task WithSettings(Func<Task> body)
    {
        var oldStore = AppSettings.Store;
        AppSettings.Store = new SettingsStore(null);
        PerformanceSettings.Reset();
        try { await body(); }
        finally
        {
            PerformanceSettings.Reset();
            AppSettings.Store = oldStore;
        }
    }

    [Fact]
    public void Settings_are_remembered_snapped_to_the_offered_sizes_and_applied_to_new_histories()
    {
        var oldStore = AppSettings.Store;
        var store = AppSettings.Store = new SettingsStore(null);
        try
        {
            PerformanceSettings.Reset();
            Assert.Equal(1024, PerformanceSettings.UndoMemoryMb);
            Assert.Equal(1L << 30, CommandHistory.DefaultMemoryLimitBytes);
            Assert.Null(CommandHistory.DefaultMaxSteps);

            PerformanceSettings.SetUndoMemoryMb(512);
            PerformanceSettings.SetUndoMaxSteps(200);
            Assert.Equal(512L << 20, CommandHistory.DefaultMemoryLimitBytes);
            Assert.Equal(200, CommandHistory.DefaultMaxSteps);

            PerformanceSettings.Reset();                        // pretend to restart: read it back from the store
            PerformanceSettings.Load(store);
            Assert.Equal(512, PerformanceSettings.UndoMemoryMb);
            Assert.Equal(200, PerformanceSettings.UndoMaxSteps);

            PerformanceSettings.SetUndoMemoryMb(3000);          // not offered: the nearest one (2 GB, 4 GB are equally far -> the smaller list order)
            Assert.Contains(PerformanceSettings.UndoMemoryMb, PerformanceSettings.MemoryChoicesMb);
            PerformanceSettings.SetUndoMaxSteps(2);             // too small to be useful: unlimited
            Assert.Null(PerformanceSettings.UndoMaxSteps);
        }
        finally
        {
            PerformanceSettings.Reset();
            AppSettings.Store = oldStore;
        }
    }

    [AvaloniaFact]
    public async Task Options_has_a_Performance_and_memory_section_that_changes_and_applies_the_limits()
    {
        await WithSettings(() =>
        {
            var w = new MainWindow(new StartupOptions()) { Width = 1400, Height = 860 };
            w.Show();
            Flush();
            var tools = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Tools");
            tools.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "_Options...").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Flush();
            var options = Assert.IsType<OptionsWindow>(w.LastOptionsWindow);
            options.ShowPage(OptionsPages.PerformanceId);
            Flush();
            var page = Assert.IsType<PerformanceOptionsPage>(options.CurrentPage);

            Assert.Equal(5, page.MemoryCombo.ItemCount);
            Assert.Equal(2, page.MemoryCombo.SelectedIndex);            // 1 GB is the standard
            Assert.Equal("", page.StepsBox.Text);                        // empty = unlimited

            page.MemoryCombo.SelectedIndex = 0;                          // 256 MB
            Assert.Equal(256, PerformanceSettings.UndoMemoryMb);
            Assert.Equal(256L << 20, w.Session.History.MemoryLimitBytes);

            page.StepsBox.Text = "40";
            page.StepsBox.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.Equal(40, PerformanceSettings.UndoMaxSteps);
            Assert.Equal(40, w.Session.History.MaxSteps);

            // The open design trims its history at once when the step limit is lowered.
            for (int i = 0; i < 60; i++) w.Session.History.Execute(new AddObjectCommand(w.Session.Document.ActiveLayer, new DeSignMage.Core.Model.RectangleObject(10, 10)));
            Assert.Equal(40, w.Session.History.UndoList.Count);
            Assert.Equal(40, PerformanceSettings.UsageProvider!().Steps);
            Assert.Contains("undo memory for", page.UsageText.Text);

            page.StepsBox.Text = "";
            page.StepsBox.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.Null(PerformanceSettings.UndoMaxSteps);

            options.Close();
            w.Close();
            return Task.CompletedTask;
        });
    }
}
