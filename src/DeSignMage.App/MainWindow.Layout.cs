using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Model;

namespace DeSignMage.App;

/// <summary>Layout menu: Guides ▸ (guides on edges, centers and text lines), Layout Properties... and Size to Fit. No shortcuts until the user chooses them.</summary>
public partial class MainWindow
{
    /// <summary>The ids of the Layout ▸ Guides ▸ submenu, in menu order ("-" = separator).</summary>
    internal static string[] GuideMenuCommandIds =>
    [
        "layout.guide.capHeight", "layout.guide.baseline", "-",
        "layout.guide.top", "layout.guide.left", "layout.guide.right", "layout.guide.bottom", "layout.guide.edges", "-",
        "layout.guide.centerV", "layout.guide.centerH", "layout.guide.centers",
    ];

    /// <summary>The Layout Properties dialog while it is open (for tests); null otherwise.</summary>
    internal LayoutPropertiesDialog? OpenLayoutProperties { get; private set; }

    private void RegisterLayoutCommands()
    {
        void Place(string id, string header, GuidePlace place) =>
            _commands.Add(new AppCommand(id, header, () => _session.AddGuides(place)) { CanExecute = () => _session.CanAddGuides(place) });

        Place("layout.guide.capHeight", "Add Guide At Text _Cap Height", GuidePlace.TextCapHeight);
        Place("layout.guide.baseline", "Add Guide At Text _Baseline", GuidePlace.TextBaseline);
        Place("layout.guide.top", "Add Guide Along _Top Edge", GuidePlace.TopEdge);
        Place("layout.guide.left", "Add Guide Along _Left Edge", GuidePlace.LeftEdge);
        Place("layout.guide.right", "Add Guide Along _Right Edge", GuidePlace.RightEdge);
        Place("layout.guide.bottom", "Add Guide Along Bo_ttom Edge", GuidePlace.BottomEdge);
        Place("layout.guide.edges", "Add Guides Along _All Edges", GuidePlace.AllEdges);
        Place("layout.guide.centerV", "Add Guide at _Vertical Center", GuidePlace.VerticalCenter);
        Place("layout.guide.centerH", "Add Guide at _Horizontal Center", GuidePlace.HorizontalCenter);
        Place("layout.guide.centers", "Add Guides at _Both Centers", GuidePlace.BothCenters);

        _commands.Add(new AppCommand("layout.properties", "Layout _Properties...", () => _ = ShowLayoutPropertiesAsync()));
        _commands.Add(new AppCommand("layout.sizeToFit", "_Size to Fit", () => _session.SizeToFit()) { CanExecute = () => _session.CanSizeToFit });
    }

    /// <summary>The page size new layouts start with: the saved one (Layout Properties ▸ "Use this size for new layouts"), else 24 x 12 inches.</summary>
    internal static (double Width, double Height) DefaultLayoutInches() =>
        (AppSettings.Store.Get(DefaultWidthSetting, 24.0), AppSettings.Store.Get(DefaultHeightSetting, 12.0));

    internal const string DefaultWidthSetting = "layout.defaultWidth", DefaultHeightSetting = "layout.defaultHeight";

    private async Task ShowLayoutPropertiesAsync()
    {
        var page = _session.Document.Page;
        var (dw, dh) = DefaultLayoutInches();
        bool isDefault = Math.Abs(dw - Units.ToInches(page.Width)) < 1e-4 && Math.Abs(dh - Units.ToInches(page.Height)) < 1e-4 &&
                         AppSettings.Store.Has(DefaultWidthSetting);
        var dialog = new LayoutPropertiesDialog(page, _session.ShowGuides, _session.SnapToGuides, isDefault);
        OpenLayoutProperties = dialog;
        try { await dialog.ShowDialog(this); }
        finally { OpenLayoutProperties = null; }
        if (dialog.Result is { } result) ApplyLayoutProperties(result, dialog.WasDefaultSize);
    }

    /// <summary>Applies what Layout Properties asked for: the page size is one undo step; the guide switches and the new-layout size are settings.</summary>
    internal void ApplyLayoutProperties(LayoutPropertiesResult r, bool wasDefaultSize)
    {
        _session.SetPageSize(Units.FromInches(r.WidthInches), Units.FromInches(r.HeightInches));
        _session.ShowGuides = r.ShowGuides;
        _session.SnapToGuides = r.SnapToGuides;
        if (r.UseAsDefaultSize)
        {
            AppSettings.Store.Set(DefaultWidthSetting, Math.Round(r.WidthInches, 4));
            AppSettings.Store.Set(DefaultHeightSetting, Math.Round(r.HeightInches, 4));
        }
        else if (wasDefaultSize)
        {
            AppSettings.Store.Set(DefaultWidthSetting, 24.0);   // back to the built-in size
            AppSettings.Store.Set(DefaultHeightSetting, 12.0);
        }
        QueueRefresh();
    }
}
