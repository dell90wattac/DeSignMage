using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Imaging;
using DeSignMage.Imaging;
using static DeSignMage.App.Shell.DialogParts;

namespace DeSignMage.App.Shell;

/// <summary>
/// The Transparency tab of the Picture Editor and the Edit in... button. Everything on the tab changes <see cref="PictureRecipe.Transparency"/>, so it stays adjustable:
/// <list type="bullet">
/// <item><b>Remove a color</b>: pick colors in the picture (the original, left), a tolerance slider says how near a color goes; "Remove plain background" picks the
/// color around the edge and removes it from the edge inward only.</item>
/// <item><b>Cut out</b> with the lasso (drag) or the polygon lasso (click the corners): the outline is kept as a vector outline on the original picture, as "keep inside" or "remove inside".</item>
/// <item><b>Edge</b>: shrink, soft edge, and the opacity of the whole picture.</item>
/// </list>
/// </summary>
internal sealed partial class PictureEditorDialog
{
    internal enum TransparencyTool { None, Pick, Lasso, Polygon }

    internal TabItem TransparencyTab { get; } = new() { Header = "Transparency", Padding = UiScale.Space(14, 6), FontWeight = FontWeight.SemiBold };

    internal ToggleButton PickColorButton { get; } = new ToggleButton { Content = "Pick a color in the picture" }.Padded();
    internal Button RemoveBackgroundButton { get; } = Small("Remove plain background");
    internal Button PutColorsBackButton { get; } = Small("Put all colors back");
    internal WrapPanel Swatches { get; } = new() { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
    internal CheckBox EdgeOnlyBox { get; } = new() { Content = "Only where it touches the edge" };
    internal ToggleButton LassoButton { get; } = new ToggleButton { Content = "Lasso" }.Padded();
    internal ToggleButton PolygonButton { get; } = new ToggleButton { Content = "Polygon lasso" }.Padded();
    internal RadioButton KeepInsideRadio { get; } = new() { Content = "Keep inside", GroupName = "cutKind" };
    internal RadioButton RemoveInsideRadio { get; } = new() { Content = "Remove inside", GroupName = "cutKind" };
    internal Button UndoCutButton { get; } = Small("Undo last cut");
    internal Button ClearCutsButton { get; } = Small("Remove all cuts");
    internal TextBlock TransparencyNote { get; } = Note();

    private Control _toleranceRow = null!, _shrinkRow = null!, _softRow = null!;
    private TransparencyTool _tool;
    private readonly List<Vec2> _lasso = [];
    private readonly List<Vec2> _polygon = [];
    private Vec2 _hover;
    private bool _lassoDown;

    internal TransparencyTool Tool => _tool;
    internal bool OnTransparencyTab => ReferenceEquals(Tabs.SelectedItem, TransparencyTab);

    /// <summary>The kind of the next cut: keep what is inside the outline (true) or remove it.</summary>
    internal bool KeepInside => KeepInsideRadio.IsChecked != false;

    /// <summary>The outline being drawn, in canvas pixels (lasso points so far, or the polygon's corners).</summary>
    internal IReadOnlyList<Vec2> OutlineInProgress => _tool == TransparencyTool.Lasso ? _lasso : _polygon;

    private void BuildTransparencyTab()
    {
        var colorColumn = new StackPanel { Spacing = UiScale.Sp(4) };
        var cutColumn = new StackPanel { Spacing = UiScale.Sp(4) };
        var edgeColumn = new StackPanel { Spacing = UiScale.Sp(4) };

        // ---- remove a color
        colorColumn.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = UiScale.Sp(6), LineSpacing = UiScale.Sp(4), Children = { PickColorButton, RemoveBackgroundButton } });
        colorColumn.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = UiScale.Sp(6), LineSpacing = UiScale.Sp(4), Children = { Swatches, PutColorsBackButton } });
        _toleranceRow = AddRow(colorColumn, "Tolerance", 0, 100, PictureTransparency.DefaultTolerance, "0", r => r.Transparency.Tolerance,
            (r, v) => r with { Transparency = r.Transparency with { Tolerance = (int)Math.Round(v) } },
            "How different from a chosen color a pixel may be and still be removed. A white background needs only a little; raise it for a shaded or noisy one.");
        colorColumn.Children.Add(EdgeOnlyBox);

        // ---- cut out
        cutColumn.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = UiScale.Sp(6), LineSpacing = UiScale.Sp(4), Children = { LassoButton, PolygonButton } });
        cutColumn.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = UiScale.Sp(12), LineSpacing = UiScale.Sp(4), Children = { KeepInsideRadio, RemoveInsideRadio } });
        cutColumn.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = UiScale.Sp(6), LineSpacing = UiScale.Sp(4), Children = { UndoCutButton, ClearCutsButton } });
        cutColumn.Children.Add(TransparencyNote);

        // ---- edge and opacity
        _shrinkRow = AddRow(edgeColumn, "Shrink edge", 0, PictureTransparency.MaxShrink, 0, "0", r => r.Transparency.Shrink,
            (r, v) => r with { Transparency = r.Transparency with { Shrink = (int)Math.Round(v) } },
            "Moves the border of the cut-out inward, in pixels of the original: removes a light fringe left from the old background.");
        _softRow = AddRow(edgeColumn, "Soft edge", 0, PictureTransparency.MaxSoftness, 0, "0", r => r.Transparency.Softness,
            (r, v) => r with { Transparency = r.Transparency with { Softness = (int)Math.Round(v) } },
            "Fades the border of the cut-out over this many pixels of the original.");
        AddRow(edgeColumn, "Opacity", 1, 100, 100, "0", r => r.Transparency.Opacity,
            (r, v) => r with { Transparency = r.Transparency with { Opacity = (int)Math.Round(v) } },
            "Makes the whole picture see-through: 100 is solid.");

        var three = new Grid { ColumnSpacing = UiScale.Sp(28), Margin = UiScale.Space(0, 8, 0, 0) };
        for (int i = 0; i < 3; i++) three.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var s1 = Section("Remove a color", colorColumn); var s2 = Section("Cut out", cutColumn); var s3 = Section("Edge", edgeColumn);
        Grid.SetColumn(s1, 0); Grid.SetColumn(s2, 1); Grid.SetColumn(s3, 2);
        three.Children.Add(s1); three.Children.Add(s2); three.Children.Add(s3);
        TransparencyTab.Content = three;

        KeepInsideRadio.IsChecked = true;
        ToolTip.SetTip(PickColorButton, "Click a color in the picture to make it transparent. Click again to pick more colors; the tolerance decides how near a color goes.");
        ToolTip.SetTip(RemoveBackgroundButton, "Finds the plain color around the edge of the picture and removes it, from the edge inward.");
        ToolTip.SetTip(LassoButton, "Drag around what you want with the mouse; the outline closes when you let go.");
        ToolTip.SetTip(PolygonButton, "Click the corners one after the other. Click the first corner, press Enter or double-click to close the outline; Esc starts over.");
        ToolTip.SetTip(EdgeOnlyBox, "Removes the color only where it connects to the edge of the picture, so the same color inside the picture stays.");

        PickColorButton.IsCheckedChanged += (_, _) => { if (!_syncing) SelectTool(PickColorButton.IsChecked == true ? TransparencyTool.Pick : TransparencyTool.None); };
        LassoButton.IsCheckedChanged += (_, _) => { if (!_syncing) SelectTool(LassoButton.IsChecked == true ? TransparencyTool.Lasso : TransparencyTool.None); };
        PolygonButton.IsCheckedChanged += (_, _) => { if (!_syncing) SelectTool(PolygonButton.IsChecked == true ? TransparencyTool.Polygon : TransparencyTool.None); };
        RemoveBackgroundButton.Click += (_, _) => RemovePlainBackground();
        PutColorsBackButton.Click += (_, _) => ChangeTransparency(t => t with { Colors = ValueList<int>.Empty, EdgeOnly = false });
        EdgeOnlyBox.IsCheckedChanged += (_, _) => { if (!_syncing) ChangeTransparency(t => t with { EdgeOnly = EdgeOnlyBox.IsChecked == true }); };
        UndoCutButton.Click += (_, _) => ChangeTransparency(t => t.Cuts.Count == 0 ? t : t with { Cuts = t.Cuts.RemoveAt(t.Cuts.Count - 1) });
        ClearCutsButton.Click += (_, _) => ChangeTransparency(t => t with { Cuts = ValueList<CutShape>.Empty });
    }

    private void ChangeTransparency(Func<PictureTransparency, PictureTransparency> change)
    {
        Session.Change(r => r with { Transparency = change(r.Transparency) });
        SyncFields();
    }

    // ---------------------------------------------------------------- tools

    internal void SelectTool(TransparencyTool tool)
    {
        if (_tool != tool) CancelOutline();
        _tool = tool;
        bool was = _syncing;
        _syncing = true;
        PickColorButton.IsChecked = tool == TransparencyTool.Pick;
        LassoButton.IsChecked = tool == TransparencyTool.Lasso;
        PolygonButton.IsChecked = tool == TransparencyTool.Polygon;
        _syncing = was;
        var cursor = tool == TransparencyTool.None ? null : new Cursor(StandardCursorType.Cross);
        LeftPane.Cursor = cursor;
        RightPane.Cursor = cursor;
        RefreshOutlines();
        UpdateCutNote();
    }

    private void OnTabChanged()
    {
        if (!OnTransparencyTab && _tool != TransparencyTool.None) SelectTool(TransparencyTool.None);
        RefreshOutlines();
    }

    /// <summary>Drops the lasso or the polygon that is being drawn.</summary>
    internal void CancelOutline()
    {
        _lasso.Clear();
        _polygon.Clear();
        _lassoDown = false;
    }

    internal void TransparencyDown(Vec2 p, PointerEventArgs? e = null)
    {
        switch (_tool)
        {
            case TransparencyTool.Pick:
                PickColorAt(p);
                break;
            case TransparencyTool.Lasso:
                _lasso.Clear();
                _lasso.Add(p);
                _lassoDown = true;
                break;
            case TransparencyTool.Polygon:
                bool doubleClick = e is PointerPressedEventArgs { ClickCount: >= 2 };
                if (_polygon.Count >= 3 && (doubleClick || Vec2.Distance(p, _polygon[0]) * _view.Zoom <= 8)) { ClosePolygon(); break; }
                if (doubleClick) break;
                _polygon.Add(p);
                _hover = p;
                break;
        }
        RefreshOutlines();
        UpdateCutNote();
    }

    internal void TransparencyMove(Vec2 p)
    {
        _hover = p;
        if (_tool == TransparencyTool.Lasso && _lassoDown)
        {
            if (Vec2.Distance(p, _lasso[^1]) * _view.Zoom >= 2) _lasso.Add(p);
            RefreshOutlines();
        }
        else if (_tool == TransparencyTool.Polygon && _polygon.Count > 0) RefreshOutlines();
    }

    internal void TransparencyUp(Vec2 p)
    {
        if (_tool != TransparencyTool.Lasso || !_lassoDown) return;
        _lassoDown = false;
        var outline = Simplify(_lasso, 1.2 / Math.Max(_view.Zoom, 1e-6));
        _lasso.Clear();
        if (outline.Count >= 3) AddCut(outline, KeepInside);
        else RefreshOutlines();
        UpdateCutNote();
    }

    internal void ClosePolygon()
    {
        var outline = _polygon.ToList();
        _polygon.Clear();
        if (outline.Count >= 3) AddCut(outline, KeepInside);
        RefreshOutlines();
        UpdateCutNote();
    }

    /// <summary>Adds a cut from an outline given in canvas pixels (what the panes show); it is stored on the original picture so it follows later turns and crops.</summary>
    internal void AddCut(IReadOnlyList<Vec2> canvasOutline, bool keep)
    {
        var recipe = Session.Recipe;
        var shape = new CutShape(new ValueList<Vec2>(canvasOutline.Select(p => PictureGeometry.CanvasToSource(recipe, p))), keep);
        ChangeTransparency(t => t with { Cuts = t.Cuts.Add(shape) });
    }

    /// <summary>Makes the color at a point of the canvas (pixels of the turned original) transparent. Points outside the picture or on clear pixels do nothing.</summary>
    internal void PickColorAt(Vec2 p)
    {
        if (Session.CanvasPreview is not { } canvas) return;
        var (cw, ch) = Session.Recipe.CanvasSize;
        if (p.X < 0 || p.Y < 0 || p.X >= cw || p.Y >= ch) return;
        int x = Math.Min(canvas.Width - 1, (int)(p.X * canvas.Width / cw)), y = Math.Min(canvas.Height - 1, (int)(p.Y * canvas.Height / ch));
        var (r, g, b, a) = canvas.Get(x, y);
        if (a < 16) return;
        int color = PictureTransparency.Pack(r, g, b);
        if (Session.Recipe.Transparency.Colors.Contains(color)) return;
        ChangeTransparency(t => t with { Colors = t.Colors.Add(color) });
    }

    /// <summary>Finds the plain color around the edge of the picture as cropped and removes it from the edge inward. Replaces the colors chosen so far.</summary>
    internal void RemovePlainBackground()
    {
        var r = Session.Recipe;
        var plain = PictureProcessor.Render(Session.Work, r with { Transparency = PictureTransparency.None, Adjust = PictureAdjust.None, Effects = PictureEffects.None }, Session.WorkScale);
        if (TransparencyOps.BackgroundColor(plain) is not { } color)
        {
            TransparencyNote.Text = "No plain background found around the edge. Pick the color in the picture instead.";
            return;
        }
        ChangeTransparency(t => t with { Colors = new ValueList<int>([color]), EdgeOnly = true });
        UpdateCutNote();
    }

    private void UpdateCutNote()
    {
        TransparencyNote.Text = _tool switch
        {
            TransparencyTool.Pick => "Click a color in the picture.",
            TransparencyTool.Lasso => _lassoDown ? "Let go to close the outline." : "Drag around the part you want.",
            TransparencyTool.Polygon => _polygon.Count == 0 ? "Click the first corner." : _polygon.Count < 3 ? "Click the next corner." : "Click the first corner, double-click or press Enter to close.",
            _ => Session.Recipe.Transparency.Cuts.Count == 0 ? "" : $"{Session.Recipe.Transparency.Cuts.Count} cut{(Session.Recipe.Transparency.Cuts.Count == 1 ? "" : "s")}",
        };
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (!OnTransparencyTab) return;
        if (e.Key == Key.Escape && (_lasso.Count > 0 || _polygon.Count > 0 || _tool != TransparencyTool.None))
        {
            if (_lasso.Count > 0 || _polygon.Count > 0) { CancelOutline(); RefreshOutlines(); }
            else SelectTool(TransparencyTool.None);
            UpdateCutNote();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _tool == TransparencyTool.Polygon && _polygon.Count >= 3)
        {
            ClosePolygon();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- what the panes show

    private void SyncTransparency(PictureRecipe r)
    {
        var t = r.Transparency;
        PutColorsBackButton.IsVisible = t.HasColors;
        EdgeOnlyBox.IsVisible = t.HasColors;
        EdgeOnlyBox.IsChecked = t.EdgeOnly;
        _toleranceRow.IsVisible = t.HasColors;
        _shrinkRow.IsVisible = _softRow.IsVisible = t.HasColors || t.HasCuts;
        UndoCutButton.IsVisible = ClearCutsButton.IsVisible = t.Cuts.Count > 0;
        Swatches.IsVisible = t.HasColors;

        Swatches.Children.Clear();
        foreach (int color in t.Colors)
        {
            int captured = color;
            var swatch = new Button
            {
                Background = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)),
                BorderBrush = AppTheme.Brush("Ui.ChromeBorder"), BorderThickness = new Thickness(1), Padding = new Thickness(0), Focusable = false,
            }.ScaledWidth(26).ScaledHeight(22);
            ToolTip.SetTip(swatch, $"#{color:X6} is removed. Click to put it back.");
            swatch.Click += (_, _) => ChangeTransparency(x => x with { Colors = x.Colors.Without(captured) });
            Swatches.Children.Add(swatch);
        }
        if (_tool == TransparencyTool.None) UpdateCutNote();
        RefreshOutlines();
    }

    /// <summary>Gives both panes the cut outlines (placed on the canvas as it is now) and the one being drawn.</summary>
    private void RefreshOutlines()
    {
        var recipe = Session.Recipe;
        var list = new List<PaneOutline>();
        if (OnTransparencyTab)
        {
            foreach (var cut in recipe.Transparency.Cuts.Where(c => c.IsUsable))
                list.Add(new PaneOutline(cut.Points.Select(p => PictureGeometry.SourceToCanvas(recipe, p)).ToList(), Closed: true, cut.Keep, InProgress: false));
            if (_tool == TransparencyTool.Lasso && _lasso.Count > 1) list.Add(new PaneOutline(_lasso.ToList(), Closed: false, KeepInside, InProgress: true));
            else if (_tool == TransparencyTool.Polygon && _polygon.Count > 0) list.Add(new PaneOutline([.. _polygon, _hover], Closed: false, KeepInside, InProgress: true, Corners: _polygon.Count));
        }
        LeftPane.Outlines = list;
        RightPane.Outlines = list;
        LeftPane.InvalidateVisual();
        RightPane.InvalidateVisual();
    }

    /// <summary>The outline with its nearly straight runs thinned out (Douglas-Peucker), <paramref name="tolerance"/> in canvas pixels.</summary>
    internal static List<Vec2> Simplify(IReadOnlyList<Vec2> points, double tolerance)
    {
        if (points.Count < 3) return points.ToList();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            double worst = 0;
            int at = -1;
            for (int i = from + 1; i < to; i++)
            {
                double d = DistanceToSegment(points[i], points[from], points[to]);
                if (d > worst) { worst = d; at = i; }
            }
            if (at >= 0 && worst > tolerance)
            {
                keep[at] = true;
                stack.Push((from, at));
                stack.Push((at, to));
            }
        }
        return points.Where((_, i) => keep[i]).ToList();
    }

    private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Vec2.Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Vec2.Distance(p, new Vec2(a.X + t * dx, a.Y + t * dy));
    }

    // ---------------------------------------------------------------- Edit in...

    internal Button EditInButton { get; } = Small("Edit in...");
    internal TextBlock EditInNote { get; } = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Brush("Ui.Muted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    internal StackPanel EditInHost { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };

    private Action<PictureEditorDialog>? _editInRequested;

    /// <summary>The window starts the other program and watches the file (null: Edit in... is not offered).</summary>
    internal Action<PictureEditorDialog>? EditInRequested
    {
        get => _editInRequested;
        set { _editInRequested = value; EditInButton.IsVisible = value is not null; }
    }

    private void BuildEditIn()
    {
        EditInHost.Children.Add(EditInNote);
        EditInHost.Children.Add(EditInButton);
        EditInButton.IsVisible = false;
        ToolTip.SetTip(EditInButton, "Opens the picture in another program (choose it in Tools > Options > Pictures). When you save it there, it comes back here with your edits kept.");
        EditInButton.Click += (_, _) => _editInRequested?.Invoke(this);
    }

    /// <summary>The other program was started; the editor now waits for it to save.</summary>
    internal void ShowEditInStarted() =>
        EditInNote.Text = $"Waiting for {PictureSettings.EditorName}. Save the picture there and it comes back here.";

    /// <summary>The picture was saved in the other program: it is the new original and every setting stays.</summary>
    internal void ReplaceOriginal(string assetId, PixelBuffer pixels)
    {
        Session.ReplaceSource(pixels, assetId);
        EditInNote.Text = $"Updated from {PictureSettings.EditorName} at {DateTime.Now:t}.";
        SyncFields();
        _fitted = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_fitted) FitCanvas(); });
    }
}
