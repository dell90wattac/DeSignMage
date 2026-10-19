using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;
using DeSignMage.Imaging;
using static DeSignMage.App.Shell.DialogParts;

namespace DeSignMage.App.Shell;

/// <summary>
/// The Picture Editor (double-click a picture, or Image ▸ Edit Picture...). Non-destructive: the original pixels stay and what is done to them is a recipe
/// (<see cref="PictureRecipe"/>) that can be changed again whenever the editor is opened.
/// <list type="bullet">
/// <item>Top: one tab per kind of edit that exists: <b>Crop</b> (frame, exact size, quarter turns and flips, free rotate with a dial, straighten by a level line, auto-trim),
/// <b>Adjust</b> (levels, brightness, contrast, gamma, saturation, hue, color balance), <b>Transparency</b> (see <c>PictureEditorDialog.Transparency.cs</c>) and <b>Effects</b>
/// (noise reduction, blur, sharpen; grayscale, sepia, invert; black &amp; white, posterize). Tabs for edits that are not built yet are not shown. <b>Edit in...</b> sits at the right of the tab row.</item>
/// <item>Middle: the picture as it is before cropping and coloring, with the crop frame (left), and the result (right), both on the transparency checkerboard
/// at the same zoom and position. One large view and hold-to-compare views are one click away.</item>
/// <item>Bottom: Reset all, Cancel, OK. While the window is open the picture in the design behind it shows the preview; Cancel puts everything back.</item>
/// </list>
/// </summary>
internal sealed partial class PictureEditorDialog : Window
{
    internal enum ViewMode { SideBySide, Single, Compare }

    /// <summary>One slider with its number box and reset button, on the Adjust, Transparency or Effects tab; it reads and writes one number of the recipe.</summary>
    private sealed class SliderRow
    {
        public required Control Container { get; init; }
        public required string Name { get; init; }
        public required Slider Slider { get; init; }
        public required TextBox Box { get; init; }
        public required Button Reset { get; init; }
        public required Func<PictureRecipe, double> Get { get; init; }
        public required Func<PictureRecipe, double, PictureRecipe> Set { get; init; }
        public required double Default { get; init; }
        public required string Format { get; init; }
    }

    private readonly EditorSession _editor;
    private readonly List<SliderRow> _rows = [];
    private bool _syncing;

    // ---- crop gesture state
    private enum Drag { None, Move, Resize, Draw, Line }
    private Drag _drag;
    private RectI _dragStart;
    private Handle _dragHandle;
    private Vec2 _dragFrom;

    internal PictureEditSession Session { get; }

    /// <summary>True when OK was pressed and the picture was changed (one undo step); false after Cancel or when nothing differed.</summary>
    public bool Committed { get; private set; }

    // ---- tabs and tool controls (internal for the tests)
    internal TabControl Tabs { get; } = new();
    internal TabItem CropTab { get; } = new() { Header = "Crop", Padding = UiScale.Space(14, 6), FontWeight = FontWeight.SemiBold };
    internal TabItem AdjustTab { get; } = new() { Header = "Adjust", Padding = UiScale.Space(14, 6), FontWeight = FontWeight.SemiBold };
    internal TabItem EffectsTab { get; } = new() { Header = "Effects", Padding = UiScale.Space(14, 6), FontWeight = FontWeight.SemiBold };

    // the Crop tab's turning controls: quarter turns and flips (they change the orientation of the recipe), the free angle and its dial
    internal Button RotateLeftButton { get; } = Small("↶ Rotate left");
    internal Button RotateRightButton { get; } = Small("↷ Rotate right");
    internal Button FlipHorizontalButton { get; } = Small("⇆ Flip horizontal");
    internal Button FlipVerticalButton { get; } = Small("⇅ Flip vertical");
    internal Button ResetOrientationButton { get; } = Small("Original way up");
    internal AngleDial Dial { get; } = new();

    // the Effects tab's two switches (the other effects are sliders)
    internal CheckBox GrayscaleBox { get; } = new() { Content = "Grayscale" };
    internal CheckBox InvertBox { get; } = new() { Content = "Invert (negative)" };

    internal TextBox CropWidthBox { get; } = Field();
    internal TextBox CropHeightBox { get; } = Field();
    internal TextBox CropWidthInBox { get; } = Field();
    internal TextBox CropHeightInBox { get; } = Field();
    internal CheckBox LockAspectBox { get; } = new() { Content = "Keep proportions" };
    internal Button ResetCropButton { get; } = Small("Whole picture");
    internal TextBox AngleBox { get; } = Field();
    internal ToggleButton LevelToolButton { get; } = new ToggleButton { Content = "Draw a level line" }.Padded();
    internal Button RemoveCornersButton { get; } = Small("Remove empty corners");
    internal Button ResetAngleButton { get; } = Small("No turn");
    internal Button AutoTrimButton { get; } = Small("Auto-trim borders");
    internal TextBox TrimToleranceBox { get; } = Field(60, "12");
    internal Button AutoLevelsButton { get; } = Small("Auto levels");
    internal Button ResetLevelsButton { get; } = Small("Reset levels");
    internal TextBlock LevelsNote { get; } = Note();
    internal TextBlock CropNote { get; } = Note();

    // ---- views
    internal RadioButton SideBySideRadio { get; } = new() { Content = "Side by side", GroupName = "view" };
    internal RadioButton SingleRadio { get; } = new() { Content = "Result only", GroupName = "view" };
    internal RadioButton CompareRadio { get; } = new() { Content = "Compare", GroupName = "view" };
    internal Button HoldButton { get; } = Small("Hold to see the original");
    internal Button FitButton { get; } = Small("Fit");
    internal Button FitResultButton { get; } = Small("Fit result");
    internal Button ActualButton { get; } = Small("100%");
    internal TextBlock ZoomText { get; } = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted") };
    internal PicturePane LeftPane { get; } = new() { Caption = "Original" };
    internal PicturePane RightPane { get; } = new() { Caption = "Result" };

    // ---- bottom
    internal Button ResetAllButton { get; } = Small("Reset all");
    internal Button OkButton { get; } = new Button { Content = "OK", IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);
    internal Button CancelButton { get; } = new Button { Content = "Cancel", IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center }.ScaledMinWidth(96);
    internal TextBlock SummaryText { get; } = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted"), TextWrapping = TextWrapping.Wrap };

    private readonly PictureView _view = new();
    private readonly Grid _paneGrid = new() { ColumnSpacing = 8 };
    private bool _holding, _fitted;

    internal ViewMode Mode { get; private set; } = ViewMode.SideBySide;

    public PictureEditorDialog(EditorSession editor, ImageObject image, PixelBuffer source, string sourceAssetId)
    {
        _editor = editor;
        Session = new PictureEditSession(editor, image, source, sourceAssetId);
        Title = "Picture Editor";
        DialogScale.Attach(this, 1120, 780, 900, 640);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        LeftPane.Attach(_view);
        RightPane.Attach(_view);

        BuildCropTab();
        BuildAdjustTab();
        BuildTransparencyTab();
        BuildEffectsTab();
        Tabs.Items.Add(CropTab);
        Tabs.Items.Add(AdjustTab);
        Tabs.Items.Add(TransparencyTab);
        Tabs.Items.Add(EffectsTab);
        Tabs.SelectionChanged += (_, _) => { OnTabChanged(); SyncFields(); };
        BuildEditIn();

        // ---- the area between: view buttons over the two panes
        var viewBar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = UiScale.Sp(10), LineSpacing = UiScale.Sp(4), Margin = UiScale.Space(0, 8, 0, 6) };
        viewBar.Children.Add(SideBySideRadio);
        viewBar.Children.Add(SingleRadio);
        viewBar.Children.Add(CompareRadio);
        viewBar.Children.Add(HoldButton);
        viewBar.Children.Add(new Border { Width = 1, Background = AppTheme.Brush("Ui.ChromeBorder"), Margin = UiScale.Space(4, 2) });
        viewBar.Children.Add(FitButton);
        viewBar.Children.Add(FitResultButton);
        viewBar.Children.Add(ActualButton);
        viewBar.Children.Add(ZoomText);
        ToolTip.SetTip(LeftPane, "Mouse wheel zooms. Drag with the middle or right button to move the picture. Both panes always show the same part.");

        _paneGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        _paneGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(LeftPane, 0);
        Grid.SetColumn(RightPane, 1);
        _paneGrid.Children.Add(LeftPane);
        _paneGrid.Children.Add(RightPane);

        var bottomButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8), Children = { CancelButton, OkButton } };
        var bottom = new DockPanel { Margin = UiScale.Space(0, 10, 0, 0) };
        DockPanel.SetDock(bottomButtons, Dock.Right);
        DockPanel.SetDock(ResetAllButton, Dock.Left);
        ResetAllButton.Margin = UiScale.Space(0, 0, 12, 0);
        bottom.Children.Add(bottomButtons);
        bottom.Children.Add(ResetAllButton);
        bottom.Children.Add(SummaryText);

        var middle = new DockPanel();
        DockPanel.SetDock(viewBar, Dock.Top);
        middle.Children.Add(viewBar);
        middle.Children.Add(_paneGrid);

        var root = new DockPanel { Margin = UiScale.Space(14) };
        var tabArea = new Grid { Children = { Tabs, EditInHost } };   // Edit in... floats at the right end of the tab row
        DockPanel.SetDock(tabArea, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(tabArea);
        root.Children.Add(bottom);
        root.Children.Add(middle);
        Content = root;

        // ---- behavior
        SideBySideRadio.IsChecked = true;
        foreach (var (radio, mode) in new[] { (SideBySideRadio, ViewMode.SideBySide), (SingleRadio, ViewMode.Single), (CompareRadio, ViewMode.Compare) })
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) SetMode(mode); };
        HoldButton.AddHandler(PointerPressedEvent, (_, _) => Hold(true), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        HoldButton.AddHandler(PointerReleasedEvent, (_, _) => Hold(false), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        HoldButton.PointerCaptureLost += (_, _) => Hold(false);
        FitButton.Click += (_, _) => FitCanvas();
        FitResultButton.Click += (_, _) => FitResult();
        ActualButton.Click += (_, _) => _view.Set(1, _view.CenterX, _view.CenterY);
        _view.Changed += () => ZoomText.Text = $"{_view.Zoom * 100:0}%";
        ResetAllButton.Click += (_, _) => { Session.ResetAll(); SyncFields(); };
        OkButton.Click += async (_, _) => await AcceptAsync();
        CancelButton.Click += (_, _) => Close();
        Closed += (_, _) => Session.Cancel();   // a no-op after OK
        LeftPane.CanvasPointerPressed += (p, e) => LeftDown(p, e);
        LeftPane.CanvasPointerMoved += (p, _) => LeftMove(p);
        LeftPane.CanvasPointerReleased += (p, _) => LeftUp(p);
        // On the Transparency tab the result pane takes the lasso and the color picker as well (the panes share one view, so the points are the same).
        RightPane.CanvasPointerPressed += (p, e) => { if (OnTransparencyTab) TransparencyDown(p, e); };
        RightPane.CanvasPointerMoved += (p, _) => { if (OnTransparencyTab) TransparencyMove(p); };
        RightPane.CanvasPointerReleased += (p, _) => { if (OnTransparencyTab) TransparencyUp(p); };
        AddHandler(KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);   // before the Cancel and OK buttons see Esc and Enter
        _paneGrid.SizeChanged += (_, _) => { if (!_fitted) FitCanvas(); };
        Session.PreviewReady += OnPreviewReady;

        SyncFields();
        Opened += (_, _) => { Session.RequestPreview(); };
    }

    // ---------------------------------------------------------------- building

    private static TextBox Field(double width = 76, string? text = null) =>
        new TextBox { HorizontalContentAlignment = HorizontalAlignment.Right, Text = text }.ScaledWidth(width);

    private static Button Small(string text) => new Button { Content = text, Padding = UiScale.Space(10, 3), Focusable = false };

    private static Control Labeled(string label, Control field)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(6) };
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Label") });
        p.Children.Add(field);
        return p;
    }

    private static WrapPanel Line() => new() { Orientation = Orientation.Horizontal, ItemSpacing = UiScale.Sp(12), LineSpacing = UiScale.Sp(6), Margin = UiScale.Space(0, 2) };

    private void BuildCropTab()
    {
        var sizeLine = Line();
        sizeLine.Children.Add(SectionLabelInline("Frame"));
        sizeLine.Children.Add(Labeled("Width", WithUnit(CropWidthBox, "px")));
        sizeLine.Children.Add(Labeled("Height", WithUnit(CropHeightBox, "px")));
        sizeLine.Children.Add(Labeled("or", WithUnit(CropWidthInBox, "in wide")));
        sizeLine.Children.Add(WithUnit(CropHeightInBox, "in high"));
        sizeLine.Children.Add(LockAspectBox);
        sizeLine.Children.Add(ResetCropButton);
        sizeLine.Children.Add(AutoTrimButton);
        sizeLine.Children.Add(Labeled("tolerance", TrimToleranceBox));

        var wayLine = Line();
        wayLine.Children.Add(SectionLabelInline("Turn and flip"));
        wayLine.Children.Add(RotateLeftButton);
        wayLine.Children.Add(RotateRightButton);
        wayLine.Children.Add(FlipHorizontalButton);
        wayLine.Children.Add(FlipVerticalButton);
        wayLine.Children.Add(ResetOrientationButton);

        var turnLine = Line();
        turnLine.Children.Add(SectionLabelInline("Rotate"));
        turnLine.Children.Add(Dial);
        turnLine.Children.Add(Labeled("Angle", WithUnit(AngleBox, "degrees")));
        turnLine.Children.Add(LevelToolButton);
        turnLine.Children.Add(RemoveCornersButton);
        turnLine.Children.Add(ResetAngleButton);
        turnLine.Children.Add(CropNote);
        foreach (var child in turnLine.Children.OfType<Control>().Where(c => c is not AngleDial)) child.VerticalAlignment = VerticalAlignment.Center;

        CropTab.Content = new StackPanel { Spacing = UiScale.Sp(2), Margin = UiScale.Space(0, 8, 0, 0), Children = { sizeLine, wayLine, turnLine } };
        ToolTip.SetTip(LevelToolButton, "Drag a line along something that should be level (or upright) in the left picture; the picture is turned to fix it.");
        ToolTip.SetTip(AutoTrimButton, "Cuts away plain borders of one color (or empty corners).");
        ToolTip.SetTip(RotateLeftButton, "Turns the whole picture, its crop and its angle, a quarter turn counter-clockwise.");
        ToolTip.SetTip(RotateRightButton, "Turns the whole picture, its crop and its angle, a quarter turn clockwise.");
        ToolTip.SetTip(FlipHorizontalButton, "Mirrors the picture left to right.");
        ToolTip.SetTip(FlipVerticalButton, "Mirrors the picture top to bottom.");
        ToolTip.SetTip(ResetOrientationButton, "Undoes the quarter turns and flips; the crop and the angle go along.");

        RotateLeftButton.Click += (_, _) => Orient(r => r.RotatedLeft());
        RotateRightButton.Click += (_, _) => Orient(r => r.RotatedRight());
        FlipHorizontalButton.Click += (_, _) => Orient(r => r.FlippedHorizontally());
        FlipVerticalButton.Click += (_, _) => Orient(r => r.FlippedVertically());
        ResetOrientationButton.Click += (_, _) => Orient(r => r.InOriginalOrientation());
        Dial.AngleChanged += a => { if (!_syncing) { Session.Change(r => WithStraighten(r, a)); SyncFields(); } };

        foreach (var (box, which) in new[] { (CropWidthBox, 0), (CropHeightBox, 1), (CropWidthInBox, 2), (CropHeightInBox, 3) })
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_syncing) OnCropFieldTyped(which, box); };
        AngleBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_syncing) OnAngleTyped(); };
        ResetCropButton.Click += (_, _) => { Session.Change(r => r with { Crop = null }); SyncFields(); };
        ResetAngleButton.Click += (_, _) => { Session.Change(r => WithStraighten(r, 0)); SyncFields(); };
        RemoveCornersButton.Click += (_, _) =>
        {
            Session.Change(r => r with { Crop = PictureGeometry.InscribedCrop(r.OrientedSize.Width, r.OrientedSize.Height, r.Straighten) });
            SyncFields();
        };
        AutoTrimButton.Click += (_, _) => AutoTrim();
        LevelToolButton.IsCheckedChanged += (_, _) => LeftPane.Cursor = LevelToolButton.IsChecked == true ? new Cursor(StandardCursorType.Cross) : null;
    }

    private static TextBlock SectionLabelInline(string text) =>
        new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("Ui.Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = UiScale.Space(0, 0, 8, 0) }.ScaledMinWidth(70);

    private void BuildAdjustTab()
    {
        var grid = new Grid { ColumnSpacing = UiScale.Sp(10), RowSpacing = UiScale.Sp(4), Margin = UiScale.Space(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        // two columns of rows so the tab stays short
        var left = new StackPanel { Spacing = UiScale.Sp(4) };
        var right = new StackPanel { Spacing = UiScale.Sp(4) };
        var tone = Section("Tone", left);
        var color = Section("Color", right);

        AddRow(left, "Brightness", -100, 100, 0, "0", r => r.Adjust.Brightness, (r, v) => r with { Adjust = r.Adjust with { Brightness = v } });
        AddRow(left, "Contrast", -100, 100, 0, "0", r => r.Adjust.Contrast, (r, v) => r with { Adjust = r.Adjust with { Contrast = v } });
        AddRow(left, "Gamma", 0.1, 5, 1, "0.00", r => r.Adjust.Gamma, (r, v) => r with { Adjust = r.Adjust with { Gamma = v } });
        var levels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiScale.Sp(8), Children = { AutoLevelsButton, ResetLevelsButton, LevelsNote } };
        left.Children.Add(levels);
        AddRow(right, "Saturation", -100, 100, 0, "0", r => r.Adjust.Saturation, (r, v) => r with { Adjust = r.Adjust with { Saturation = v } });
        AddRow(right, "Hue", -180, 180, 0, "0", r => r.Adjust.Hue, (r, v) => r with { Adjust = r.Adjust with { Hue = v } });
        AddRow(right, "Red", -100, 100, 0, "0", r => r.Adjust.BalanceR, (r, v) => r with { Adjust = r.Adjust with { BalanceR = v } });
        AddRow(right, "Green", -100, 100, 0, "0", r => r.Adjust.BalanceG, (r, v) => r with { Adjust = r.Adjust with { BalanceG = v } });
        AddRow(right, "Blue", -100, 100, 0, "0", r => r.Adjust.BalanceB, (r, v) => r with { Adjust = r.Adjust with { BalanceB = v } });

        var two = new Grid { ColumnSpacing = UiScale.Sp(28), Margin = UiScale.Space(0, 8, 0, 0) };
        two.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        two.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(tone, 0);
        Grid.SetColumn(color, 1);
        two.Children.Add(tone);
        two.Children.Add(color);
        AdjustTab.Content = two;

        ToolTip.SetTip(AutoLevelsButton, "Sets the black and white points of each color from the picture as cropped (0.5% of the pixels at each end are ignored).");
        AutoLevelsButton.Click += (_, _) => AutoLevels();
        ResetLevelsButton.Click += (_, _) =>
        {
            Session.Change(r => r with { Adjust = r.Adjust with { LowR = 0, LowG = 0, LowB = 0, HighR = 255, HighG = 255, HighB = 255 } });
            SyncFields();
        };
    }

    private void BuildEffectsTab()
    {
        var clean = new StackPanel { Spacing = UiScale.Sp(4) };
        var look = new StackPanel { Spacing = UiScale.Sp(4) };
        var reduce = new StackPanel { Spacing = UiScale.Sp(4) };
        var cleanSection = Section("Clean up", clean);
        var lookSection = Section("Look", look);
        var reduceSection = Section("Fewer colors", reduce);

        AddRow(clean, "Noise", 0, 100, 0, "0", r => r.Effects.NoiseReduction, (r, v) => r with { Effects = r.Effects with { NoiseReduction = (int)Math.Round(v) } },
            "Noise reduction: removes specks by replacing each pixel with the median of its neighbors. Best judged at 100%.");
        AddRow(clean, "Blur", 0, PictureEffects.MaxBlur, 0, "0.#", r => r.Effects.Blur, (r, v) => r with { Effects = r.Effects with { Blur = v } },
            "Softens the picture; the radius is in pixels of the original.");
        AddRow(clean, "Sharpen", 0, 100, 0, "0", r => r.Effects.Sharpen, (r, v) => r with { Effects = r.Effects with { Sharpen = (int)Math.Round(v) } },
            "Makes edges crisper (unsharp mask). Best judged at 100%.");

        look.Children.Add(GrayscaleBox);
        AddRow(look, "Sepia", 0, 100, 0, "0", r => r.Effects.Sepia, (r, v) => r with { Effects = r.Effects with { Sepia = (int)Math.Round(v) } },
            "Replaces the colors by a warm brown tone, this much.");
        look.Children.Add(InvertBox);

        AddRow(reduce, "Black & white", 0, 255, 0, "0", r => r.Effects.Threshold, (r, v) => r with { Effects = r.Effects with { Threshold = (int)Math.Round(v) } },
            "Every pixel at least this light becomes white, every other one black. 0 = off. A good start for Vectorize.");
        AddRow(reduce, "Levels", 1, PictureEffects.MaxPosterize, 1, "0", r => r.Effects.Posterize, (r, v) => r with { Effects = r.Effects with { Posterize = (int)Math.Round(v) } },
            "Posterize: how many different values each color keeps (2 = very flat). 1 = off. Fewer colors make Vectorize trace flatter shapes.");

        var three = new Grid { ColumnSpacing = UiScale.Sp(28), Margin = UiScale.Space(0, 8, 0, 0) };
        for (int i = 0; i < 3; i++) three.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(cleanSection, 0); Grid.SetColumn(lookSection, 1); Grid.SetColumn(reduceSection, 2);
        three.Children.Add(cleanSection); three.Children.Add(lookSection); three.Children.Add(reduceSection);
        EffectsTab.Content = three;

        GrayscaleBox.IsCheckedChanged += (_, _) => { if (!_syncing) { Session.Change(r => r with { Effects = r.Effects with { Grayscale = GrayscaleBox.IsChecked == true } }); SyncFields(); } };
        InvertBox.IsCheckedChanged += (_, _) => { if (!_syncing) { Session.Change(r => r with { Effects = r.Effects with { Invert = InvertBox.IsChecked == true } }); SyncFields(); } };
    }

    private Grid AddRow(Panel into, string name, double min, double max, double neutral, string format, Func<PictureRecipe, double> get, Func<PictureRecipe, double, PictureRecipe> set, string? tip = null)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = neutral, SmallChange = (max - min) / 200, LargeChange = (max - min) / 20, HorizontalAlignment = HorizontalAlignment.Stretch };
        var box = Field(64);
        var reset = new Button { Content = "↺", Padding = UiScale.Space(6, 1), Focusable = false, IsVisible = false };
        ToolTip.SetTip(reset, $"Reset {name.ToLowerInvariant()}");
        var grid = new Grid { ColumnSpacing = UiScale.Sp(8) };
        var row = new SliderRow { Container = grid, Name = name, Slider = slider, Box = box, Reset = reset, Get = get, Set = set, Default = neutral, Format = format };
        _rows.Add(row);

        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Label") }.ScaledMinWidth(78);
        if (tip is not null) ToolTip.SetTip(grid, tip);
        slider.VerticalAlignment = VerticalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        reset.VerticalAlignment = VerticalAlignment.Center;
        // the reset button's place is kept when it is hidden, so the number boxes of all rows line up
        var resetHost = new Border { Child = reset, VerticalAlignment = VerticalAlignment.Center }.ScaledWidth(34);
        Grid.SetColumn(label, 0); Grid.SetColumn(slider, 1); Grid.SetColumn(box, 2); Grid.SetColumn(resetHost, 3);
        grid.Children.Add(label); grid.Children.Add(slider); grid.Children.Add(box); grid.Children.Add(resetHost);
        into.Children.Add(grid);

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncing) return;
            SetAdjust(row, Math.Round(slider.Value, row.Format.Length > 1 ? 2 : 0));
        };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _syncing) return;
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) SetAdjust(row, Math.Clamp(v, min, max), fromBox: true);
        };
        reset.Click += (_, _) => { SetAdjust(row, row.Default); SyncFields(); };
        return grid;
    }

    private void SetAdjust(SliderRow row, double value, bool fromBox = false)
    {
        Session.Change(r => row.Set(r, value));
        if (!fromBox) SyncFields(except: null);
        else SyncFields(except: row.Box);
    }

    // ---------------------------------------------------------------- keeping the fields in step with the recipe

    private double InchesPerPixel => 1.0 / Math.Max(Session.SourceDpi, 1e-6);

    private static string Num(double v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Writes the recipe into every field and shows only the controls that can do something (R26).</summary>
    internal void SyncFields(TextBox? except = null)
    {
        var r = Session.Recipe;
        _syncing = true;
        try
        {
            var crop = r.EffectiveCrop;
            if (!ReferenceEquals(except, CropWidthBox)) CropWidthBox.Text = crop.Width.ToString(CultureInfo.InvariantCulture);
            if (!ReferenceEquals(except, CropHeightBox)) CropHeightBox.Text = crop.Height.ToString(CultureInfo.InvariantCulture);
            if (!ReferenceEquals(except, CropWidthInBox)) CropWidthInBox.Text = Num(crop.Width * InchesPerPixel);
            if (!ReferenceEquals(except, CropHeightInBox)) CropHeightInBox.Text = Num(crop.Height * InchesPerPixel);
            if (!ReferenceEquals(except, AngleBox)) AngleBox.Text = Num(r.Straighten, "0.##");
            ResetCropButton.IsVisible = r.Crop is not null;
            ResetAngleButton.IsVisible = Math.Abs(r.Straighten) > 1e-6;
            RemoveCornersButton.IsVisible = Math.Abs(r.Straighten) > 1e-6 && Math.Abs(r.Straighten % 90) > 1e-6;
            ResetOrientationButton.IsVisible = r.Mirror || r.Quarter != 0;
            Dial.Angle = r.Straighten;
            GrayscaleBox.IsChecked = r.Effects.Grayscale;
            InvertBox.IsChecked = r.Effects.Invert;
            FitResultButton.IsVisible = r.Crop is not null;
            HoldButton.IsVisible = Mode == ViewMode.Compare;

            foreach (var row in _rows)
            {
                double v = row.Get(r);
                if (!ReferenceEquals(except, row.Box)) row.Box.Text = v.ToString(row.Format, CultureInfo.InvariantCulture);
                if (Math.Abs(row.Slider.Value - v) > 1e-9) row.Slider.Value = v;
                row.Reset.IsVisible = Math.Abs(v - row.Default) > 1e-9;
            }
            var a = r.Adjust;
            ResetLevelsButton.IsVisible = !a.LevelsAreDefault;
            LevelsNote.Text = a.LevelsAreDefault ? "" : $"Levels R {a.LowR}-{a.HighR}  G {a.LowG}-{a.HighG}  B {a.LowB}-{a.HighB}";
            ResetAllButton.IsVisible = !r.IsIdentity || Session.IsChanged;

            var (cw, ch) = r.CanvasSize;
            CropNote.Text = r.Crop is null && Math.Abs(r.Straighten) < 1e-6 ? "" : $"Result {crop.Width} × {crop.Height} px";
            SummaryText.Text = $"Original {Session.Source.Width} × {Session.Source.Height} px · result {crop.Width} × {crop.Height} px";
            LeftPane.CanvasWidth = RightPane.CanvasWidth = cw;
            LeftPane.CanvasHeight = RightPane.CanvasHeight = ch;
            LeftPane.Frame = crop;
            LeftPane.DimOutsideFrame = true;
            LeftPane.Visibility();
            SyncTransparency(r);
        }
        finally { _syncing = false; }
    }

    // ---------------------------------------------------------------- crop fields and gestures

    private void OnCropFieldTyped(int which, TextBox box)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return;
        var r = Session.Recipe;
        var (cw, ch) = r.CanvasSize;
        var cur = r.EffectiveCrop;
        double ppi = Session.SourceDpi;
        int w = cur.Width, h = cur.Height;
        switch (which)
        {
            case 0: w = (int)Math.Round(v); break;
            case 1: h = (int)Math.Round(v); break;
            case 2: w = (int)Math.Round(v * ppi); break;
            default: h = (int)Math.Round(v * ppi); break;
        }
        bool widthTyped = which is 0 or 2;
        if (LockAspectBox.IsChecked == true)
        {
            double aspect = (double)cur.Width / cur.Height;
            if (widthTyped) h = (int)Math.Round(w / aspect); else w = (int)Math.Round(h * aspect);
        }
        var next = new RectI(cur.X, cur.Y, Math.Max(1, w), Math.Max(1, h)).ClampedTo(cw, ch);
        Session.Change(rr => rr with { Crop = next });
        SyncFields(except: box);
    }

    private void OnAngleTyped()
    {
        if (!double.TryParse(AngleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) return;
        a = Math.Clamp(a, -PictureGeometry.MaxAngle, PictureGeometry.MaxAngle);
        Session.Change(r => WithStraighten(r, a));
        SyncFields(except: AngleBox);
    }

    /// <summary>A quarter turn, a flip or the way back to the original orientation: the recipe changes (the crop goes along) and the view is fitted to the new canvas.</summary>
    internal void Orient(Func<PictureRecipe, PictureRecipe> change)
    {
        Session.Change(change);
        SyncFields();
        _fitted = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_fitted) FitCanvas(); });
    }

    /// <summary>The recipe turned to <paramref name="degrees"/>. The canvas changes size, so a crop moves with the middle of the canvas and keeps its size.</summary>
    internal static PictureRecipe WithStraighten(PictureRecipe r, double degrees)
    {
        var (ow, oh) = r.CanvasSize;
        var turned = r with { Straighten = degrees };
        var (nw, nh) = turned.CanvasSize;
        if (r.Crop is { } c)
            turned = turned with { Crop = new RectI(c.X + (nw - ow) / 2, c.Y + (nh - oh) / 2, c.Width, c.Height).ClampedTo(nw, nh) };
        return turned;
    }

    /// <summary>The left button went down on the left pane at canvas point <paramref name="p"/>: starts moving or resizing the frame, drawing a new one, or the straighten line.</summary>
    internal void LeftDown(Vec2 p, PointerEventArgs? e = null)
    {
        if (OnTransparencyTab) { TransparencyDown(p, e); return; }
        if (LevelToolButton.IsChecked == true)
        {
            _drag = Drag.Line;
            _dragFrom = p;
            LeftPane.Line = (p, p);
            return;
        }
        var frame = Session.Recipe.EffectiveCrop;
        double radius = 9 / Math.Max(_view.Zoom, 1e-6);
        if (CropFrame.HandleAt(frame, p, radius) is { } handle)
        {
            _drag = Drag.Resize; _dragHandle = handle; _dragStart = frame;
        }
        else if (p.X >= frame.X && p.X <= frame.Right && p.Y >= frame.Y && p.Y <= frame.Bottom)
        {
            _drag = Drag.Move; _dragFrom = p; _dragStart = frame;
        }
        else
        {
            _drag = Drag.Draw; _dragFrom = p; _dragStart = frame;
        }
    }

    internal void LeftMove(Vec2 p)
    {
        if (OnTransparencyTab) { TransparencyMove(p); return; }
        var (cw, ch) = Session.Recipe.CanvasSize;
        switch (_drag)
        {
            case Drag.None:
                LeftPane.Cursor = LevelToolButton.IsChecked == true ? new Cursor(StandardCursorType.Cross) : CursorFor(p);
                return;
            case Drag.Line:
                LeftPane.Line = (_dragFrom, p);
                LeftPane.InvalidateVisual();
                return;
            case Drag.Move:
                SetCrop(CropFrame.Move(_dragStart, p.X - _dragFrom.X, p.Y - _dragFrom.Y, cw, ch));
                return;
            case Drag.Resize:
                SetCrop(CropFrame.Resize(_dragStart, _dragHandle, p, cw, ch, LockAspectBox.IsChecked == true));
                return;
            case Drag.Draw:
                if (Vec2.Distance(p, _dragFrom) * _view.Zoom >= 4) SetCrop(CropFrame.Draw(_dragFrom, p, cw, ch));
                return;
        }
    }

    internal void LeftUp(Vec2 p)
    {
        if (OnTransparencyTab) { TransparencyUp(p); return; }
        if (_drag == Drag.Line)
        {
            var line = LeftPane.Line;
            LeftPane.Line = null;
            LevelToolButton.IsChecked = false;
            if (line is { } l && Vec2.Distance(l.From, l.To) * _view.Zoom >= 6)
            {
                double angle = PictureGeometry.StraightenFromLine(l.From, p, Session.Recipe.Straighten);
                Session.Change(r => WithStraighten(r, Math.Round(angle, 2)));
            }
            LeftPane.InvalidateVisual();
        }
        _drag = Drag.None;
        SyncFields();
    }

    private void SetCrop(RectI crop)
    {
        Session.Change(r => r with { Crop = crop });
        SyncFields();
    }

    private Cursor? CursorFor(Vec2 p)
    {
        var frame = Session.Recipe.EffectiveCrop;
        if (CropFrame.HandleAt(frame, p, 9 / Math.Max(_view.Zoom, 1e-6)) is { } h)
            return new Cursor(h is Handle.Left or Handle.Right ? StandardCursorType.SizeWestEast : h is Handle.Top or Handle.Bottom ? StandardCursorType.SizeNorthSouth
                : h is Handle.TopLeft or Handle.BottomRight ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner);
        return p.X >= frame.X && p.X <= frame.Right && p.Y >= frame.Y && p.Y <= frame.Bottom ? new Cursor(StandardCursorType.SizeAll) : new Cursor(StandardCursorType.Cross);
    }

    // ---------------------------------------------------------------- tools that work on the picture

    internal void AutoTrim()
    {
        var r = Session.Recipe;
        var canvas = Session.CanvasPreview ?? PictureProcessor.RenderParts(Session.Work, r with { Crop = null, Adjust = PictureAdjust.None, Effects = PictureEffects.None }, Session.WorkScale).Canvas;
        int tolerance = int.TryParse(TrimToleranceBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? Math.Clamp(t, 0, 80) : 12;
        var found = PictureProcessor.AutoTrim(canvas, tolerance);
        var (cw, ch) = r.CanvasSize;
        if (found is not { } f)
        {
            CropNote.Text = "No plain border found";
            return;
        }
        double kx = (double)cw / canvas.Width, ky = (double)ch / canvas.Height;
        // from the preview copy to the real canvas: rounded inwards, so no border pixel is kept by the conversion
        int x0 = (int)Math.Ceiling(f.X * kx), y0 = (int)Math.Ceiling(f.Y * ky);
        int x1 = (int)Math.Floor(f.Right * kx), y1 = (int)Math.Floor(f.Bottom * ky);
        Session.Change(rr => rr with { Crop = new RectI(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)).ClampedTo(cw, ch) });
        SyncFields();
    }

    internal void AutoLevels()
    {
        var r = Session.Recipe;
        // From the picture as cropped, before any color adjustment or effect, so the levels do not depend on the other sliders.
        var plain = PictureProcessor.Render(Session.Work, r with { Adjust = PictureAdjust.None, Effects = PictureEffects.None }, Session.WorkScale);
        var levels = PictureProcessor.AutoLevels(plain, r.Adjust);
        Session.Change(rr => rr with { Adjust = rr.Adjust with { LowR = levels.LowR, LowG = levels.LowG, LowB = levels.LowB, HighR = levels.HighR, HighG = levels.HighG, HighB = levels.HighB } });
        SyncFields();
    }

    // ---------------------------------------------------------------- views

    internal void SetMode(ViewMode mode)
    {
        Mode = mode;
        LeftPane.IsVisible = mode == ViewMode.SideBySide;
        Grid.SetColumn(RightPane, mode == ViewMode.SideBySide ? 1 : 0);
        Grid.SetColumnSpan(RightPane, mode == ViewMode.SideBySide ? 1 : 2);
        Hold(false);
        SyncFields();
        ShowPreviews();
        _fitted = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_fitted) FitCanvas(); });
    }

    internal void Hold(bool on)
    {
        if (_holding == on) return;
        _holding = on && Mode == ViewMode.Compare;
        ShowPreviews();
    }

    internal void FitCanvas()
    {
        var (cw, ch) = Session.Recipe.CanvasSize;
        var pane = Mode == ViewMode.SideBySide ? LeftPane : RightPane;
        _view.Fit(new RectI(0, 0, cw, ch), pane.Bounds.Width, pane.Bounds.Height);
        _fitted = pane.Bounds.Width > 8;
    }

    internal void FitResult()
    {
        var pane = Mode == ViewMode.SideBySide ? LeftPane : RightPane;
        _view.Fit(Session.Recipe.EffectiveCrop, pane.Bounds.Width, pane.Bounds.Height);
    }

    private void OnPreviewReady()
    {
        ShowPreviews();
        SyncFields();
    }

    /// <summary>Gives both panes the pictures of the newest preview.</summary>
    private void ShowPreviews()
    {
        if (Session.CanvasPreview is not { } canvas || Session.ResultPreview is not { } result || Session.PreviewRecipe is not { } shown) return;
        var (cw, ch) = shown.CanvasSize;
        double toCanvas = (double)cw / canvas.Width;
        var crop = shown.EffectiveCrop;
        LeftPane.CanvasWidth = RightPane.CanvasWidth = cw;
        LeftPane.CanvasHeight = RightPane.CanvasHeight = ch;
        LeftPane.SetPicture(canvas, toCanvas, 0, 0);
        if (_holding)
        {
            RightPane.SetPicture(canvas, toCanvas, 0, 0);
            RightPane.CheckerArea = null;
            RightPane.Caption = "Original";
        }
        else
        {
            RightPane.SetPicture(result, (double)crop.Width / result.Width, crop.X, crop.Y);
            RightPane.CheckerArea = crop;
            RightPane.Caption = "Result";
        }
        LeftPane.Frame = Session.Recipe.EffectiveCrop;
    }

    // ---------------------------------------------------------------- OK

    internal async Task AcceptAsync()
    {
        if (!OkButton.IsEnabled) return;
        OkButton.IsEnabled = false;
        try
        {
            Committed = await Session.CommitAsync();
        }
        finally
        {
            Close();
        }
    }
}

internal static class PictureEditorDialogExtensions
{
    public static ToggleButton Padded(this ToggleButton b)
    {
        b.Padding = UiScale.Space(10, 3);
        b.Focusable = false;
        return b;
    }

    /// <summary>Keeps a pane showing its newest state (no-op placeholder for symmetry).</summary>
    public static void Visibility(this PicturePane pane) => pane.InvalidateVisual();
}
