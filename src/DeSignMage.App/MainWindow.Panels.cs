using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DeSignMage.App.Shell;

namespace DeSignMage.App;

/// <summary>
/// The Dock's panels (Color Mixer, Layers, History): their Dock Tabs sit at the top of the Dock, and <see cref="DockLayout"/> decides which
/// one is open (R17). The open panel fills the Dock; with none open the Dock is only its tab strip and the canvas takes the room. The
/// program always starts with every panel closed. The Palette Strip is not a Dock panel: it is always there.
/// </summary>
public partial class MainWindow
{
    private readonly DockLayout _dock = new();
    private Dictionary<string, (Control Panel, ToggleButton Tab)> _dockParts = [];

    /// <summary>The Dock panel that is open now, or null (tests).</summary>
    internal string? OpenDockPanel => _dock.Open;

    /// <summary>What a click on a panel's Dock Tab does (tests click the tab itself).</summary>
    internal void ToggleDockPanel(string id) => _dock.Toggle(id);

    internal bool DockVisible => _dock.Open is not null;

    private void InitDockPanels()
    {
        _dockParts = new()
        {
            ["colorMixer"] = (ColorMixerPanel, DockTabColorMixer),
            ["layers"] = (LayersPanel, DockTabLayers),
            ["history"] = (HistoryPanel, DockTabHistory),
        };
        foreach (var (id, parts) in _dockParts)
        {
            string panelId = id;
            parts.Tab.Click += (_, _) => _dock.Toggle(panelId);
        }
        _dock.Changed += (_, _) =>
        {
            ApplyDockLayout();
            QueueRefresh();
        };
        ApplyDockLayout();
    }

    /// <summary>
    /// Shows the open panel, presses (melts) its tab, and shrinks the Dock to just its tab strip when none is open, so the ruler and canvas
    /// take the full width below the band.
    /// </summary>
    private void ApplyDockLayout()
    {
        if (_dockParts.Count == 0) return;
        foreach (var (id, parts) in _dockParts)
        {
            bool open = _dock.IsOpen(id);
            parts.Panel.IsVisible = open;
            parts.Tab.IsChecked = open;
        }

        bool dock = _dock.Open is not null;
        DockGrid.IsVisible = DockSplitter.IsVisible = dock;
        // The Dock keeps about the same share of the window: wider as the text grows, but never more than 40% of it. Closed, it is
        // only as wide as its tab strip, and the work area (ruler, canvas) spans the columns it no longer uses.
        double want = 270 * UiScale.Scale, max = Math.Max(270, Bounds.Width * 0.4);
        BodyGrid.ColumnDefinitions[1].Width = new GridLength(dock ? 4 : 0);
        BodyGrid.ColumnDefinitions[2].Width = dock ? new GridLength(Math.Min(want, max)) : GridLength.Auto;
        Grid.SetColumnSpan(WorkArea, dock ? 1 : 3);
    }
}
