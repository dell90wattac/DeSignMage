using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeSignMage.App.Reporting;
using DeSignMage.App.Shell;

namespace DeSignMage.App.Updates;

/// <summary>Help ▸ About: the version, and whether this copy updates itself (and what it knows about a newer version).</summary>
internal sealed class AboutDialog : Window
{
    public TextBlock TitleText { get; } = new() { Text = "DeSign Mage", FontWeight = FontWeight.SemiBold, Name = "TitleText" };
    public TextBlock VersionText { get; } = new() { Name = "VersionText" };
    public TextBlock UpdateText { get; } = new() { TextWrapping = TextWrapping.Wrap, Name = "UpdateText" };

    public AboutDialog() : this(FeedbackHost.AppVersionText, () => UpdateHost.Status, h => UpdateHost.StatusChanged += h, h => UpdateHost.StatusChanged -= h) { }

    /// <param name="status">What the updater knows right now.</param>
    /// <param name="subscribe">Starts / <paramref name="unsubscribe"/> stops listening for changes of that (called from any thread).</param>
    public AboutDialog(string version, Func<UpdateStatus> status, Action<Action> subscribe, Action<Action> unsubscribe)
    {
        Title = "About";
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DialogScale.Attach(this, 400);

        VersionText.Text = $"Version {version}";
        UpdateText.Foreground = AppTheme.Brush("Ui.Muted");
        var blurb = new TextBlock { Text = "A vector graphic design program.\n.NET 10 · Avalonia · SkiaSharp", TextWrapping = TextWrapping.Wrap };
        Content = DialogScale.Scrollable(new StackPanel
        {
            Margin = UiScale.Space(16), Spacing = UiScale.Sp(8),
            Children = { TitleText, VersionText, blurb, UpdateText },
        });

        void Refresh() => UpdateText.Text = status().Describe(version);
        void OnChanged() => Dispatcher.UIThread.Post(Refresh);
        Refresh();
        subscribe(OnChanged);
        Closed += (_, _) => unsubscribe(OnChanged);
    }
}
