using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.App.Shell;

namespace DeSignMage.App.Recovery;

/// <summary>The words of the restore prompt (kept apart from the window so they can be checked without one).</summary>
public static class RecoveryText
{
    /// <summary>"today at 3:42 PM", "yesterday at 9:05 AM", or the date and time.</summary>
    public static string When(DateTime savedUtc, DateTime nowLocal)
    {
        var local = savedUtc.ToLocalTime();
        string time = local.ToString("t");
        if (local.Date == nowLocal.Date) return $"today at {time}";
        if (local.Date == nowLocal.Date.AddDays(-1)) return $"yesterday at {time}";
        return $"{local:d} at {time}";
    }

    public static string Heading(int count) => count == 1
        ? "DeSign Mage closed before your design was saved. Restore it?"
        : $"DeSign Mage closed before {count} designs were saved. Which one do you want to restore?";

    /// <summary>Where the design belongs: its file, "not saved to a file yet" or the layout it was opened from.</summary>
    public static string Belongs(RecoveryMeta meta) =>
        meta.DocumentPath is { } path ? $"File: {path}"
        : meta.SourcePath is { } source ? $"Opened from: {source} (Save will ask for a new name)"
        : "Not saved to a file yet (Save will ask for a name)";

    public static string Kept(int others) => others == 1
        ? "Only one design can be open in this window. The other one stays saved and is offered again the next time you start the program."
        : "Only one design can be open in this window. The others stay saved and are offered again the next time you start the program.";
}

/// <summary>
/// "DeSign Mage closed before your design was saved. Restore it?" One row per design left behind, with its name and when it was backed up, and
/// Restore / Discard buttons. Restore closes the window with that design chosen; Discard asks once more (it cannot be undone) and then removes
/// the copy at once through <c>discard</c>. Closing the window with "Not now" or the X changes nothing: the question comes back at the next start.
/// </summary>
public sealed class RestorePromptDialog : Window
{
    private readonly List<Row> _rows = [];
    private readonly Func<DateTime> _nowLocal;
    private readonly Action<RecoverableDesign> _discard;
    private readonly StackPanel _list = new() { Spacing = UiScale.Sp(8) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted") };

    private sealed class Row(RecoverableDesign design)
    {
        public RecoverableDesign Design { get; } = design;
        public Border Box { get; } = new();
        public Button Restore { get; } = new Button { Content = "Restore", IsDefault = false, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);
        public Button Discard { get; } = new Button { Content = "Discard", HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);
        public Button ConfirmDiscard { get; } = new Button { Content = "Yes, discard it", HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = false }.ScaledMinWidth(110);
        public Button KeepIt { get; } = new Button { Content = "Keep it", HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = false }.ScaledMinWidth(90);
        public TextBlock Question { get; } = new() { Text = "Throw this copy away? It cannot be brought back.", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    }

    /// <summary>The design the person chose to restore (null if none).</summary>
    public RecoverableDesign? Chosen { get; private set; }

    /// <summary>The designs the person discarded in this window.</summary>
    public List<RecoverableDesign> Discarded { get; } = [];

    public TextBlock Heading { get; } = new() { Name = "RestoreHeading", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    public Button NotNowButton { get; } = new Button { Name = "RestoreNotNowButton", Content = "Not now", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);

    /// <param name="discard">Removes a design's copy for good (called at once, when the person confirms).</param>
    /// <param name="nowLocal">The clock, for "today at ..." (tests pin it).</param>
    public RestorePromptDialog(IReadOnlyList<RecoverableDesign> designs, Action<RecoverableDesign> discard, Func<DateTime>? nowLocal = null)
    {
        _discard = discard;
        _nowLocal = nowLocal ?? (() => DateTime.Now);
        Title = "DeSign Mage";
        DialogScale.Attach(this, 560);
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var design in designs) AddRow(design);
        UpdateTexts();

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = UiScale.Space(0, 8, 0, 0), Children = { NotNowButton } };
        NotNowButton.Click += (_, _) => Close();
        Content = DialogScale.Scrollable(new StackPanel
        {
            Margin = UiScale.Space(16), Spacing = UiScale.Sp(10),
            Children = { Heading, _list, _note, bottom },
        });
    }

    internal IReadOnlyList<Button> RestoreButtons => _rows.Select(r => r.Restore).ToList();
    internal IReadOnlyList<Button> DiscardButtons => _rows.Select(r => r.Discard).ToList();
    internal IReadOnlyList<Button> ConfirmDiscardButtons => _rows.Select(r => r.ConfirmDiscard).ToList();
    internal IReadOnlyList<Button> KeepButtons => _rows.Select(r => r.KeepIt).ToList();
    internal IReadOnlyList<string> RowTexts => _rows.Select(r => string.Join("\n", ((StackPanel)r.Box.Child!).Children.OfType<TextBlock>().Select(t => t.Text))).ToList();
    internal string NoteText => _note.Text ?? "";

    private void AddRow(RecoverableDesign design)
    {
        var row = new Row(design);
        var meta = design.Meta;
        var name = new TextBlock { Text = meta.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        string how = design.FromCrashCopy ? "saved at the moment the program stopped" : "backed up";
        var when = new TextBlock { Text = $"Last {how} {RecoveryText.When(meta.SavedUtc, _nowLocal())}", TextWrapping = TextWrapping.Wrap };
        var belongs = new TextBlock { Text = RecoveryText.Belongs(meta), TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted") };
        var texts = new StackPanel { Spacing = UiScale.Sp(2), Children = { name, when, belongs } };
        if (DiskFileNewer(meta))
            texts.Children.Add(new TextBlock { Text = "The file on disk was changed after this copy was made. Restoring and saving would replace those changes.", TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted") });
        texts.Children.Add(row.Question);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8), Margin = UiScale.Space(0, 6, 0, 0),
            Children = { row.Restore, row.Discard, row.ConfirmDiscard, row.KeepIt },
        };
        texts.Children.Add(buttons);
        row.Box.Child = texts;
        row.Box.BorderBrush = AppTheme.Brush("Ui.PopupBorder");
        row.Box.BorderThickness = new Avalonia.Thickness(1);
        row.Box.CornerRadius = new Avalonia.CornerRadius(3);
        row.Box.Padding = UiScale.Space(10, 8);

        row.Restore.Click += (_, _) => { Chosen = design; Close(); };
        row.Discard.Click += (_, _) => Ask(row, true);
        row.KeepIt.Click += (_, _) => Ask(row, false);
        row.ConfirmDiscard.Click += (_, _) =>
        {
            _discard(design);
            Discarded.Add(design);
            _list.Children.Remove(row.Box);
            _rows.Remove(row);
            if (_rows.Count == 0) Close();
            else UpdateTexts();
        };
        _rows.Add(row);
        _list.Children.Add(row.Box);
    }

    private static void Ask(Row row, bool asking)
    {
        row.Question.IsVisible = asking;
        row.ConfirmDiscard.IsVisible = asking;
        row.KeepIt.IsVisible = asking;
        row.Restore.IsVisible = !asking;
        row.Discard.IsVisible = !asking;
    }

    private void UpdateTexts()
    {
        Heading.Text = RecoveryText.Heading(_rows.Count);
        _note.Text = _rows.Count > 1 ? RecoveryText.Kept(_rows.Count - 1) : "";
        _note.IsVisible = _rows.Count > 1;
    }

    private static bool DiskFileNewer(RecoveryMeta meta)
    {
        try { return meta.DocumentPath is { } path && File.Exists(path) && File.GetLastWriteTimeUtc(path) > meta.SavedUtc.AddSeconds(2); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
