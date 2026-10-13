using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeSignMage.Vectorize;
using DeSignMage.Vectorize.Imaging;
using SkiaSharp;

namespace DeSignMage.App.Shell;

/// <summary>
/// Image â–¸ Vectorize options with a live preview. The preview traces a shrunken copy on a background thread
/// (restarted whenever an option changes); OK traces the full image and closes with the result.
/// </summary>
internal sealed class VectorizeDialog : Window
{
    private const int PreviewSize = 480;

    private readonly RasterImage _source;
    private readonly RasterImage _previewSource;
    private readonly double _previewScale;
    private readonly Bitmap _originalBitmap;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _previewCts;

    private readonly ComboBox _preset = new(), _mode = new(), _layering = new(), _denoise = new();
    private readonly Slider _colors = Slider(2, 64, 16), _speckle = Slider(0, 100, 8), _smooth = Slider(0, 1, 0.5), _corner = Slider(10, 170, 50);
    private readonly TextBlock _colorsText = new(), _speckleText = new(), _smoothText = new(), _cornerText = new();
    private readonly CheckBox _omitBackground = new() { Content = "Leave out the background" };
    private readonly Avalonia.Controls.Primitives.ToggleButton _showOriginal = new() { Content = "Show original", Focusable = false };
    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Name = "VectorizeProgress", IsIndeterminate = true, IsVisible = false };
    private ScrollViewer? _optionsHost;
    private CancellationTokenSource? _traceCts;
    private readonly Button _ok = new Button { Content = "Vectorize", IsDefault = true }.ScaledMinWidth(90);
    private readonly Button _cancel = new Button { Content = "Cancel", IsCancel = true }.ScaledMinWidth(90);
    private Bitmap? _resultBitmap;
    private bool _busy, _loading = true;

    /// <summary>The result of the full-size trace, set when the dialog was accepted.</summary>
    public VectorizeResult? Result { get; private set; }
    public VectorizeOptions? ChosenOptions { get; private set; }

    /// <summary>True while the full-size trace runs (Cancel then stops it; for tests).</summary>
    internal bool IsTracing => _busy;
    internal Button CancelButton => _cancel;
    internal Button VectorizeButton => _ok;

    /// <summary>The newest preview finished (for tests).</summary>
    internal VectorizeResult? LastPreview { get; private set; }
    internal int PreviewsCompleted { get; private set; }

    public VectorizeDialog(RasterImage source, string sourceName)
    {
        _source = source;
        _previewSource = source.LimitSize(PreviewSize);
        _previewScale = (double)_previewSource.Width / source.Width;
        using (var sk = SkiaImaging.ToBitmap(_previewSource))
            _originalBitmap = new Bitmap(new MemoryStream(SkiaImaging.EncodePng(sk)));

        Title = "Vectorize";
        DialogScale.Attach(this, 920, 640, 720, 520);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = BuildLayout(sourceName);
        ApplyPreset(0);
        _loading = false;

        _debounce.Tick += (_, _) => { _debounce.Stop(); StartPreview(); };
        _ok.Click += async (_, _) => await AcceptAsync();
        _cancel.Click += (_, _) => { if (_busy) _traceCts?.Cancel(); else Close(null); };   // while tracing, Cancel stops the trace and keeps the dialog
        _showOriginal.IsCheckedChanged += (_, _) => ShowCurrent();
        Opened += (_, _) => StartPreview();
        Closed += (_, _) => { _previewCts?.Cancel(); _traceCts?.Cancel(); _debounce.Stop(); };
    }

    private static Slider Slider(double min, double max, double value) => new Slider { Minimum = min, Maximum = max, Value = value }.ScaledMinWidth(180);

    private Control BuildLayout(string sourceName)
    {
        _preset.ItemsSource = new[] { "Logo / clipart (flat colors)", "Photo", "Black and white line art", "Outline only" };
        _mode.ItemsSource = new[] { "Color", "Black and white", "Outline only" };
        _layering.ItemsSource = new[] { "Stacked (no gaps, parts overlap)", "Cut out (parts abut, no overlap)" };
        _denoise.ItemsSource = new[] { "Off", "Light", "Medium", "Strong" };
        _mode.SelectedIndex = 0; _layering.SelectedIndex = 0; _denoise.SelectedIndex = 0;

        Row Make(string label, Control control, TextBlock? valueText = null)
        {
            var row = new StackPanel { Spacing = UiScale.Sp(2), Margin = UiScale.Space(0, 4) };
            row.Children.Add(new TextBlock { Text = label });
            if (valueText is null) row.Children.Add(control);
            else
            {
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                line.Children.Add(control);
                valueText.HorizontalAlignment = HorizontalAlignment.Right;
                valueText.ScaledMinWidth(44);
                valueText.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(valueText, 1);
                line.Children.Add(valueText);
                row.Children.Add(line);
            }
            return new Row(row);
        }

        var options = new StackPanel { Margin = UiScale.Space(0, 0, 12, 0) }.ScaledWidth(280);
        options.Children.Add(Make("Preset", _preset).Panel);
        options.Children.Add(new Separator { Margin = UiScale.Space(0, 6) });
        options.Children.Add(Make("Output", _mode).Panel);
        options.Children.Add(Make("Layers", _layering).Panel);
        options.Children.Add(Make("Colors", _colors, _colorsText).Panel);
        options.Children.Add(Make("Noise filter: smallest part (pixels)", _speckle, _speckleText).Panel);
        options.Children.Add(Make("Noise filter: smooth photo grain", _denoise).Panel);
        options.Children.Add(Make("Curve smoothness", _smooth, _smoothText).Panel);
        options.Children.Add(Make("Corners sharper than (degrees)", _corner, _cornerText).Panel);
        options.Children.Add(_omitBackground);

        foreach (var s in new[] { _colors, _speckle, _smooth, _corner })
            s.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) OptionChanged(); };
        foreach (var c in new[] { _mode, _layering, _denoise })
            c.SelectionChanged += (_, _) => OptionChanged();
        _omitBackground.IsCheckedChanged += (_, _) => OptionChanged();
        _preset.SelectionChanged += (_, _) => { if (!_loading && _preset.SelectedIndex >= 0) ApplyPreset(_preset.SelectedIndex); };

        var optionScroll = _optionsHost = new ScrollViewer { Content = options, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var previewHost = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Child = _previewImage,
        };
        var previewHeader = new DockPanel { Margin = UiScale.Space(0, 0, 0, 6) };
        DockPanel.SetDock(_showOriginal, Dock.Right);
        previewHeader.Children.Add(_showOriginal);
        previewHeader.Children.Add(new TextBlock { Text = $"Preview  -  {sourceName}  ({_source.Width} x {_source.Height})", VerticalAlignment = VerticalAlignment.Center });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8), HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok);
        buttons.Children.Add(_cancel);

        var right = new DockPanel();
        DockPanel.SetDock(previewHeader, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_progress, Dock.Bottom);
        right.Children.Add(previewHeader);
        right.Children.Add(_progress);
        right.Children.Add(_status);
        right.Children.Add(previewHost);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(optionScroll);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var root = new DockPanel { Margin = UiScale.Space(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = UiScale.Space(0, 12, 0, 0);
        root.Children.Add(buttons);
        root.Children.Add(body);
        return root;
    }

    private sealed record Row(StackPanel Panel);

    // ---- Options ----

    private void ApplyPreset(int index)
    {
        _loading = true;
        switch (index)
        {
            case 0: Set(VectorizeMode.Color, VectorizeLayering.Stacked, 12, 8, 0, 0.5, 50); break;
            case 1: Set(VectorizeMode.Color, VectorizeLayering.Stacked, 24, 30, 2, 0.7, 60); break;
            case 2: Set(VectorizeMode.BlackAndWhite, VectorizeLayering.Stacked, 2, 6, 0, 0.5, 50); break;
            default: Set(VectorizeMode.Outline, VectorizeLayering.CutOut, 2, 6, 0, 0.5, 50); break;
        }
        _preset.SelectedIndex = index;
        _loading = false;
        OptionChanged();

        void Set(VectorizeMode mode, VectorizeLayering layering, int colors, int speckle, int denoise, double smooth, double corner)
        {
            _mode.SelectedIndex = (int)mode;
            _layering.SelectedIndex = (int)layering;
            _colors.Value = colors; _speckle.Value = speckle; _denoise.SelectedIndex = denoise;
            _smooth.Value = smooth; _corner.Value = corner;
        }
    }

    internal VectorizeOptions CurrentOptions() => new()
    {
        Mode = (VectorizeMode)Math.Max(0, _mode.SelectedIndex),
        Layering = (VectorizeLayering)Math.Max(0, _layering.SelectedIndex),
        Colors = (int)Math.Round(_colors.Value),
        SpeckleSize = (int)Math.Round(_speckle.Value),
        Denoise = Math.Max(0, _denoise.SelectedIndex),
        Smoothness = Math.Round(_smooth.Value, 2),
        CornerAngle = Math.Round(_corner.Value),
        OmitBackground = _omitBackground.IsChecked == true,
    };

    private void OptionChanged()
    {
        _colorsText.Text = ((int)Math.Round(_colors.Value)).ToString();
        _speckleText.Text = ((int)Math.Round(_speckle.Value)).ToString();
        _smoothText.Text = _smooth.Value.ToString("0.00");
        _cornerText.Text = ((int)Math.Round(_corner.Value)).ToString() + " deg";
        bool color = _mode.SelectedIndex <= 0;
        _colors.IsEnabled = color;
        _omitBackground.IsEnabled = color;
        _layering.IsEnabled = _mode.SelectedIndex != 2;
        if (_loading) return;
        _debounce.Stop();
        _debounce.Start();
    }

    // ---- Preview ----

    /// <summary>The preview sees a shrunken picture, so a speckle size in source pixels shrinks with it.</summary>
    private VectorizeOptions PreviewOptions(VectorizeOptions o) => o with
    {
        SpeckleSize = o.SpeckleSize == 0 ? 0 : Math.Max(1, (int)Math.Round(o.SpeckleSize * _previewScale * _previewScale)),
        MinWorkingSize = 600,
    };

    internal void StartPreview()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var options = PreviewOptions(CurrentOptions());
        _status.Text = "Tracing preview...";
        Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = Vectorizer.Trace(_previewSource, options, cts.Token);
            using var bmp = SkiaImaging.Render(result, 2, SKColors.White);
            return (result, png: SkiaImaging.EncodePng(bmp), ms: watch.ElapsedMilliseconds);
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (t.IsFaulted) { _status.Text = "Preview failed: " + t.Exception?.GetBaseException().Message; return; }
                var (result, png, ms) = t.Result;
                _resultBitmap?.Dispose();
                _resultBitmap = new Bitmap(new MemoryStream(png));
                LastPreview = result;
                PreviewsCompleted++;
                _status.Text = $"{result.Shapes.Count} parts, {result.NodeCount:N0} nodes, {result.Palette.Count} colors  (preview {ms} ms)";
                ShowCurrent();
            });
        }, TaskScheduler.Default);
    }

    private void ShowCurrent() =>
        _previewImage.Source = _showOriginal.IsChecked == true || _resultBitmap is null ? _originalBitmap : _resultBitmap;

    // ---- Accept ----

    internal async Task AcceptAsync()
    {
        if (_busy) return;
        _busy = true;
        _ok.IsEnabled = false;
        if (_optionsHost is not null) _optionsHost.IsEnabled = false;   // no option changes (and no new previews competing for the CPU) while tracing
        _previewCts?.Cancel();
        _debounce.Stop();
        _progress.IsVisible = true;
        _status.Text = "Vectorizing the full image...  (Cancel stops it)";
        var options = CurrentOptions();
        var cts = _traceCts = new CancellationTokenSource();
        try
        {
            Result = await Task.Run(() => Vectorizer.Trace(_source, options, cts.Token), cts.Token);
            ChosenOptions = options;
            Close(true);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled. Change the options and try again, or close the window.";
            Reset();
        }
        catch (Exception ex)
        {
            DeSignMage.Feedback.AppLog.Error("vectorize", "Vectorize failed", ex);
            _status.Text = "Vectorize failed: " + ex.Message;
            Reset();
        }

        void Reset()
        {
            _ok.IsEnabled = true;
            if (_optionsHost is not null) _optionsHost.IsEnabled = true;
            _progress.IsVisible = false;
            _traceCts = null;
            _busy = false;
            OptionChanged();   // refreshes the preview
        }
    }
}

