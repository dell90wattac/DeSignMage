using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Import;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using static DeSignMage.App.Shell.DialogParts;

namespace DeSignMage.App.Shell;

/// <summary>
/// Dragging a PDF, Illustrator, EPS or SVG file onto the window: what is about to come in, shown on the transparency checkerboard, with the few
/// choices there are (the page of a multi-page file, one group or loose objects) and what could not be carried over from the file, in plain words.
/// Styled like the Export dialog (same parts, same fixed size scaled by the text size). The file is read on a worker thread, so the dialog opens at
/// once and stays responsive; a newer choice cancels an older read or preview. Import is enabled only when something was read.
/// </summary>
internal sealed class ImportDialog : Window
{
    /// <summary>A typed page number waits this long before the file is read again.</summary>
    internal const int ReadDelayMs = 250;
    internal const int PreviewDelayMs = 90;

    private readonly string _path;
    private readonly IVectorImporter _importer;

    internal TextBlock FileText { get; } = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    internal TextBlock FileNote { get; } = Note();
    internal TextBox PageBox { get; } = new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right }.ScaledWidth(64);
    internal TextBlock PageOf { get; } = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Unit") };
    internal CheckBox GroupBox { get; } = new() { Content = "Import as one group" };
    internal TextBlock WarningsText { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal TextBlock SummaryText { get; } = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = Avalonia.Media.TextAlignment.Center };
    internal TextBlock ErrorText { get; } = new() { Foreground = AppTheme.Brush("Ui.Error"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    internal Button OkButton { get; } = new Button { Content = "Import", IsDefault = true, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);
    internal Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);

    private readonly Control _pageSection, _warningsSection;
    private readonly Border _previewPane = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ClipToBounds = true };
    private readonly Image _previewImage = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _previewEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted"), TextAlignment = Avalonia.Media.TextAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private CancellationTokenSource? _readCts, _previewCts;
    private Task _readTask = Task.CompletedTask, _previewTask = Task.CompletedTask;
    private Bitmap? _shown;
    private Document? _source;
    private ExportPlan? _plan;
    private int _pageCount = 1, _page = 1, _readPage;
    private bool _building = true;

    /// <summary>What was chosen when Import was pressed; null otherwise.</summary>
    public ImportChoice? Result { get; private set; }

    /// <summary>How many times the file was read, and the previews drawn, and the last preview's size and area. For tests.</summary>
    internal int ReadsDone { get; private set; }
    internal int PreviewsDrawn { get; private set; }
    internal (int Width, int Height, Rect2 Area)? LastPreview { get; private set; }
    internal Task ReadTask => _readTask;
    internal Task PreviewTask => _previewTask;
    internal Document? Source => _source;
    internal ExportPlan? Plan => _plan;
    internal int Page => _page;

    public ImportDialog(string path, IVectorImporter importer, ImportSettings settings)
    {
        _path = path;
        _importer = importer;
        Title = "Import";
        DialogScale.Attach(this, 780, 540);
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        FileText.Text = Path.GetFileName(path);
        FileNote.Text = "Reading the file...";
        GroupBox.IsChecked = settings.AsGroup;
        _pageSection = Section("Pages", Stack(Row("Page", WithUnit(PageBox, "")), Row("", PageOf)));
        _warningsSection = Section("What could not be carried over", Stack(WarningsText));
        _pageSection.IsVisible = false;
        _warningsSection.IsVisible = false;

        var form = new StackPanel { Spacing = UiScale.Sp(16) };
        form.Children.Add(Section("File", Stack(FileText, FileNote)));
        form.Children.Add(_pageSection);
        form.Children.Add(Section("Options", Stack(GroupBox, Note("The imported objects arrive selected and hang on the pointer: click where they should go. Esc cancels."))));
        form.Children.Add(_warningsSection);
        var formScroll = new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = UiScale.Space(0, 0, 10, 0) };

        _previewPane.Background = AppTheme.Brush("Ui.Panel");
        _previewPane.BorderBrush = AppTheme.Brush("Ui.ChromeBorder");
        _previewPane.Child = new Grid { Children = { _previewImage, _previewEmpty } };
        _previewEmpty.Text = "Reading...";
        _previewPane.SizeChanged += (_, _) => SchedulePreview();
        var previewColumn = new DockPanel { LastChildFill = true };
        var caption = new StackPanel { Spacing = UiScale.Sp(4), Margin = UiScale.Space(0, 8, 0, 0), Children = { SummaryText } };
        DockPanel.SetDock(caption, Dock.Bottom);
        var previewHeader = SectionLabel("Preview");
        DockPanel.SetDock(previewHeader, Dock.Top);
        previewColumn.Children.Add(caption);
        previewColumn.Children.Add(previewHeader);
        previewColumn.Children.Add(_previewPane);

        var columns = new Grid { ColumnSpacing = UiScale.Sp(22) };
        columns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10, GridUnitType.Star)));
        columns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(11, GridUnitType.Star)));
        Grid.SetColumn(formScroll, 0);
        Grid.SetColumn(previewColumn, 1);
        columns.Children.Add(formScroll);
        columns.Children.Add(previewColumn);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8), Children = { CancelButton, OkButton } };
        var bottom = new DockPanel { Margin = UiScale.Space(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(ErrorText);
        var root = new DockPanel { Margin = UiScale.Space(18) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(columns);
        Content = root;

        PageBox.Text = "1";
        PageBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_building) OnPageTyped(); };
        OkButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => Close();
        Opened += (_, _) => SchedulePreview();
        Closed += (_, _) => { _readCts?.Cancel(); _previewCts?.Cancel(); _shown?.Dispose(); };
        _building = false;
        StartRead(1, delay: 0);
    }

    // ---------------------------------------------------------------- reading

    private void OnPageTyped()
    {
        var text = PageBox.Text?.Trim() ?? "";
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > _pageCount)
        {
            ErrorText.Text = $"Enter a page from 1 to {_pageCount}.";
            OkButton.IsEnabled = false;
            return;
        }
        ErrorText.Text = "";
        if (n == _readPage) { OkButton.IsEnabled = _plan is not null; return; }
        StartRead(n, ReadDelayMs);
    }

    /// <summary>Reads page <paramref name="page"/> on a worker thread; a newer request cancels this one.</summary>
    private void StartRead(int page, int delay)
    {
        _readCts?.Cancel();
        var cts = _readCts = new CancellationTokenSource();
        _page = page;
        OkButton.IsEnabled = false;
        if (_source is null) FileNote.Text = "Reading the file...";
        _readTask = ReadAsync(page, delay, cts.Token);
    }

    private async Task ReadAsync(int page, int delay, CancellationToken cancel)
    {
        try
        {
            if (delay > 0) await Task.Delay(delay, cancel);
            var path = _path;
            var importer = _importer;
            var (result, plan) = await Task.Run(() =>
            {
                var r = importer.Read(path, page);
                cancel.ThrowIfCancellationRequested();
                return (r, BuildPlan(r.Document));
            }, cancel);
            if (cancel.IsCancellationRequested) return;
            Show(result, plan, page);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!cancel.IsCancellationRequested) Fail(e.Message);
        }
        catch (Exception e)
        {
            if (!cancel.IsCancellationRequested) Fail($"The file could not be read ({e.GetType().Name}: {e.Message}). It may be damaged.");
        }
    }

    private void Fail(string message)
    {
        _source = null;
        _plan = null;
        FileNote.Text = "";
        ErrorText.Text = message;
        SummaryText.Text = "";
        _previewImage.Source = null;
        _previewEmpty.Text = "Nothing to preview";
        _previewEmpty.IsVisible = true;
        OkButton.IsEnabled = false;
        ReadsDone++;
    }

    private void Show(VectorReadResult result, ExportPlan? plan, int page)
    {
        _source = result.Document;
        _plan = plan;
        _pageCount = Math.Max(1, result.PageCount);
        _readPage = page;
        ReadsDone++;

        _pageSection.IsVisible = _pageCount > 1;
        PageOf.Text = $"of {_pageCount}";
        FileNote.Text = _pageCount > 1 ? $"{_pageCount} pages" : "1 page";

        var warnings = result.Warnings;
        _warningsSection.IsVisible = warnings.Count > 0;
        WarningsText.Text = string.Join(Environment.NewLine, warnings.Take(10).Select(w => "• " + w)) + (warnings.Count > 10 ? $"{Environment.NewLine}… and {warnings.Count - 10} more." : "");

        if (plan is null || plan.ObjectCount == 0)
        {
            ErrorText.Text = "There is nothing to import on this page.";
            SummaryText.Text = "";
            OkButton.IsEnabled = false;
            _previewImage.Source = null;
            _previewEmpty.Text = "Nothing to preview";
            _previewEmpty.IsVisible = true;
            return;
        }
        ErrorText.Text = "";
        var a = plan.Area;
        int n = plan.ObjectCount;
        SummaryText.Text = $"{n} object{(n == 1 ? "" : "s")} · {(a.Width / Units.PointsPerInch).ToString("0.##", CultureInfo.InvariantCulture)} × {(a.Height / Units.PointsPerInch).ToString("0.##", CultureInfo.InvariantCulture)} in";
        OkButton.IsEnabled = true;
        _drawnPlan = null;
        SchedulePreview();
    }

    /// <summary>What a page brings in: the drawn objects of its printable layers, with the area they cover (strokes and soft-effect halos included). Null when there is nothing.</summary>
    internal static ExportPlan? BuildPlan(Document doc)
    {
        var plan = ExportPlan.Create(doc, new ExportOptions { IncludePasteboard = true });
        var area = Rect2.Empty;
        foreach (var layer in plan.Layers)
            foreach (var o in layer.Objects) area = area.Union(ExportPlan.VisualBounds(o));
        return area.IsEmpty || plan.ObjectCount == 0 ? null : new ExportPlan(area, plan.Layers);
    }

    /// <summary>
    /// Detached copies of what the plan holds, ready to be placed: each top-level object (a group with its members) once, in drawing order. With
    /// <paramref name="asGroup"/> and more than one, they come as a single group named <paramref name="name"/>.
    /// </summary>
    internal static List<DocObject> BuildItems(ExportPlan plan, bool asGroup, string name)
    {
        var tops = new List<DocObject>();
        var seen = new HashSet<DocObject>();
        foreach (var layer in plan.Layers)
            foreach (var o in layer.Objects)
                if (seen.Add(o.TopLevel)) tops.Add(o.TopLevel);
        var copies = tops.Select(t => t.Clone()).ToList();
        return asGroup && copies.Count > 1 ? [GroupObject.Of(copies, name)] : copies;
    }

    private void Accept()
    {
        if (_source is null || _plan is null || !OkButton.IsEnabled) return;
        Result = new ImportChoice(_source, _plan, GroupBox.IsChecked == true, _page, Path.GetFileName(_path));
        Close();
    }

    internal ImportSettings CurrentSettings() => new(GroupBox.IsChecked == true);

    // ---------------------------------------------------------------- preview

    private ExportPlan? _drawnPlan;

    private void SchedulePreview()
    {
        if (!IsLoaded && !IsVisible) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        _previewTask = DrawPreviewAsync(cts.Token);
    }

    private async Task DrawPreviewAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(PreviewDelayMs, cancel);
            var plan = _plan;
            var source = _source;
            double scale = RenderScaling > 0 ? RenderScaling : 1;
            int boxW = (int)Math.Floor(Math.Max(0, _previewPane.Bounds.Width - 2) * scale), boxH = (int)Math.Floor(Math.Max(0, _previewPane.Bounds.Height - 2) * scale);
            if (plan is null || source is null || boxW < 8 || boxH < 8) return;
            if (ReferenceEquals(_drawnPlan, plan) && _shown is not null && _shownBox == (boxW, boxH)) return;
            var theme = AppTheme.DarkMenus ? CanvasTheme.Dark : CanvasTheme.Light;
            var options = new ExportPreviewOptions(boxW, boxH, theme.CheckerA, theme.CheckerB, false, Math.Max(4, (int)Math.Round(8 * scale)));
            var bitmap = await Task.Run(() => ExportPreview.Render(source, plan, options, cancel), cancel);
            if (cancel.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }
            ShowPreview(bitmap, scale, plan);
            _drawnPlan = plan;
            _shownBox = (boxW, boxH);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            _previewEmpty.Text = "Nothing to preview";
            _previewEmpty.IsVisible = true;
        }
    }

    private (int, int) _shownBox;

    private void ShowPreview(SKBitmap bitmap, double scale, ExportPlan plan)
    {
        _shown?.Dispose();
        using (bitmap)
        {
            var wb = new WriteableBitmap(new PixelSize(bitmap.Width, bitmap.Height), new Vector(96 * scale, 96 * scale), PixelFormat.Rgba8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                var src = bitmap.GetPixelSpan();
                int rowBytes = bitmap.Width * 4;
                for (int y = 0; y < bitmap.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(src.Slice(y * bitmap.RowBytes, rowBytes).ToArray(), 0, fb.Address + y * fb.RowBytes, rowBytes);
            }
            _shown = wb;
            _previewImage.Source = wb;
            LastPreview = (bitmap.Width, bitmap.Height, plan.Area);
        }
        _previewEmpty.IsVisible = false;
        PreviewsDrawn++;
    }
}
