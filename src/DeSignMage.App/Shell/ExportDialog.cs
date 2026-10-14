using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Rendering;
using DeSignMage.Rendering.Export;
using SkiaSharp;
using static DeSignMage.App.Shell.DialogParts;

namespace DeSignMage.App.Shell;

/// <summary>What the user asked for in the Export dialog.</summary>
internal sealed record ExportRequest(ExportFormat Format, ExportOptions Options)
{
    public string Extension => ExportFormats.Get(Format).Extension;
}

/// <summary>
/// The choices the Export dialog starts with (the last ones used in this session). The size is the last number typed: a width or a height
/// (the other follows the proportions of whatever is exported next), so "3200 wide" stays 3200 wide for a selection too.
/// </summary>
internal sealed record ExportSettings(
    ExportFormat Format = ExportFormat.Png, int? PixelWidth = null, int? PixelHeight = null, int JpegQuality = 90, int PngCompression = 6,
    bool IncludePasteboard = false, bool TextAsOutlines = true, bool SelectionOnly = false, DocColor? JpegBackground = null);

/// <summary>One entry of the format drop-down.</summary>
internal sealed record ExportFormatInfo(ExportFormat Format, string Name, string Extension, string Description, string PickerLabel, bool IsPicture);

/// <summary>The formats of File ▸ Export, in the order of the drop-down.</summary>
internal static class ExportFormats
{
    public static readonly IReadOnlyList<ExportFormatInfo> All =
    [
        new(ExportFormat.Png, "PNG picture", ".png", "A picture with transparency wherever nothing is drawn.", "PNG pictures (*.png)", true),
        new(ExportFormat.Jpeg, "JPEG picture", ".jpg", "A smaller picture on a solid background (white unless you choose another). JPEG cannot be transparent.", "JPEG pictures (*.jpg)", true),
        new(ExportFormat.Pdf, "PDF document", ".pdf", "Vector page for printing and sharing. Text becomes curves.", "PDF documents (*.pdf)", false),
        new(ExportFormat.Svg, "SVG vector graphic", ".svg", "Vector graphic for the web and drawing programs.", "SVG graphics (*.svg)", false),
        new(ExportFormat.Eps, "EPS (PostScript)", ".eps", "Vector file for print shops and page layout programs.", "EPS files (*.eps)", false),
        new(ExportFormat.Ai, "AI (Adobe Illustrator)", ".ai", "A PDF that Illustrator and other drawing programs open. Text becomes curves.", "Illustrator files (*.ai)", false),
        new(ExportFormat.Lyt, "Legacy layout (.lyt)", ".lyt", "The legacy program's layout file. The whole design is written.", "Legacy layouts (*.lyt)", false),
    ];

    public static ExportFormatInfo Get(ExportFormat format) => All.First(f => f.Format == format);
}

/// <summary>
/// File ▸ Export...: one fixed-size dialog. On the left: the file type, what to export, the size in pixels (typing one number fills in the other,
/// following the proportions of what is exported) and the options that belong to the chosen type. On the right: a live preview of exactly the area
/// that will be written. Only what applies to the chosen type is shown. The file name is asked for afterwards.
/// </summary>
internal sealed class ExportDialog : Window
{
    /// <summary>The preview waits this long after the last change before it draws, so quick clicking and typing never queue work up.</summary>
    internal const int PreviewDelayMs = 90;

    private readonly Document _doc;
    private readonly IReadOnlyCollection<DocObject> _selection;
    private readonly Dictionary<(bool Selection, bool Pasteboard), ExportPlan?> _plans = [];
    private readonly Dictionary<(bool Selection, bool Pasteboard), string> _planErrors = [];

    internal ComboBox FormatBox { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    internal TextBlock FormatDescription { get; } = Note();
    internal RadioButton PageRadio { get; } = new() { Content = "Whole page", GroupName = "area" };
    internal RadioButton SelectionRadio { get; } = new() { GroupName = "area" };
    internal CheckBox PasteboardBox { get; } = new() { Content = "Include objects outside the page" };
    internal TextBox WidthBox { get; } = SizeField();
    internal TextBox HeightBox { get; } = SizeField();
    internal TextBlock SizeNote { get; } = Note();
    internal TextBlock VectorSize { get; } = new() { VerticalAlignment = VerticalAlignment.Center };
    internal Slider JpegSlider { get; } = new() { Minimum = 1, Maximum = 100, Value = 90, SmallChange = 1, LargeChange = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Slider PngSlider { get; } = new() { Minimum = 0, Maximum = 9, Value = 6, SmallChange = 1, LargeChange = 1, TickFrequency = 1, IsSnapToTickEnabled = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal RadioButton OutlinesRadio { get; } = new() { Content = "Letters as outlines (looks the same everywhere)", GroupName = "text" };
    internal RadioButton LiveTextRadio { get; } = new() { Content = "Real text (editable; needs the font on the other computer)", GroupName = "text" };
    internal Button OkButton { get; } = new Button { Content = "Export...", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);
    internal Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);
    internal TextBlock ErrorText { get; } = new() { Foreground = AppTheme.Brush("Ui.Error"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    internal TextBlock SummaryText { get; } = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = Avalonia.Media.TextAlignment.Center };
    internal TextBlock PreviewNote { get; } = Note();

    /// <summary>JPEG only: the solid color behind the art (a swatch with its hex code; click to choose another).</summary>
    internal Button BackgroundButton { get; } = new Button { Focusable = true, HorizontalAlignment = HorizontalAlignment.Left, Padding = UiScale.Space(6, 3) };
    private readonly TextBlock _backgroundHex = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _backgroundSwatch = new Border { BorderThickness = new Thickness(1), BorderBrush = AppTheme.Brush("Ui.SwatchBorder") }.ScaledWidth(18).ScaledHeight(18);
    private DocColor _background = DocColor.White;

    /// <summary>The background color chosen for a JPEG.</summary>
    internal DocColor JpegBackground => _background;

    private readonly TextBlock _jpegValue = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextAlignment = Avalonia.Media.TextAlignment.Right };
    private readonly TextBlock _pngValue = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextAlignment = Avalonia.Media.TextAlignment.Right };
    private readonly Control _areaSection, _sizeSection, _pictureSizeRow, _vectorSizeRow, _jpegSection, _pngSection, _textSection;
    private readonly Control _pasteboardRow;
    private readonly Border _previewPane = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ClipToBounds = true };
    private readonly Image _previewImage = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _previewEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted"), TextAlignment = Avalonia.Media.TextAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private bool _updating, _building = true, _sizeValid = true;
    private bool _widthBasis = true;      // the last number typed was a width (else a height)
    private int _basisValue;
    private ExportPlan? _plan;
    private string? _problem;

    private CancellationTokenSource? _previewCts;
    private Task _previewTask = Task.CompletedTask;
    private Bitmap? _shownPreview;
    private (ExportPlan? Plan, bool White, DocColor? Solid, int Width, int Height)? _drawnKey;   // what the picture on show was drawn from

    public ExportRequest? Result { get; private set; }

    /// <summary>How many previews were drawn so far, and the last one's size and area. For tests and the performance check.</summary>
    internal int PreviewsDrawn { get; private set; }
    internal (int Width, int Height, Rect2 Area)? LastPreview { get; private set; }
    internal double LastPreviewMs { get; private set; }

    /// <summary>The color of the top left pixel of the preview on show (what is behind the art there). For tests.</summary>
    internal SKColor LastPreviewCorner { get; private set; }

    /// <summary>Completes when the preview for the choices made so far has been drawn (or cancelled by newer choices).</summary>
    internal Task PreviewTask => _previewTask;

    public ExportDialog(Document doc, IReadOnlyCollection<DocObject> selection, ExportSettings settings)
    {
        _doc = doc;
        _selection = selection;
        Title = "Export";
        DialogScale.Attach(this, 820, 580);
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var f in ExportFormats.All) FormatBox.Items.Add(new ComboBoxItem { Content = f.Name, Tag = f.Format });
        SelectionRadio.Content = $"Selected objects only ({selection.Count})";
        JpegSlider.ValueChanged += (_, _) => { _jpegValue.Text = ((int)Math.Round(JpegSlider.Value)).ToString(CultureInfo.InvariantCulture); if (!_building) UpdateState(); };
        PngSlider.ValueChanged += (_, _) => { _pngValue.Text = ((int)Math.Round(PngSlider.Value)).ToString(CultureInfo.InvariantCulture); if (!_building) UpdateState(); };
        OkButton.Click += (_, _) =>
        {
            if (!TryBuild(out var request, out _)) return;
            Result = request;
            Close();
        };
        CancelButton.Click += (_, _) => Close();

        // ---- left column: sections, each a small label over its rows (labels in one column, fields lined up in the next)
        PasteboardBox.Margin = UiScale.Space(0, 4, 0, 0);
        _pasteboardRow = PasteboardBox;
        _areaSection = Section("What to export", Stack(PageRadio, SelectionRadio, PasteboardBox));

        _pictureSizeRow = Stack(Row("Width", WithUnit(WidthBox, "px")), Row("Height", WithUnit(HeightBox, "px")), Row("", SizeNote));
        _vectorSizeRow = Row("Page size", VectorSize);
        _sizeSection = Section("Size", Stack(_pictureSizeRow, _vectorSizeRow));

        var backgroundContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8) };
        backgroundContent.Children.Add(_backgroundSwatch);
        backgroundContent.Children.Add(_backgroundHex);
        BackgroundButton.Content = backgroundContent;
        ToolTip.SetTip(BackgroundButton, "The color behind the art: a JPEG cannot be transparent, so the empty corners and every soft edge blend into it.");
        BackgroundButton.Click += async (_, _) => await ChooseBackgroundAsync();
        _jpegSection = Section("Picture quality", Stack(
            Row("Quality", SliderRow(JpegSlider, _jpegValue)),
            Row("", Note("A higher quality makes a bigger file.")),
            Row("Background", BackgroundButton)));
        _pngSection = Section("Picture quality", Stack(
            Row("Compression", SliderRow(PngSlider, _pngValue)),
            Row("", Note("Higher compression makes a smaller file and takes longer. The picture is the same."))));
        _textSection = Section("Text", Stack(OutlinesRadio, LiveTextRadio));

        var form = new StackPanel { Spacing = UiScale.Sp(16) };
        form.Children.Add(Section("File type", Stack(FormatBox, FormatDescription)));
        form.Children.Add(_areaSection);
        form.Children.Add(_sizeSection);
        form.Children.Add(_jpegSection);
        form.Children.Add(_pngSection);
        form.Children.Add(_textSection);
        var formScroll = new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = UiScale.Space(0, 0, 10, 0) };

        // ---- right column: the preview
        _previewPane.Background = AppTheme.Brush("Ui.Panel");
        _previewPane.BorderBrush = AppTheme.Brush("Ui.ChromeBorder");
        _previewPane.Child = new Grid { Children = { _previewImage, _previewEmpty } };
        _previewPane.SizeChanged += (_, _) => SchedulePreview();
        var previewColumn = new DockPanel { LastChildFill = true };
        var caption = new StackPanel { Spacing = UiScale.Sp(4), Margin = UiScale.Space(0, 8, 0, 0), Children = { SummaryText, PreviewNote } };
        DockPanel.SetDock(caption, Dock.Bottom);
        var previewHeader = SectionLabel("Preview");
        DockPanel.SetDock(previewHeader, Dock.Top);
        previewColumn.Children.Add(caption);
        previewColumn.Children.Add(previewHeader);
        previewColumn.Children.Add(_previewPane);

        var columns = new Grid { ColumnSpacing = UiScale.Sp(22) };
        columns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(11, GridUnitType.Star)));
        columns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10, GridUnitType.Star)));
        Grid.SetColumn(formScroll, 0);
        Grid.SetColumn(previewColumn, 1);
        columns.Children.Add(formScroll);
        columns.Children.Add(previewColumn);

        // ---- bottom: the message line on the left, the buttons bottom right
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

        // ---- start from the remembered choices
        _updating = true;
        FormatBox.SelectedIndex = ExportFormats.All.ToList().FindIndex(f => f.Format == settings.Format);
        if (FormatBox.SelectedIndex < 0) FormatBox.SelectedIndex = 0;
        (settings.SelectionOnly && selection.Count > 0 ? SelectionRadio : PageRadio).IsChecked = true;
        PasteboardBox.IsChecked = settings.IncludePasteboard;
        JpegSlider.Value = Math.Clamp(settings.JpegQuality, 1, 100);
        SetBackground(settings.JpegBackground ?? DocColor.White);
        PngSlider.Value = Math.Clamp(settings.PngCompression, 0, 9);
        _jpegValue.Text = ((int)Math.Round(JpegSlider.Value)).ToString(CultureInfo.InvariantCulture);
        _pngValue.Text = ((int)Math.Round(PngSlider.Value)).ToString(CultureInfo.InvariantCulture);
        (settings.TextAsOutlines ? OutlinesRadio : LiveTextRadio).IsChecked = true;
        _updating = false;

        FormatBox.SelectionChanged += (_, _) => { if (_building) return; UpdateState(); SchedulePreview(); };
        foreach (var r in new[] { PageRadio, SelectionRadio }) r.IsCheckedChanged += (_, _) => { if (!_building) OnAreaChanged(); };
        PasteboardBox.IsCheckedChanged += (_, _) => { if (!_building) OnAreaChanged(); };
        foreach (var r in new[] { OutlinesRadio, LiveTextRadio }) r.IsCheckedChanged += (_, _) => { if (!_building) UpdateState(); };
        WidthBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OnSizeTyped(width: true); };
        HeightBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OnSizeTyped(width: false); };

        // the first size: the remembered number, or 3200 pixels along the longer side
        _plan = CurrentPlan();
        if (settings.PixelWidth is { } sw) { _widthBasis = true; _basisValue = sw; }
        else if (settings.PixelHeight is { } sh) { _widthBasis = false; _basisValue = sh; }
        else
        {
            var a = _plan?.Area ?? doc.Page.Bounds;
            _widthBasis = a.Width >= a.Height;
            _basisValue = 3200;
        }
        ShowSizeFromBasis();
        _building = false;
        UpdateState();
        Opened += (_, _) => SchedulePreview();
        Closed += (_, _) => { _previewCts?.Cancel(); _shownPreview?.Dispose(); };
    }

    // ---------------------------------------------------------------- building blocks

    private static TextBox SizeField() => new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right }.ScaledWidth(84);

    private static Control SliderRow(Slider slider, TextBlock value)
    {
        var grid = new Grid { ColumnSpacing = UiScale.Sp(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        value.ScaledMinWidth(30);
        Grid.SetColumn(slider, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }

    // ---------------------------------------------------------------- choices

    internal ExportFormat Format => FormatBox.SelectedItem is ComboBoxItem { Tag: ExportFormat f } ? f : ExportFormat.Png;
    private ExportFormatInfo Info => ExportFormats.Get(Format);
    private bool IsLegacy => Format == ExportFormat.Lyt;

    private bool SelectionChosen => SelectionRadio.IsChecked == true && _selection.Count > 0 && !IsLegacy;

    /// <summary>What is exported with these choices (the legacy layout always writes the whole design, pasteboard included).</summary>
    private (bool Selection, bool Pasteboard) AreaKey() =>
        IsLegacy ? (false, true) : (SelectionChosen, !SelectionChosen && PasteboardBox.IsChecked == true);

    /// <summary>The plan of the current choices; planned once per choice, so typing in a size field plans nothing.</summary>
    private ExportPlan? CurrentPlan()
    {
        var key = AreaKey();
        if (!_plans.TryGetValue(key, out var plan))
        {
            try
            {
                plan = ExportPlan.Create(_doc, new ExportOptions { Scope = key.Selection ? ExportScope.Selection : ExportScope.Page, Selection = _selection, IncludePasteboard = key.Pasteboard });
            }
            catch (ExportException ex)
            {
                plan = null;
                _planErrors[key] = ex.Message;
            }
            _plans[key] = plan;
        }
        return plan;
    }

    private void OnAreaChanged()
    {
        _plan = CurrentPlan();
        ShowSizeFromBasis();
        UpdateState();
        SchedulePreview();
    }

    /// <summary>The number the user typed last stays; the other follows the proportions of the area.</summary>
    private void ShowSizeFromBasis()
    {
        if (_plan is null) return;
        var (w, h) = Proportional(_plan.Area, _widthBasis, _basisValue);
        _updating = true;
        WidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = h.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        _sizeValid = true;
    }

    /// <summary>Width and height for a typed width (or height) following the proportions of <paramref name="area"/>.</summary>
    internal static (long Width, long Height) Proportional(Rect2 area, bool fromWidth, long value)
    {
        double aw = Math.Max(area.Width, 1e-9), ah = Math.Max(area.Height, 1e-9);
        value = Math.Max(1, value);
        return fromWidth
            ? (value, Math.Max(1, (long)Math.Round(value * ah / aw)))
            : (Math.Max(1, (long)Math.Round(value * aw / ah)), value);
    }

    private void OnSizeTyped(bool width)
    {
        if (_updating || _plan is null) return;
        var box = width ? WidthBox : HeightBox;
        if (!long.TryParse(box.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
        {
            _sizeValid = false;
            UpdateState();
            return;
        }
        _sizeValid = true;
        _widthBasis = width;
        _basisValue = (int)Math.Min(n, int.MaxValue);
        var (w, h) = Proportional(_plan.Area, width, n);
        _updating = true;
        if (width) HeightBox.Text = h.ToString(CultureInfo.InvariantCulture);
        else WidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        UpdateState();
    }

    // ---------------------------------------------------------------- what is shown, what is valid

    private void UpdateState()
    {
        var info = Info;
        bool picture = info.IsPicture, legacy = IsLegacy, hasSelection = _selection.Count > 0;
        bool svg = Format == ExportFormat.Svg;

        FormatDescription.Text = info.Description;
        _areaSection.IsVisible = !legacy;
        SelectionRadio.IsVisible = hasSelection;
        _pasteboardRow.IsVisible = !SelectionChosen && !legacy;
        _sizeSection.IsVisible = !legacy;
        _pictureSizeRow.IsVisible = picture;
        _vectorSizeRow.IsVisible = !picture && !legacy;
        _jpegSection.IsVisible = Format == ExportFormat.Jpeg;
        _pngSection.IsVisible = Format == ExportFormat.Png;
        _textSection.IsVisible = svg;

        _problem = null;
        PreviewNote.Text = "";
        _plan = CurrentPlan();
        var key = AreaKey();
        if (_plan is null)
            _problem = _planErrors.GetValueOrDefault(key) ?? "There is nothing to export.";

        string summary = "";
        if (_plan is not null)
        {
            var area = _plan.Area;
            if (picture)
            {
                if (!_sizeValid) _problem = "Enter whole numbers of pixels, 1 or more.";
                else
                {
                    try
                    {
                        var (w, h) = DocumentExporter.PixelSize(area, SizeOptions());
                        summary = Format == ExportFormat.Png
                            ? $"{w} × {h} px · PNG · transparent"
                            : $"{w} × {h} px · JPEG · quality {(int)Math.Round(JpegSlider.Value)} · background {ColorChoiceDialog.Hex(_background)}";
                    }
                    catch (ExportException ex) { _problem = ex.Message; }
                }
                SizeNote.Text = $"Typing one number fills in the other. The proportions are those of the {(SelectionChosen ? "selection" : "page")} ({Inches(area.Width)} × {Inches(area.Height)} in).";
            }
            else if (legacy) summary = "Legacy layout · the whole design";
            else
            {
                string size = $"{Inches(area.Width)} × {Inches(area.Height)} in";
                VectorSize.Text = size;
                summary = $"{size} · {info.Name.Split(' ')[0]} · vector";
            }
            if (Format == ExportFormat.Eps && EpsExporter.RasterPieceCount(_plan) is > 0 and var n)
                PreviewNote.Text = $"{n} object{(n == 1 ? "" : "s")} with soft effects or transparency {(n == 1 ? "is" : "are")} written as {(n == 1 ? "a picture" : "pictures")}: EPS cannot draw them.";
            if (legacy)
                PreviewNote.Text = "Written from the documented layout format; not yet checked in the legacy program. After exporting you will be told what had to be simplified.";
        }
        SummaryText.Text = summary;
        ErrorText.Text = _problem ?? "";
        OkButton.IsEnabled = _problem is null;
    }

    private static string Inches(double points) => (points / Units.PointsPerInch).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The size part of the options: both numbers as shown (the other follows the typed one by the proportions, so they agree).</summary>
    private ExportOptions SizeOptions() =>
        new()
        {
            PixelWidth = int.TryParse(WidthBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : null,
            PixelHeight = int.TryParse(HeightBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : null,
        };

    /// <summary>The request for the current choices, or false (with the reason) when something is not valid.</summary>
    internal bool TryBuild(out ExportRequest? request, out string error)
    {
        request = null;
        error = _problem ?? "";
        if (_problem is not null || _plan is null) return false;
        var key = AreaKey();
        var options = new ExportOptions
        {
            Scope = key.Selection ? ExportScope.Selection : ExportScope.Page,
            Selection = _selection,
            IncludePasteboard = key.Pasteboard && !IsLegacy,
            JpegQuality = (int)Math.Round(JpegSlider.Value),
            Background = _background,
            PngCompression = (int)Math.Round(PngSlider.Value),
            TextAsOutlines = OutlinesRadio.IsChecked != false,
        };
        if (Info.IsPicture) options = options with { PixelWidth = SizeOptions().PixelWidth, PixelHeight = SizeOptions().PixelHeight };
        request = new ExportRequest(Format, options);
        return true;
    }

    /// <summary>The choices to start with next time.</summary>
    internal ExportSettings CurrentSettings() => new(
        Format, _widthBasis ? _basisValue : null, _widthBasis ? null : _basisValue, (int)Math.Round(JpegSlider.Value), (int)Math.Round(PngSlider.Value),
        PasteboardBox.IsChecked == true, OutlinesRadio.IsChecked != false, SelectionRadio.IsChecked == true && _selection.Count > 0, _background);

    // ---------------------------------------------------------------- background (JPEG)

    internal void SetBackground(DocColor color)
    {
        _background = new DocColor(color.R, color.G, color.B);
        _backgroundSwatch.Background = new SolidColorBrush(ColorUi.ToAvalonia(_background));
        _backgroundHex.Text = ColorChoiceDialog.Hex(_background);
        if (_building) return;
        UpdateState();
        SchedulePreview();
    }

    private async Task ChooseBackgroundAsync()
    {
        var picker = new ColorChoiceDialog("Background color", _background, JobColors.Collect(_doc));
        await picker.ShowDialog(this);
        if (picker.Result is { } chosen) SetBackground(chosen);
    }

    // ---------------------------------------------------------------- preview

    /// <summary>Starts drawing the preview of the current choices after a short pause; a newer request cancels the older one.</summary>
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
            double scale = RenderScaling > 0 ? RenderScaling : 1;
            int boxW = (int)Math.Floor(Math.Max(0, _previewPane.Bounds.Width - 2) * scale), boxH = (int)Math.Floor(Math.Max(0, _previewPane.Bounds.Height - 2) * scale);
            if (plan is null)
            {
                ShowPreview(null, 0, scale, plan);
                return;
            }
            if (boxW < 8 || boxH < 8) return;   // not laid out yet: the pane's size change asks again
            bool white = Format is ExportFormat.Jpeg or ExportFormat.Lyt;
            DocColor? solid = Format == ExportFormat.Jpeg ? _background : null;
            var key = (plan, white, solid, boxW, boxH);
            if (_drawnKey == key && _shownPreview is not null) return;   // the same picture again (typing a size, changing a quality): nothing to do
            var theme = AppTheme.DarkMenus ? CanvasTheme.Dark : CanvasTheme.Light;
            var options = new ExportPreviewOptions(boxW, boxH, theme.CheckerA, theme.CheckerB, white, Math.Max(4, (int)Math.Round(8 * scale)),
                solid is { } s ? new SKColor(s.R, s.G, s.B) : null);
            var doc = _doc;
            var started = System.Diagnostics.Stopwatch.StartNew();
            var bitmap = await Task.Run(() => ExportPreview.Render(doc, plan, options, cancel), cancel);
            if (cancel.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }
            ShowPreview(bitmap, started.Elapsed.TotalMilliseconds, scale, plan);
            _drawnKey = key;
        }
        catch (OperationCanceledException) { }
    }

    private void ShowPreview(SKBitmap? bitmap, double ms, double scale, ExportPlan? plan)
    {
        _shownPreview?.Dispose();
        _shownPreview = null;
        if (bitmap is null)
        {
            _previewImage.Source = null;
            _previewEmpty.Text = "Nothing to preview";
            _previewEmpty.IsVisible = true;
            return;
        }
        using (bitmap)
        {
            LastPreviewCorner = bitmap.GetPixel(0, 0);
            var wb = new WriteableBitmap(new PixelSize(bitmap.Width, bitmap.Height), new Vector(96 * scale, 96 * scale), PixelFormat.Rgba8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                var src = bitmap.GetPixelSpan();
                int rowBytes = bitmap.Width * 4;
                for (int y = 0; y < bitmap.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(src.Slice(y * bitmap.RowBytes, rowBytes).ToArray(), 0, fb.Address + y * fb.RowBytes, rowBytes);
            }
            _shownPreview = wb;
            _previewImage.Source = wb;
            LastPreview = (bitmap.Width, bitmap.Height, plan!.Area);
        }
        _previewEmpty.IsVisible = false;
        LastPreviewMs = ms;
        PreviewsDrawn++;
    }
}
