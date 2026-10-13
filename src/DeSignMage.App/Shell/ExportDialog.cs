using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Model;
using DeSignMage.Rendering.Export;

namespace DeSignMage.App.Shell;

/// <summary>What the user asked for in the Export dialog.</summary>
internal sealed record ExportRequest(ExportFormat Format, ExportOptions Options)
{
    public string Extension => Format switch { ExportFormat.Svg => ".svg", ExportFormat.Pdf => ".pdf", _ => ".png" };
}

/// <summary>The choices the Export dialog starts with (the last ones used in this session).</summary>
internal sealed record ExportSettings(
    ExportFormat Format = ExportFormat.Png, double Dpi = 300, bool Transparent = false, bool IncludePasteboard = false,
    bool TextAsOutlines = true, bool SelectionOnly = false);

/// <summary>File ▸ Export...: format, area, resolution and the other options. The file name is asked for afterwards.</summary>
internal sealed class ExportDialog : Window
{
    private readonly Document _doc;
    private readonly IReadOnlyCollection<DocObject> _selection;

    private readonly RadioButton _svg = new() { Content = "SVG (vector, for the web, Illustrator, Inkscape)", GroupName = "fmt" };
    private readonly RadioButton _pdf = new() { Content = "PDF (vector, for printing and sharing)", GroupName = "fmt" };
    private readonly RadioButton _png = new() { Content = "PNG (picture)", GroupName = "fmt" };
    private readonly RadioButton _page = new() { Content = "Whole page", GroupName = "area" };
    private readonly RadioButton _selected = new() { GroupName = "area" };
    private readonly CheckBox _pasteboard = new() { Content = "Include objects outside the page (pasteboard)" };
    private readonly TextBox _dpi = new TextBox().ScaledWidth(70);
    private readonly TextBlock _size = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") };
    private readonly CheckBox _transparent = new() { Content = "Transparent background" };
    private readonly RadioButton _outlines = new() { Content = "Letters as outlines (looks the same everywhere)", GroupName = "text" };
    private readonly RadioButton _liveText = new() { Content = "Real text (editable; needs the font on the other computer)", GroupName = "text" };
    private readonly TextBlock _error = new() { Foreground = AppTheme.Brush("Ui.Error"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _ok = new Button { Content = "Export...", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);
    private readonly StackPanel _pngPanel = new() { Spacing = UiScale.Sp(6) };
    private readonly StackPanel _svgPanel = new() { Spacing = UiScale.Sp(4) };
    private readonly TextBlock _pdfNote = new() { Text = "Letters are written as curves, so the PDF looks the same on every computer.", TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Brush("Ui.Muted") };

    public ExportRequest? Result { get; private set; }

    public ExportDialog(Document doc, IReadOnlyCollection<DocObject> selection, ExportSettings settings)
    {
        _doc = doc;
        _selection = selection;
        Title = "Export";
        DialogScale.Attach(this, 480);
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _selected.Content = selection.Count > 0 ? $"Selected objects only ({selection.Count})" : "Selected objects only (nothing is selected)";
        _selected.IsEnabled = selection.Count > 0;

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(4) };
        foreach (int p in new[] { 72, 150, 300, 600 })
        {
            var b = new Button { Content = p.ToString(CultureInfo.InvariantCulture), Padding = UiScale.Space(6, 2), Focusable = false };
            b.Click += (_, _) => { _dpi.Text = p.ToString(CultureInfo.InvariantCulture); UpdateState(); };
            presets.Children.Add(b);
        }
        _pngPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8),
            Children = { new TextBlock { Text = "Resolution:", VerticalAlignment = VerticalAlignment.Center }, _dpi, new TextBlock { Text = "dpi", VerticalAlignment = VerticalAlignment.Center }, presets },
        });
        _pngPanel.Children.Add(_size);
        _pngPanel.Children.Add(_transparent);
        _svgPanel.Children.Add(new TextBlock { Text = "Live text:", FontWeight = FontWeight.SemiBold });
        _svgPanel.Children.Add(_outlines);
        _svgPanel.Children.Add(_liveText);

        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(90);
        cancel.Click += (_, _) => Close();
        _ok.Click += (_, _) =>
        {
            if (!TryBuild(out var request, out _)) return;
            Result = request;
            Close();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = UiScale.Sp(8), Margin = UiScale.Space(0, 12, 0, 0), Children = { _ok, cancel } };
        var root = new StackPanel { Margin = UiScale.Space(16), Spacing = UiScale.Sp(6) };
        root.Children.Add(Heading("Format"));
        root.Children.Add(_svg); root.Children.Add(_pdf); root.Children.Add(_png);
        root.Children.Add(Heading("What to export"));
        root.Children.Add(_page); root.Children.Add(_selected); root.Children.Add(_pasteboard);
        root.Children.Add(_pngPanel); root.Children.Add(_svgPanel); root.Children.Add(_pdfNote);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = DialogScale.Scrollable(root);

        (settings.Format switch { ExportFormat.Svg => _svg, ExportFormat.Pdf => _pdf, _ => _png }).IsChecked = true;
        (settings.SelectionOnly && selection.Count > 0 ? _selected : _page).IsChecked = true;
        _pasteboard.IsChecked = settings.IncludePasteboard;
        _dpi.Text = settings.Dpi.ToString("0.###", CultureInfo.InvariantCulture);
        _transparent.IsChecked = settings.Transparent;
        (settings.TextAsOutlines ? _outlines : _liveText).IsChecked = true;

        foreach (var r in new[] { _svg, _pdf, _png, _page, _selected, _outlines, _liveText }) r.IsCheckedChanged += (_, _) => UpdateState();
        _pasteboard.IsCheckedChanged += (_, _) => UpdateState();
        _dpi.TextChanged += (_, _) => UpdateState();
        UpdateState();
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = UiScale.Space(0, 6, 0, 0) };

    private ExportFormat Format => _svg.IsChecked == true ? ExportFormat.Svg : _pdf.IsChecked == true ? ExportFormat.Pdf : ExportFormat.Png;

    private void UpdateState()
    {
        _pngPanel.IsVisible = Format == ExportFormat.Png;
        _svgPanel.IsVisible = Format == ExportFormat.Svg;
        _pdfNote.IsVisible = Format == ExportFormat.Pdf;
        _pasteboard.IsEnabled = _page.IsChecked == true;

        _error.Text = "";
        _size.Text = "";
        bool ok = TryBuild(out var request, out var error);
        if (!ok) _error.Text = error;
        else if (Format == ExportFormat.Png)
        {
            var (w, h) = DocumentExporter.PngSize(_doc, request!.Options);
            _size.Text = $"Picture size: {w} x {h} pixels";
        }
        _ok.IsEnabled = ok;
    }

    private bool TryBuild(out ExportRequest? request, out string error)
    {
        request = null;
        error = "";
        double dpi = 300;
        if (Format == ExportFormat.Png)
        {
            var text = _dpi.Text?.Trim() ?? "";
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out dpi) && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out dpi))
            {
                error = "Enter the resolution as a number of dots per inch, for example 300.";
                return false;
            }
        }
        var options = new ExportOptions
        {
            Scope = _selected.IsChecked == true ? ExportScope.Selection : ExportScope.Page,
            Selection = _selection,
            IncludePasteboard = _pasteboard.IsChecked == true && _page.IsChecked == true,
            Dpi = dpi,
            TransparentBackground = _transparent.IsChecked == true,
            TextAsOutlines = _outlines.IsChecked != false,
        };
        try
        {
            // Plans the export (also validates the size) without drawing anything.
            if (Format == ExportFormat.Png) DocumentExporter.PngSize(_doc, options);
            else ExportPlan.Create(_doc, options);
        }
        catch (ExportException ex)
        {
            error = ex.Message;
            return false;
        }
        request = new ExportRequest(Format, options);
        return true;
    }

    /// <summary>The settings to start with next time.</summary>
    public static ExportSettings Remember(ExportRequest r) => new(
        r.Format, r.Options.Dpi, r.Options.TransparentBackground, r.Options.IncludePasteboard, r.Options.TextAsOutlines, r.Options.Scope == ExportScope.Selection);
}
