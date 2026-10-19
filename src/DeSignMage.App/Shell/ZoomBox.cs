using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DeSignMage.Core.Editing;

namespace DeSignMage.App.Shell;

/// <summary>
/// The zoom box of the Main Toolbar (R38): shows the zoom of the open design as a percentage. Type a number and press Enter to set it (a
/// "%" is allowed, nothing sensible is ignored: a bad entry goes back to the current zoom); the small arrow beside the box opens the presets
/// (Fit page, 25, 50, 100, 200, 400 %). A preset or a typed value zooms about the middle of the drawing area, like Zoom In and Zoom Out.
/// Escape leaves the box without applying what was typed (<see cref="CancelTyping"/>), and Enter hands the keyboard back to the canvas.
/// </summary>
public sealed class ZoomBox : StackPanel
{
    /// <summary>The zoom presets of the drop-down, in percent, after "Fit page".</summary>
    public static readonly double[] Presets = [25, 50, 100, 200, 400];

    private readonly TextBox _text = new();
    private readonly Button _arrow;
    private readonly ContextMenu _menu;
    private readonly MenuItem _fit;
    private readonly List<(double Percent, MenuItem Item)> _presetItems = [];
    private EditorSession? _session;
    private bool _refreshing, _edited;

    public ZoomBox()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(1);

        _text.Name = "ZoomPercentBox";
        _text.Classes.Add("num");
        _text.ScaledWidth(60);
        _text.TextAlignment = TextAlignment.Right;
        ToolTip.SetTip(_text, "Zoom: type a percentage and press Enter");
        Avalonia.Automation.AutomationProperties.SetName(_text, "Zoom percentage");
        _text.TextChanged += (_, _) => { if (!_refreshing) _edited = true; };
        _text.GotFocus += (_, _) => Dispatcher.UIThread.Post(() => { if (_text.IsFocused) _text.SelectAll(); }, DispatcherPriority.Input);
        _text.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            ContextBars.FocusCanvas?.Invoke();
            e.Handled = true;
        };
        _text.LostFocus += (_, _) =>
        {
            Commit();
            Refresh();   // whatever the zoom is now, also when it changed while the box had the keyboard
        };

        var chevron = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M0,0 L3.5,3.5 L7,0"), StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        chevron.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, chevron.GetResourceObservable("Ui.Icon"));
        _arrow = new Button { Name = "ZoomPresetsButton", Content = chevron, Focusable = false };
        _arrow.Classes.Add("tb");
        _arrow.Margin = new Thickness(1, 0, 0, 0);
        _arrow.Padding = new Thickness(0);
        _arrow.ScaledMinWidth(18, icon: true);
        ToolTip.SetTip(_arrow, "Zoom presets");
        Avalonia.Automation.AutomationProperties.SetName(_arrow, "Zoom presets");

        _menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _fit = new MenuItem { Header = "Fit page" };
        _fit.Click += (_, _) => { _session?.ZoomToPage(push: true); ContextBars.FocusCanvas?.Invoke(); };
        _menu.Items.Add(_fit);
        _menu.Items.Add(new Separator());
        foreach (double percent in Presets)
        {
            double p = percent;
            var item = new MenuItem { Header = $"{p:0}%" };
            item.Click += (_, _) => { SetPercent(p); ContextBars.FocusCanvas?.Invoke(); };
            _presetItems.Add((p, item));
            _menu.Items.Add(item);
        }
        _arrow.Click += (_, _) => _menu.Open(this);

        Children.Add(_text);
        Children.Add(_arrow);
    }

    /// <summary>The design's session whose zoom is shown and set; the window sets it whenever a new design is loaded.</summary>
    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session is not null) _session.Viewport.Changed -= OnViewChanged;
            _session = value;
            if (_session is not null) _session.Viewport.Changed += OnViewChanged;
            Refresh();
        }
    }

    private void OnViewChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Shows the current zoom (unless a value is being typed in the box).</summary>
    public void Refresh()
    {
        if (_session is null || _text.IsFocused) return;
        _refreshing = true;
        _text.Text = Format(_session.Viewport.ZoomPercent);
        _edited = false;
        _refreshing = false;
    }

    /// <summary>The zoom as the box shows it: "38.8%", "100%".</summary>
    internal static string Format(double percent) => percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>Reads a typed zoom: a positive number, with or without a "%" sign.</summary>
    internal static bool TryParsePercent(string? text, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out percent) &&
            !double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out percent)) return false;
        return double.IsFinite(percent) && percent > 0;
    }

    /// <summary>Zooms to <paramref name="percent"/> about the middle of the drawing area (the Viewport keeps it within its limits).</summary>
    public void SetPercent(double percent)
    {
        if (_session is null) return;
        var view = _session.Viewport;
        view.ZoomAt(view.ViewSize / 2, percent / view.ZoomPercent);
    }

    /// <summary>Escape: forget what was typed (the window then hands the keyboard to the canvas, which shows the real zoom again).</summary>
    public void CancelTyping() => _edited = false;

    private void Commit()
    {
        if (!_edited) return;
        _edited = false;
        if (TryParsePercent(_text.Text, out var percent)) SetPercent(percent);
        _refreshing = true;
        _text.Text = _session is null ? "" : Format(_session.Viewport.ZoomPercent);   // what the zoom really is now (it may have been kept within its limits)
        _refreshing = false;
    }

    // ---- For tests ----

    internal TextBox Box => _text;
    internal Button PresetsButton => _arrow;
    internal ContextMenu PresetMenu => _menu;
    internal MenuItem FitItem => _fit;
    internal IReadOnlyList<(double Percent, MenuItem Item)> PresetItems => _presetItems;
}
