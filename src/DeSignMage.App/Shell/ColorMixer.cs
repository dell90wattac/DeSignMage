using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Model;

namespace DeSignMage.App.Shell;

/// <summary>One slider + number box of the Color Mixer. A slider drag is one undo step (<see cref="GestureStarted"/>/<see cref="GestureEnded"/>).</summary>
internal sealed class ChannelRow
{
    private readonly TextBlock _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center }.ScaledWidth(14);
    private bool _setting, _typed;

    public ChannelRow()
    {
        Slider = new Slider { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center }.ScaledMinHeight(20).ScaledHeight(22);
        Slider.Classes.Add("ch");
        Box = new TextBox { Padding = new Thickness(4, 1), VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.ScaledWidth(46).ScaledMinHeight(22);
        Row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_label, Dock.Left);
        DockPanel.SetDock(Box, Dock.Right);
        Row.Children.Add(_label);
        Row.Children.Add(Box);
        // The slider track is transparent (see ShellStyles.axaml); this strip behind it shows the channel's range for the current color.
        Strip = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, BorderBrush = AppTheme.Brush("Ui.SwatchBorder"), BorderThickness = new Thickness(1), IsHitTestVisible = false }.ScaledHeight(8);
        Row.Children.Add(new Grid { Children = { Strip, Slider } });

        Slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty || _setting) return;
            ShowText(Slider.Value);
            Edited?.Invoke(this, EventArgs.Empty);
        };
        // Mouse down on the slider starts a continuous edit; the release (or losing the mouse) ends it.
        Slider.AddHandler(InputElement.PointerPressedEvent, (_, _) => GestureStarted?.Invoke(this, EventArgs.Empty), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => GestureEnded?.Invoke(this, EventArgs.Empty), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        Slider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => GestureEnded?.Invoke(this, EventArgs.Empty), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        Box.TextChanged += (_, _) => { if (!_setting) _typed = true; };
        Box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitText();
            e.Handled = true;
        };
        Box.LostFocus += (_, _) => CommitText();
    }

    public Slider Slider { get; }
    public Border Strip { get; }
    public TextBox Box { get; }
    public DockPanel Row { get; }
    public int Decimals { get; set; }

    public event EventHandler? Edited, GestureStarted, GestureEnded;

    public double Value => Slider.Value;

    public void Configure(string label, double max, int decimals)
    {
        _label.Text = label;
        Decimals = decimals;
        _setting = true;
        Slider.Maximum = max;
        Slider.TickFrequency = 1;
        _setting = false;
    }

    /// <summary>Paints the range strip behind the slider with the colors the channel runs through (low to high).</summary>
    public void SetRange(params Color[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
        for (int i = 0; i < stops.Length; i++) brush.GradientStops.Add(new GradientStop(stops[i], i / (double)(stops.Length - 1)));
        Strip.Background = brush;
    }

    /// <summary>Shows a value without raising <see cref="Edited"/>.</summary>
    public void Set(double value)
    {
        _setting = true;
        Slider.Value = value;
        ShowText(value);
        _typed = false;
        _setting = false;
    }

    private void ShowText(double v)
    {
        bool was = _setting;
        _setting = true;
        Box.Text = Math.Round(v, Decimals).ToString("0.#", CultureInfo.InvariantCulture);
        _setting = was;
    }

    private void CommitText()
    {
        if (_setting || !_typed) return;
        _typed = false;
        if (!double.TryParse(Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            !double.TryParse(Box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
        {
            ShowText(Slider.Value);
            return;
        }
        v = Math.Clamp(Math.Round(v, Decimals), Slider.Minimum, Slider.Maximum);
        _setting = true;
        Slider.Value = v;
        ShowText(v);
        _setting = false;
        Edited?.Invoke(this, EventArgs.Empty);
        ContextBars.FocusCanvas?.Invoke();
    }
}

/// <summary>
/// The Color Mixer (the right dock's Color Mixer tab): color model, palette and named-color combos, per-channel sliders with number boxes,
/// the current-color swatch, an HSV ring + square, join/cap buttons, the Fill/Stroke target, and an eyedropper. Edits apply live to the selection (a slider or wheel drag is ONE undo step); with nothing selected they set the
/// default style of new objects. Colors picked from a palette keep their name, vendor and model; a hand-edited color keeps
/// only its model.
/// </summary>
public sealed class ColorMixer : IRefreshable
{
    private readonly EditorSession _session;
    private readonly ColorUiState _state;

    private readonly ComboBox _mode = new ComboBox { Name = "ColorModelCombo", Padding = new Thickness(6, 1) }.ScaledWidth(78).ScaledMinHeight(22);
    private readonly ComboBox _palette = new() { Name = "PaletteCombo", MinHeight = 22, Padding = new Thickness(6, 1), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoCompleteBox _name = new() { Name = "ColorNameBox", MinHeight = 22, MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.Contains, PlaceholderText = "Color name\u2026" };
    private readonly ChannelRow[] _rows = [new(), new(), new(), new()];
    private readonly Border _big = new Border { Name = "CurrentColorSwatch", VerticalAlignment = VerticalAlignment.Top, BorderBrush = AppTheme.Brush("Ui.SwatchBorder"), BorderThickness = new Thickness(1) }.ScaledWidth(40, icon: true).ScaledHeight(40, icon: true);
    private readonly ColorWheel _wheel = new();
    private readonly RadioButton _fill = new() { Name = "FillRadio", Content = "Fill", GroupName = "colorTarget", Focusable = false };
    private readonly RadioButton _stroke = new() { Name = "StrokeRadio", Content = "Stroke", GroupName = "colorTarget", Focusable = false };
    private readonly Button _eyedropper, _noFill, _noStroke;
    private readonly Button[] _joinButtons, _capButtons;
    private readonly CheckBox _adjust = new() { Name = "AdjustmentControls", Content = "Adjustment Controls", IsChecked = true, Focusable = false };
    private readonly ListBox _list = new() { Name = "PaletteList", MinHeight = 120, MaxHeight = 400 };
    private readonly Control _controls;

    private bool _refreshing;
    private DocColor _current = DocColor.Black;
    private double _h, _s, _v;   // the wheel's HSV; kept so hue survives passing through gray
    private List<string> _names = [];
    private List<DocColor> _paletteColors = [];

    public ColorMixer(Panel host, EditorSession session, ColorUiState state)
    {
        _session = session;
        _state = state;

        _mode.ItemsSource = new[] { "RGB", "CMYK", "HSV" };
        _palette.ItemsSource = state.Palettes.Select(p => p.Name).ToList();

        var combos = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_mode, Dock.Left);
        _mode.Margin = new Thickness(0, 0, 4, 0);
        combos.Children.Add(_mode);
        combos.Children.Add(PaletteMenuButton());
        combos.Children.Add(_palette);

        // Sliders on the left, the big current-color swatch on the right.
        var sliders = new StackPanel();
        foreach (var r in _rows) sliders.Children.Add(r.Row);
        var swatchColumn = new StackPanel { Margin = new Thickness(6, 0, 0, 0), Children = { _big } };
        var sliderArea = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(swatchColumn, Dock.Right);
        sliderArea.Children.Add(swatchColumn);
        sliderArea.Children.Add(sliders);

        // Join / cap buttons, beside the wheel.
        LineJoin[] joins = [LineJoin.Miter, LineJoin.Round, LineJoin.Bevel];
        LineCap[] caps = [LineCap.Butt, LineCap.Round, LineCap.Square];
        string[] joinTips = ["Mitered corners", "Rounded corners", "Beveled corners"];
        string[] capTips = ["Flat line ends", "Rounded line ends", "Square line ends"];
        _joinButtons = joins.Select((j, i) => ColorUi.IconButton(StrokeIcons.Join(j), joinTips[i], () => _session.ApplyStrokeJoin(j))).ToArray();
        _capButtons = caps.Select((c, i) => ColorUi.IconButton(StrokeIcons.Cap(c), capTips[i], () => _session.ApplyStrokeCap(c))).ToArray();
        _eyedropper = ColorUi.IconButton(EyedropperIcon(), "Eyedropper: click an object to take its color", () => _session.BeginEyedropper());
        _eyedropper.Name = "EyedropperButton";
        _noFill = ColorUi.TextButton("No Fill", "No Fill: removes the fill from the selected objects, leaving just the outline", () => _session.ApplyNoFill());
        _noFill.Name = "NoFillButton";
        _noStroke = ColorUi.TextButton("No Stroke", "No Stroke: removes the outline stroke from the selected objects", () => _session.ApplyNoStroke());
        _noStroke.Name = "NoStrokeButton";

        var styleColumn = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0), Spacing = 4,
            Children =
            {
                ContextBars.Caption("Corners"),
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _joinButtons[0], _joinButtons[1], _joinButtons[2] } },
                ContextBars.Caption("Line ends"),
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _capButtons[0], _capButtons[1], _capButtons[2] } },
                            },
        };
        var wheelRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _wheel, styleColumn } };

        var targetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _fill, _stroke, _eyedropper },
        };

        var noPaintRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _noFill, _noStroke } };
        _controls = new StackPanel { Spacing = 8, Children = { sliderArea, wheelRow } };
        _list.ItemTemplate = new FuncDataTemplate<DocColor>((c, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(ColorUi.Swatch(new SolidPaint(c), 14));
            row.Children.Add(new TextBlock { Text = c.DisplayName ?? c.ToHex(), VerticalAlignment = VerticalAlignment.Center });
            return row;
        });
        _list.IsVisible = false;

        host.Children.Add(combos);
        host.Children.Add(_name);
        host.Children.Add(_controls);
        host.Children.Add(_list);
        host.Children.Add(targetRow);
        host.Children.Add(noPaintRow);   // always shown, also with Adjustment Controls off
        host.Children.Add(_adjust);

        Wire();
        FillPaletteLists();
        ConfigureChannels();
        Refresh();
    }

    private static Control EyedropperIcon()
    {
        // A pipette: tilted tube with a bulb and a tip, drawn as three small shapes.
        var tube = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M5,15 L12,8 L14,10 L7,17 Z"), Fill = Brushes.SteelBlue, Stroke = AppTheme.Brush("Ui.Icon"), StrokeThickness = 1 };
        var bulb = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M11,7 L15,3 L17,5 L13,9 Z"), Fill = AppTheme.Brush("Ui.Muted") };
        var tip = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M5,15 L3,18 L6,16 Z"), Fill = AppTheme.Brush("Ui.Icon") };
        return UiIcons.Box(new Avalonia.Controls.Canvas { Width = 20, Height = 20, Children = { tube, bulb, tip } });
    }

    // ---- Wiring ----

    private void Wire()
    {
        _mode.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _mode.SelectedIndex < 0) return;
            _state.Mode = (ColorEditMode)_mode.SelectedIndex;
        };
        _state.ModeChanged += (_, _) => { ConfigureChannels(); Refresh(); };

        _palette.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _palette.SelectedIndex < 0) return;
            _state.ActivePalette = _state.Palettes[_palette.SelectedIndex];
        };
        _state.PaletteChanged += (_, _) => { FillPaletteLists(); Refresh(); };

        _state.TargetChanged += (_, _) => Refresh();
        _fill.IsCheckedChanged += (_, _) => { if (!_refreshing && _fill.IsChecked == true) _state.Target = ColorTarget.Fill; };
        _stroke.IsCheckedChanged += (_, _) => { if (!_refreshing && _stroke.IsChecked == true) _state.Target = ColorTarget.Stroke; };

        foreach (var row in _rows)
        {
            row.Edited += (_, _) => OnChannelsEdited();
            row.GestureStarted += (_, _) => _session.BeginStyleGesture();
            row.GestureEnded += (_, _) => { _session.EndStyleGesture(); ContextBars.FocusCanvas?.Invoke(); };
        }

        _wheel.GestureStarted += (_, _) => _session.BeginStyleGesture();
        _wheel.GestureEnded += (_, _) => { _session.EndStyleGesture(); ContextBars.FocusCanvas?.Invoke(); };
        _wheel.Changed += (_, _) =>
        {
            _h = _wheel.Hue; _s = _wheel.Saturation; _v = _wheel.Value;
            var (r, g, b) = ColorMath.HsvToRgb(_h, _s, _v);
            Apply(_state.Mode == ColorEditMode.Cmyk ? FromRgbAsCmyk(r, g, b) : new DocColor(r, g, b));
        };

        _name.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _name.SelectedItem is not string text) return;
            int i = _names.IndexOf(text);
            if (i >= 0) Apply(_paletteColors[i]);
        };

        _list.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _list.SelectedItem is not DocColor c) return;
            Apply(c);
            _refreshing = true;
            _list.SelectedItem = null;   // so the same color can be picked again
            _refreshing = false;
        };

        _adjust.IsCheckedChanged += (_, _) =>
        {
            bool on = _adjust.IsChecked == true;
            _controls.IsVisible = on;
            _list.IsVisible = !on;
        };

        _session.ColorPicked += (_, e) => OnPicked(e.Style);
    }

    /// <summary>The "…" button beside the palette list: load palettes from any folder or file, or forget the ones added.</summary>
    private Button PaletteMenuButton()
    {
        var folder = new MenuItem { Header = "Load palettes from a folder…" };
        folder.Click += (_, _) => _state.LoadFolderRequested?.Invoke();
        var files = new MenuItem { Header = "Load palette files…" };
        files.Click += (_, _) => _state.LoadFilesRequested?.Invoke();
        var forget = new MenuItem { Header = "Forget added palette folders" };
        forget.Click += (_, _) => _state.ForgetAddedPalettes();
        var button = new Button
        {
            Name = "PaletteMenuButton",
            Content = "…",
            Focusable = false,
            Padding = new Thickness(6, 0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Flyout = new MenuFlyout { Items = { folder, files, forget } },
        };
        ToolTip.SetTip(button, "Load color palettes (.PAL / .CLR) from a folder or file");
        DockPanel.SetDock(button, Dock.Right);
        return button;
    }

    private void FillPaletteLists()
    {
        var pal = _state.ActivePalette;
        _paletteColors = pal.Colors.ToList();
        _names = _paletteColors.Select(c => c.DisplayName ?? c.ToHex()).ToList();
        _refreshing = true;
        if (_palette.ItemCount != _state.Palettes.Count || !_state.Palettes.Select(p => p.Name).SequenceEqual(_palette.Items.OfType<string>()))
            _palette.ItemsSource = _state.Palettes.Select(p => p.Name).ToList();   // palettes were added or forgotten
        _palette.SelectedIndex = _state.Palettes.ToList().IndexOf(pal);
        _name.ItemsSource = _names;
        _list.ItemsSource = _paletteColors;
        _refreshing = false;
    }

    /// <summary>Labels, ranges and decimals of the sliders for the current model.</summary>
    private void ConfigureChannels()
    {
        _refreshing = true;
        switch (_state.Mode)
        {
            case ColorEditMode.Rgb:
                Configure(["R", "G", "B"], [255, 255, 255], [0, 0, 0]);
                break;
            case ColorEditMode.Cmyk:
                Configure(["C", "M", "Y", "K"], [100, 100, 100, 100], [1, 1, 1, 1]);
                break;
            default:
                Configure(["H", "S", "V"], [360, 100, 100], [0, 0, 0]);
                break;
        }
        _mode.SelectedIndex = (int)_state.Mode;
        _refreshing = false;

        void Configure(string[] labels, double[] max, int[] decimals)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i].Row.IsVisible = i < labels.Length;
                if (i < labels.Length) _rows[i].Configure(labels[i], max[i], decimals[i]);
            }
        }
    }

    // ---- Reading and writing the color ----

    /// <summary>A color from RGB bytes that stays CMYK-model (CMYK mode edits keep the model).</summary>
    private static DocColor FromRgbAsCmyk(byte r, byte g, byte b)
    {
        var k = ColorMath.RgbToCmyk(r, g, b);
        return DocColor.FromCmyk(Math.Round(k.C, 1), Math.Round(k.M, 1), Math.Round(k.Y, 1), Math.Round(k.K, 1));
    }

    private DocColor ColorFromChannels() => _state.Mode switch
    {
        ColorEditMode.Rgb => new DocColor((byte)Math.Round(_rows[0].Value), (byte)Math.Round(_rows[1].Value), (byte)Math.Round(_rows[2].Value)),
        ColorEditMode.Cmyk => DocColor.FromCmyk(_rows[0].Value, _rows[1].Value, _rows[2].Value, _rows[3].Value),
        _ => HsvColor(_rows[0].Value, _rows[1].Value, _rows[2].Value),
    };

    private DocColor HsvColor(double h, double s, double v)
    {
        _h = h; _s = s; _v = v;
        var (r, g, b) = ColorMath.HsvToRgb(h, s, v);
        return new DocColor(r, g, b);
    }

    private void OnChannelsEdited()
    {
        if (_refreshing) return;
        var color = ColorFromChannels();
        if (_state.Mode != ColorEditMode.Hsv)
        {
            (_h, _s, _v) = color.ToHsv();
            if (_s == 0) _h = _wheel.Hue;   // gray has no hue: keep the ring where it was
        }
        Apply(color);
    }

    private void Apply(DocColor color)
    {
        var paint = new SolidPaint(color);
        if (_state.Target == ColorTarget.Fill) _session.ApplyFill(paint);
        else _session.ApplyStrokePaint(paint);
    }

    private void OnPicked(ObjectStyle picked)
    {
        // The target's own paint if the object has one, else the other (a stroked-only object still gives a color to a fill).
        var paint = ColorUi.PaintOf(picked, _state.Target) ?? ColorUi.PaintOf(picked, _state.Target == ColorTarget.Fill ? ColorTarget.Stroke : ColorTarget.Fill);
        if (paint is SolidPaint) Apply(((SolidPaint)paint).Color);
    }

    // ---- Refresh ----

    public void Refresh()
    {
        var style = _session.CurrentStyle;
        var paint = ColorUi.PaintOf(style, _state.Target);
        _refreshing = true;

        _fill.IsChecked = _state.Target == ColorTarget.Fill;
        _stroke.IsChecked = _state.Target == ColorTarget.Stroke;

        if (paint is SolidPaint sp)
        {
            _current = sp.Color;
            // Keep the wheel's hue while the color still is what the wheel made (hue is lost at gray and black).
            var (cr, cg, cb) = ColorMath.HsvToRgb(_h, _s, _v);
            if ((cr, cg, cb) != (_current.R, _current.G, _current.B)) (_h, _s, _v) = _current.ToHsv();
        }

        ShowChannels();
        UpdateRanges();
        _wheel.Set(_h, _s, _v);
        _big.Child = null;
        _big.Background = paint is SolidPaint ? new SolidColorBrush(ColorUi.ToAvalonia(_current)) : Brushes.White;
        if (paint is null) _big.Child = ColorUi.Cross(34);
        ToolTip.SetTip(_big, paint is SolidPaint ? ColorUi.Describe(_current) : "No color");

        _name.Text = paint is SolidPaint ? (_current.DisplayName ?? "") : "";
        _name.PlaceholderText = paint is null ? "No color" : "Color name\u2026";
        _controls.Opacity = paint is null ? 0.45 : 1;   // sliders and wheel are dimmed while the target has no color; using one gives it a color

        var stroke = style.Stroke;
        for (int i = 0; i < _joinButtons.Length; i++) ColorUi.SetOn(_joinButtons[i], stroke?.Join == (LineJoin)i);
        for (int i = 0; i < _capButtons.Length; i++) ColorUi.SetOn(_capButtons[i], stroke?.Cap == (LineCap)i);
        ColorUi.SetOn(_eyedropper, _session.ActiveTool is EyedropperTool);

        _refreshing = false;
    }

    /// <summary>Gradients behind the sliders: each channel from its lowest to its highest value with the others as they are.</summary>
    private void UpdateRanges()
    {
        static Color Rgb((byte R, byte G, byte B) c) => Color.FromRgb(c.R, c.G, c.B);
        switch (_state.Mode)
        {
            case ColorEditMode.Rgb:
                _rows[0].SetRange(Color.FromRgb(0, _current.G, _current.B), Color.FromRgb(255, _current.G, _current.B));
                _rows[1].SetRange(Color.FromRgb(_current.R, 0, _current.B), Color.FromRgb(_current.R, 255, _current.B));
                _rows[2].SetRange(Color.FromRgb(_current.R, _current.G, 0), Color.FromRgb(_current.R, _current.G, 255));
                break;
            case ColorEditMode.Cmyk:
            {
                var k = _current.ToCmyk();
                Color At(double c, double m, double y, double kk) => Rgb(ColorMath.CmykToRgb(c, m, y, kk));
                _rows[0].SetRange(At(0, k.M, k.Y, k.K), At(100, k.M, k.Y, k.K));
                _rows[1].SetRange(At(k.C, 0, k.Y, k.K), At(k.C, 100, k.Y, k.K));
                _rows[2].SetRange(At(k.C, k.M, 0, k.K), At(k.C, k.M, 100, k.K));
                _rows[3].SetRange(At(k.C, k.M, k.Y, 0), At(k.C, k.M, k.Y, 100));
                break;
            }
            default:
                _rows[0].SetRange(Enumerable.Range(0, 7).Select(i => Rgb(ColorMath.HsvToRgb(i * 60, 100, 100))).ToArray());
                _rows[1].SetRange(Rgb(ColorMath.HsvToRgb(_h, 0, _v)), Rgb(ColorMath.HsvToRgb(_h, 100, _v)));
                _rows[2].SetRange(Rgb(ColorMath.HsvToRgb(_h, _s, 0)), Rgb(ColorMath.HsvToRgb(_h, _s, 100)));
                break;
        }
    }

    private void ShowChannels()
    {
        switch (_state.Mode)
        {
            case ColorEditMode.Rgb:
                _rows[0].Set(_current.R); _rows[1].Set(_current.G); _rows[2].Set(_current.B);
                break;
            case ColorEditMode.Cmyk:
                var k = _current.ToCmyk();
                _rows[0].Set(k.C); _rows[1].Set(k.M); _rows[2].Set(k.Y); _rows[3].Set(k.K);
                break;
            default:
                _rows[0].Set(_h); _rows[1].Set(_s); _rows[2].Set(_v);
                break;
        }
    }

    // ---- For tests ----

    internal ChannelRow[] Rows => _rows;
    internal ColorWheel Wheel => _wheel;
    internal ComboBox ModeCombo => _mode;
    internal ComboBox PaletteCombo => _palette;
    internal AutoCompleteBox NameBox => _name;
    internal RadioButton FillRadio => _fill;
    internal RadioButton StrokeRadio => _stroke;
    internal Button Eyedropper => _eyedropper;
    internal Button NoFill => _noFill;
    internal Button NoStroke => _noStroke;
    internal Button[] JoinButtons => _joinButtons;
    internal Button[] CapButtons => _capButtons;
    internal CheckBox Adjustment => _adjust;
    internal ListBox List => _list;
    internal Border BigSwatch => _big;
}
