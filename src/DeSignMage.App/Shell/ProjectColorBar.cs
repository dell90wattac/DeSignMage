using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>
/// The Project Color Bar, at the left of the bottom bar: the colors used in the design (the Job color palette: read-only, click = fill,
/// right click = stroke, like the Palette Strip; View ▸ Show Job Color Palette), then the Fill and Stroke swatches of the selection (the
/// crossed box means none; clicking one makes it the target of the Color Mixer), each with its No Fill / No Stroke button. With nothing
/// selected the swatches show what new shapes get (nothing, until a color is chosen). Corners and line ends are set in the Color Mixer.
/// </summary>
public sealed class ProjectColorBar : IRefreshable
{
    private readonly EditorSession _session;
    private readonly ColorUiState _state;
    private readonly Button _fillButton, _strokeButton, _noFill, _noStroke;
    private readonly StackPanel _jobCells = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _jobScroll;
    private readonly Border _jobBox;
    private TextBlock _jobCaption = null!;
    private IReadOnlyList<DocColor> _jobShown = [];

    public ProjectColorBar(Panel host, EditorSession session, ColorUiState state)
    {
        _session = session;
        _state = state;

        _jobScroll = new ScrollViewer
        {
            Name = "JobPaletteScroll", Content = _jobCells,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        }.ScaledWidth(170, icon: true).ScaledHeight(24, icon: true);
        _jobScroll.PointerWheelChanged += (_, e) =>   // the wheel scrolls the row sideways
        {
            _jobScroll.Offset = new Vector(Math.Max(0, _jobScroll.Offset.X - e.Delta.Y * 36), 0);
            e.Handled = true;
        };
        _jobBox = new Border { Name = "JobPalette", Child = _jobScroll, BorderBrush = AppTheme.Brush("Ui.ActBorder"), BorderThickness = new Thickness(1), Padding = new Thickness(1), IsVisible = state.ShowJobPalette };
        ToolTip.SetTip(_jobBox, "Job colors: every color used in this document (read-only). Left click: fill. Right click: stroke.");
        state.JobPaletteVisibilityChanged += (_, _) =>
        {
            _jobBox.IsVisible = _jobCaption.IsVisible = state.ShowJobPalette;
            if (state.ShowJobPalette) RefreshJobColors();
        };

        _fillButton = SwatchButton("FillSwatchButton", "Fill color (click to edit it in the Color Mixer)", ColorTarget.Fill);
        _strokeButton = SwatchButton("StrokeSwatchButton", "Stroke color (click to edit it in the Color Mixer)", ColorTarget.Stroke);
        state.TargetChanged += (_, _) => Refresh();

        _noFill = ColorUi.TextButton("No Fill", "No Fill: removes the fill from the selected objects, leaving just the outline", () => _session.ApplyNoFill());
        _noFill.Name = "BarNoFillButton";
        _noStroke = ColorUi.TextButton("No Stroke", "No Stroke: removes the outline stroke from the selected objects", () => _session.ApplyNoStroke());
        _noStroke.Name = "BarNoStrokeButton";

        _jobCaption = ContextBars.Caption("Project colors");
        _jobCaption.IsVisible = state.ShowJobPalette;
        host.Children.Add(ContextBars.Group(_jobCaption, _jobBox));
        host.Children.Add(ContextBars.Group(ContextBars.Caption("Fill"), _fillButton, _noFill));
        host.Children.Add(ContextBars.Group(ContextBars.Caption("Stroke"), _strokeButton, _noStroke));
        Refresh();
    }

    private Button SwatchButton(string name, string tip, ColorTarget target)
    {
        var b = new Button { Name = name, Focusable = false };
        b.Classes.Add("tb");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => _state.Target = target;
        return b;
    }

    /// <summary>Rebuilds the job-color cells when the set of colors in use has changed (the list is cached per document revision).</summary>
    private void RefreshJobColors()
    {
        var colors = _session.JobPalette.Colors;
        if (ReferenceEquals(colors, _jobShown) || colors.SequenceEqual(_jobShown)) return;
        _jobShown = colors;
        _jobCells.Children.Clear();
        foreach (var c in colors) _jobCells.Children.Add(JobCell(c));
    }

    private Control JobCell(DocColor color)
    {
        var paint = new SolidPaint(color);
        var swatch = ColorUi.Swatch(paint, 18);
        swatch.Margin = new Thickness(1, 2);
        ToolTip.SetTip(swatch, ColorUi.Describe(color) + "\nJob color. Left click: fill   Right click: stroke");
        swatch.PointerPressed += (_, e) =>
        {
            var kind = e.GetCurrentPoint(swatch).Properties.PointerUpdateKind;
            if (kind == Avalonia.Input.PointerUpdateKind.LeftButtonPressed) _session.ApplyFill(paint);
            else if (kind == Avalonia.Input.PointerUpdateKind.RightButtonPressed) _session.ApplyStrokePaint(paint);
            e.Handled = true;
        };
        return swatch;
    }

    public void Refresh()
    {
        var style = _session.CurrentStyle;
        _fillButton.Content = ColorUi.Swatch(style.Fill, 18);
        _strokeButton.Content = ColorUi.Swatch(style.Stroke?.Paint, 18);
        ColorUi.SetOn(_fillButton, _state.Target == ColorTarget.Fill);
        ColorUi.SetOn(_strokeButton, _state.Target == ColorTarget.Stroke);
        if (_state.ShowJobPalette) RefreshJobColors();
    }

    // ---- For tests ----

    internal Button FillButton => _fillButton;
    internal Button StrokeButton => _strokeButton;
    internal Button NoFill => _noFill;
    internal Button NoStroke => _noStroke;
    internal StackPanel JobCells => _jobCells;
    internal Border JobPalette => _jobBox;
}
