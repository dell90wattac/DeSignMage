using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeSignMage.App.Shell;
using DeSignMage.Feedback;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Imaging;
using DeSignMage.Core.Model;

namespace DeSignMage.App;

public partial class MainWindow : Window
{
    /// <summary>Menu bar layout. Most entries arrive with later tasks.</summary>
    private static readonly MenuNode[] MenuLayout =
    [
        MenuNode.Menu("_File", "file.new", "file.open", "-", "file.save", "file.saveAs", "-", "file.import", "file.export", "-", "file.exit"),
        MenuNode.Menu("_Edit", "edit.undo", "edit.redo", "-", "edit.cut", "edit.copy", "edit.paste", "edit.delete", "-", "edit.selectAll", "-",
            // Edit > Vector/Node submenu (enabled while the Vector Edit tool has an object open), plus the Loop Edit tool's.
            MenuNode.Menu("Vector/Node", "vector.insert", "vector.toLine", "vector.toArc", "vector.toBezier", "vector.makeSharp", "-",
                "vector.makeCorner", "vector.makeSmooth", "vector.makeSymmetric", "-",
                "vector.breakLoop", "vector.setStart", "vector.join", "vector.delete"),
            MenuNode.Menu("Loops", "loop.reverse", "loop.open", "loop.close", "loop.toCircle", "loop.breakOut", "-", "loop.reorder", "loop.delete")),
        MenuNode.Menu("_View", "view.zoomIn", "view.zoomOut", "view.zoomPage", "view.zoomAll", "view.zoomSelection", "-", "view.fillColors", "view.loopDirection", "view.highlightSmall", "view.jobPalette", "view.rulers", "view.guides", "view.snapGuides", "-",
            MenuNode.Menu("Interface Text _Size", TextSizeMenuIds.Cast<object>().ToArray()), "-", "view.darkMenus", "view.darkCanvas"),
        MenuNode.Menu("_Layout", MenuNode.Menu("_Guides", GuideMenuCommandIds.Cast<object>().ToArray()), MenuNode.Menu("Shapes", "tool.rectangle", "tool.roundedRectangle", "tool.ellipse", "tool.square", "tool.circle", "tool.polygon", "tool.arrow", "-", "shape.convertRounded"), "-", "layout.properties", "layout.sizeToFit"),
        MenuNode.Menu("_Arrange", MenuNode.Menu("Alig_n", AlignMenu.Cast<object>().ToArray()), "-", MenuNode.Menu("Group", "arrange.ungroup", "arrange.group", "arrange.groupAll", "arrange.groupByColor"), "arrange.combine", "arrange.breakApart", "arrange.breakIntoLoops", "arrange.breakIntoOuterLoops", "arrange.closeAllLoops", "arrange.removeInnerLoops", "text.toCurves", "-",
            "arrange.lock", "arrange.unlockAll", "-", "arrange.mirrorH", "arrange.mirrorV", "-",
            MenuNode.Menu("Z Order", "arrange.toTop", "arrange.oneUp", "arrange.oneDown", "arrange.toBottom")),
        MenuNode.Menu("_Text", "tool.text", "text.changeFont", "-", "text.toCurves", "effects.textOnPath"),
        MenuNode.Menu("_Paragraph"),
        // Image menu: what is not built yet is shown disabled.
        MenuNode.Menu("_Image", "image.vectorize", "image.editPicture", "image.flatten", "image.crop", "image.rasterize", "image.resample", "-",
            MenuNode.Menu("Modify Pixels"), "-",
            "image.rotateCW", "image.rotateCCW", "image.flipH", "image.flipV", "image.invert", "image.commitRotation", "image.zoom11", "-",
            MenuNode.Menu("Color Depth"), "image.extractAlpha", "-",
            "image.saveAs", "-",
            "image.fillLibManage", "image.fillLibUpdate", "image.fillLibAdd"),
        MenuNode.Menu("Effe_cts", "effects.outline", "effects.inline", "effects.knife", "effects.weld", "effects.commonWeld", "effects.cookieCutter", "-",
            "effects.shadow", "effects.shadowExtruded", "effects.shadowPerspective", "effects.shadowCast", "-",
            "effects.softShadow", "effects.glow", "effects.feather", "-",
            "effects.gradientBlend", "effects.easyBevel", "-", "effects.textOnPath"),
        MenuNode.Menu("_Tools", "tool.select", "tool.vector", "tool.vectorDraw", "tool.loop", "tools.showTransparency", "-", "tools.options", "-", "debug.stress"),
        MenuNode.Menu("_Help", "help.feedback", "-", "help.about"),
    ];

    /// <summary>Main toolbar layout ("-" = gap).</summary>
    private static readonly string[] ToolbarLayout =
    [
        "file.new", "file.open", "file.save", "-", "edit.undo", "edit.redo", "edit.delete", "-",
        "view.zoomIn", "view.zoomOut", "view.zoomPage", "view.zoomAll", "view.zoomSelection", "-",
        "view.guides", "view.snapGuides",
    ];

    private readonly StartupOptions _options;
    private readonly CommandRegistry _commands = new();
    private readonly List<IRefreshable> _refreshables = [];
    private readonly List<ToolBarButton> _toolbarButtons = [];
    private EditorSession _session = null!;
    private bool _refreshQueued;
    private List<DocObject?> _layerRows = [];
    private bool _syncingLayers;

    private sealed record ToolBarButton(Button Button, AppCommand Command);

    internal EditorSession Session => _session;

    private Shell.ColorUiState? _colorState;
    private Shell.ProjectColorBar _projectColorBar = null!;
    private Shell.ColorMixer _colorMixer = null!;
    internal Shell.ColorUiState ColorState => _colorState!;
    internal Shell.ProjectColorBar ProjectColorBar => _projectColorBar;
    internal Shell.ColorMixer ColorMixer => _colorMixer;

    public MainWindow() : this(new StartupOptions()) { }

    public MainWindow(StartupOptions options)
    {
        _options = options;
        DeSignMage.Text.SkiaFontService.Install();   // installed fonts + HarfBuzz for live text
        Program.Mark("window-ctor");
        InitializeComponent();
        Program.Mark("xaml-loaded");
        if (options.WindowSize is { } size) { WindowState = WindowState.Normal; Width = size.Width; Height = size.Height; }
        Shell.ContextBars.FocusCanvas = () => Surface.Focus();
        RegisterCommands();

        foreach (var node in MenuLayout) MainMenu.Items.Add(MenuNode.Build(node, _commands));
        BuildToolbar();
        InitThemeButtons();
        InitMenuStrip();
        InitFileOpening();
        VectorFiles.Importer ??= new FormatsVectorImporter(() => _session?.BooleanService);   // PDF, AI, EPS and SVG (File > Open, drag and drop)
        InitRecentMenu();
        InitFeedback();
        InitDockPanels();
        InitUiScale();
        Closing += OnWindowClosing;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        LayersList.SelectionChanged += OnLayersListSelectionChanged;
        HistoryList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<HistoryRow>((row, _) =>
            new TextBlock { Text = row?.Text, Foreground = row is { Redo: true } ? AppTheme.Brush("Ui.Muted") : null });   // steps that are only redo-able are muted
        HistoryList.SelectionChanged += OnHistoryListSelectionChanged;
        ContextBarScroll.PointerWheelChanged += (_, e) =>   // a Context Bar row that is longer than the window scrolls sideways with the wheel
        {
            ContextBarScroll.Offset = new Vector(Math.Max(0, ContextBarScroll.Offset.X - e.Delta.Y * 48), 0);
            e.Handled = true;
        };

        var perfTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        perfTimer.Tick += (_, _) => PerfText.Text =
            $"frame {Surface.LastDrawMs:0.0} ms · record {Surface.LastRecordMs:0.0} ms · {_session.Document.ObjectCount} objects";
        perfTimer.Start();

        Program.Mark("ui-built");
        LoadSession(new EditorSession(DemoContent.NewDocument()));
        Program.Mark("session-loaded");
        if (options.Demo) DemoContent.AddSample(_session);
        if (options.Stress > 0) DemoContent.AddStress(_session, options.Stress);
        if (options.Guides) DemoContent.AddGuides(_session);
        if (options.Demo || options.Stress > 0 || options.Guides) _session.History.MarkSaved();

        // A path on the command line (or "Open with"): open it once the window is up, so any message has an owner.
        Task startupLoad = Task.CompletedTask;
        if (options.OpenPath is { } openPath) Opened += (_, _) => startupLoad = LoadFileAsync(openPath);
        Opened += (_, _) => Program.Mark("opened");
        Opened += (_, _) => Surface.Focus();   // letter shortcuts need the canvas focused
        Opened += (_, _) => FontCatalog.Prewarm();   // the font list is built off the UI thread, so the drop-down opens at once (R12)
        if (options.FirstFramePath is { } firstFramePath) ExitAfterFirstFrame(firstFramePath);
        if (options.ScreenshotPath is { } path) Opened += async (_, _) => { await startupLoad; ScheduleScreenshot(path); };   // a file given on the command line loads first
    }

    private void LoadSession(EditorSession session)
    {
        _session = session;
        AttachImageService(session);
        AttachEffects(session);
        Surface.Session = session;
        TopRuler.CanvasRef = LeftRuler.CanvasRef = Surface;
        TopRuler.Session = session;
        LeftRuler.Session = session;

        ToolboxHost.Children.Clear();
        Shell.Toolbox.Populate(ToolboxHost, session);
        // Colors (C.3): the Palette Strip, the Color Mixer and the Project Color Bar share one state (target, slider model, Project Color Bar visibility).
        _colorState = new Shell.ColorUiState(_colorState);
        Shell.PaletteStrip.Populate(PaletteStripHost, session);
        ProjectColorBarHost.Children.Clear();
        ColorMixerHost.Children.Clear();
        _refreshables.Clear();
        _projectColorBar = new Shell.ProjectColorBar(ProjectColorBarHost, session, _colorState);
        _colorMixer = new Shell.ColorMixer(ColorMixerHost, session, _colorState);
        _refreshables.Add(_projectColorBar);
        _refreshables.Add(_colorMixer);
        session.DefaultStyleChanged += (_, _) => QueueRefresh();
        session.GetTool<Core.Editing.Tools.GradientTool>().Changed += (_, _) => QueueRefresh();   // the Color Mixer follows the picked gradient color
        _historyLogger = new HistoryLogger(session.History);

        session.Document.Changed += (_, _) => QueueRefresh();
        session.Selection.Changed += (_, _) => QueueRefresh();
        session.History.Changed += (_, _) => QueueRefresh();
        session.Viewport.Changed += (_, _) => ZoomText.Text = $"{session.Viewport.ZoomPercent:0.#}%";
        session.ToolChanged += (_, _) => OnToolChanged();
        session.GuideStateChanged += (_, _) => ShowContextBar();
        session.ContextMenuRequested += (_, req) => ShowObjectMenu(req);
        session.Beep = Shell.SystemBeep.Play;   // a refused Vector Edit box
        session.DisplayChanged += (_, _) => QueueRefresh();   // toolbar toggle state
        Surface.PointerDocChanged += (_, _) => OnPointerDocChanged();
        Surface.Redrawn += (_, _) =>
        {
            HintText.Text = _flash ?? _session.Hint;
            ShowPagePreviewSize();
        };

        OnToolChanged();
        RefreshPanels();
    }

    // ---- Commands ----

    private void RegisterCommands()
    {
        // Shortcuts are not set here; they come from KeyMap.Default.
        void Add(string id, string header, Action run, string? glyph = null, Func<bool>? can = null, Func<bool>? isChecked = null) =>
            _commands.Add(new AppCommand(id, header, run) { Glyph = glyph, CanExecute = can ?? (() => true), IsChecked = isChecked });
        bool HasSelection() => !_session.Selection.IsEmpty;

        Add("file.new", "_New", () => _ = NewDocumentAsync(), glyph: "\uE8A5");
        Add("file.open", "_Open...", () => _ = OpenFileDialogAsync(), glyph: "");
        Add("file.save", "_Save", () => _ = SaveAsync(), glyph: "\uE74E");
        Add("file.saveAs", "Save _As...", () => _ = SaveAsAsync());
        Add("file.export", "_Export...", () => _ = ExportAsync());
        Add("file.exit", "E_xit", Close);

        Add("edit.undo", "_Undo", () => _session.Undo(), "\uE7A7", () => _session.History.CanUndo);
        Add("edit.redo", "_Redo", () => _session.Redo(), "\uE7A6", () => _session.History.CanRedo);
        // In the Loop Edit tool a selected loop is what gets cut or copied ("Cutting a Loop to the Clipboard"), not the object.
        Add("edit.cut", "Cu_t", () => { if (_session.ActiveTool is LoopEditTool { HasSelection: true } lt) lt.Cut(); else _session.Cut(); }, can: HasSelection);
        Add("edit.copy", "_Copy", () => { if (_session.ActiveTool is LoopEditTool { HasSelection: true } lt) lt.Copy(); else _session.Copy(); }, can: HasSelection);
        Add("edit.paste", "_Paste", () => _ = PasteAsync(), can: CanPasteNow);   // objects copied here, else a picture / files from the system clipboard (MainWindow.Paste.cs)
        Add("edit.delete", "_Delete", () => _session.DeleteSelection(), "\uE74D",
            () => (HasSelection() || _session.SelectedGuide is not null) && _session.ActiveTool is not (VectorEditTool or LoopEditTool));
        Add("edit.selectAll", "Select _All", () => _session.SelectAll());

        Add("view.zoomIn", "Zoom _In", () => _session.Viewport.ZoomAt(_session.Viewport.ViewSize / 2, 1.5), glyph: "\uE8A3");
        Add("view.zoomOut", "Zoom _Out", () => _session.Viewport.ZoomAt(_session.Viewport.ViewSize / 2, 1 / 1.5), glyph: "\uE71F");
        Add("view.zoomPage", "Zoom _Layout", () => _session.ZoomToPage(push: true), glyph: "\uE9A6");
        Add("view.zoomAll", "Zoom _All", () => _session.ZoomToContent(push: true), glyph: "\uE740");
        Add("view.zoomSelection", "Zoom _Object", () => _session.ZoomToSelection(push: true), glyph: "\uE8B3", can: () => !_session.Selection.IsEmpty);
        Add("view.rulers", "Show _Rulers", ToggleRulers, isChecked: () => TopRuler.IsVisible);
        // Ruler guides (requirement R4). No shortcuts until the user chooses them.
        Add("view.guides", "Show _Guidelines", () => _session.ShowGuides = !_session.ShowGuides, "\u2506", isChecked: () => _session.ShowGuides);
        Add("view.snapGuides", "S_nap to Guidelines", () => _session.SnapToGuides = !_session.SnapToGuides, "\u2316", isChecked: () => _session.SnapToGuides);

        Add("tool.select", "_Select Tool", () => _session.SetTool("select"));
        Add("tool.rectangle", "_Rectangle", () => _session.SetTool("rectangle"));
        Add("tool.ellipse", "_Ellipse", () => _session.SetTool("ellipse"));
        // Layout > Shapes: no shortcuts (the user approves keys first).
        Add("tool.roundedRectangle", "Rounded Rectan_gle", () => _session.SetTool("roundedRectangle"));
        Add("tool.square", "_Square", () => _session.SetTool("square"));
        Add("tool.circle", "_Circle", () => _session.SetTool("circle"));
        Add("tool.polygon", "_Polygon / Star", () => _session.SetTool("polygon"));
        Add("tool.arrow", "_Arrow", () => _session.SetTool("arrow"));
        Add("shape.convertRounded", "Convert to Rounded Rectangle", () => _session.BeginConvertToRoundedRectangle(), can: () => _session.CanConvertToRoundedRectangle);
        Add("tool.vector", "_Vector Edit Tool", () => _session.SetTool("vector"));
        Add("tool.vectorDraw", "Vector _Draw Tool", () => _session.SetTool("vectordraw"));   // no shortcut until the user chooses one
        Add("tool.loop", "_Loop Edit Tool", () => _session.SetTool("loop"));   // not bound until the user agrees
        Add("tool.text", "_Text Tool", () => _session.SetTool("text"));   // not bound until the shortcut map is agreed
        Add("text.toCurves", "Convert to _Curves", () => _session.ConvertSelectionToCurves(), can: () => _session.CanConvertToCurves);
        Add("text.changeFont", "Change _Font...", () => _ = ChangeFontAsync(), can: () => _session.CanChangeFont);   // no shortcut until the user chooses one
        // "Show Job Color Palette" (R9). No shortcut.
        Add("view.jobPalette", "Show _Job Color Palette", () => _colorState!.ShowJobPalette = !_colorState.ShowJobPalette, isChecked: () => _colorState?.ShowJobPalette ?? true);
        Add("view.fillColors","Show _Fill Colors", () => _session.ShowFillColors = !_session.ShowFillColors, glyph: "\uE790", isChecked: () => _session.ShowFillColors);

        // Vector menu (double-click a vector in the vector tool); the letter keys act on the vector under the cursor.
        VectorEditTool V() => _session.GetTool<VectorEditTool>();
        bool InVector(Func<VectorEditTool, bool> can) => _session.ActiveTool is VectorEditTool v && v.HasTarget && can(v);
        Add("vector.insert", "_Insert", () => V().Insert(), can: () => InVector(v => v.CanInsert));
        Add("vector.toLine", "To _Line", () => V().ToLine(), can: () => InVector(v => v.CanToLine));
        Add("vector.toArc", "To _Arc", () => V().ToArc(), can: () => InVector(v => v.CanToArc));
        Add("vector.toBezier", "To _Bezier", () => V().ToBezier(), can: () => InVector(v => v.CanToBezier));
        Add("vector.breakLoop", "Break L_oop", () => V().BreakLoop(), can: () => InVector(v => v.CanBreakLoop));
        Add("vector.setStart", "Set Start/_End", () => V().SetStartEnd(), can: () => InVector(v => v.CanSetStartEnd));
        Add("vector.join", "_Join Start/End", () => V().JoinStartEnd(), can: () => InVector(v => v.CanJoinStartEnd));
        Add("vector.delete", "_Delete", () => V().Delete(), can: () => InVector(v => v.CanDelete));
        // Node commands from the Context Bar (Toggle Corner, Sharp) and the smooth/symmetric kinds; no shortcuts.
        Add("vector.makeSharp", "_Sharp Corner", () => V().MakeSharp(), can: () => InVector(v => v.CanMakeSharp));
        Add("vector.makeCorner", "Make _Corner Node", () => V().MakeCorner(), can: () => InVector(v => v.CanMakeCorner));
        Add("vector.makeSmooth", "Make S_mooth Node", () => V().MakeSmooth(), can: () => InVector(v => v.CanMakeSmooth));
        Add("vector.makeSymmetric", "Make S_ymmetric Node", () => V().MakeSymmetric(), can: () => InVector(v => v.CanMakeSmooth));

        // Loop Edit tool: the loop menu (double-click a loop) and the Edit > Loops menu.
        LoopEditTool Lp() => _session.GetTool<LoopEditTool>();
        bool InLoop(Func<LoopEditTool, bool> can) => _session.ActiveTool is LoopEditTool l && l.HasTarget && can(l);
        Add("loop.reverse", "_Reverse Dir.", () => Lp().ReverseDirection(), can: () => InLoop(l => l.CanReverse));
        Add("loop.open", "_Open Loop", () => Lp().OpenLoop(), can: () => InLoop(l => l.CanOpen));
        Add("loop.close", "_Close Loop", () => Lp().CloseLoop(), can: () => InLoop(l => l.CanClose));
        Add("loop.toCircle", "To C_ircle", () => Lp().ToCircle(), can: () => InLoop(l => l.CanToCircle));
        Add("loop.delete", "_Delete Loop", () => Lp().Delete(), can: () => InLoop(l => l.CanDelete));
        Add("loop.breakOut", "_Break Out", () => Lp().BreakOut(), can: () => InLoop(l => l.CanBreakOut));
        Add("loop.reorder", "Re_order Loops", () => Lp().ReorderLoops(), can: () => InLoop(l => l.CanReorder));
        Add("view.loopDirection", "Show Loop _Direction", () => Lp().ShowDirection = !Lp().ShowDirection, isChecked: () => Lp().ShowDirection);
        // A red square at the end of every vector shorter than the small-vector threshold (Tools > Options > Vector editing); remembered.
        Add("view.highlightSmall", "Highlight Small _Vectors", () => { Shell.VectorEditSettings.SetHighlightSmallVectors(!VectorEditOptions.HighlightSmallVectors); _session.RequestRedraw(); }, isChecked: () => VectorEditOptions.HighlightSmallVectors);

        Add("arrange.toTop", "To _Top", () => Reorder(int.MaxValue, absolute: true), can: HasSelection);
        Add("arrange.oneUp", "One _Up", () => Reorder(1, absolute: false), can: HasSelection);
        Add("arrange.oneDown", "One _Down", () => Reorder(-1, absolute: false), can: HasSelection);
        Add("arrange.toBottom", "To _Bottom", () => Reorder(0, absolute: true), can: HasSelection);
        Add("arrange.group", "_Make Group", () => _session.GroupSelection(), can: () => _session.CanGroup);
        Add("arrange.ungroup", "_Ungroup", () => _session.UngroupSelection(), can: () => _session.CanUngroup);
        Add("arrange.groupAll", "Group _All", () => _session.GroupAll(), can: () => _session.CanGroupAll);
        Add("arrange.groupByColor", "Group By _Color", () => _session.GroupByColor(), can: () => _session.CanGroupByColor);
        Add("arrange.combine", "_Combine", () => _session.CombineSelection(), can: () => _session.CanCombine);
        Add("arrange.breakApart", "_Break Apart", () => _session.BreakApartSelection(), can: () => _session.CanBreakApart);
        // No shortcut on purpose: the user approves shortcuts before they are wired (see KeyMap).
        Add("arrange.breakIntoLoops", "Break Into _Loops", () => _session.BreakIntoLoopsSelection(), can: () => _session.CanBreakIntoLoops);
        Add("arrange.breakIntoOuterLoops", "Break Into _Outer Loops", () => _session.BreakIntoOuterLoopsSelection(), can: () => _session.CanBreakIntoOuterLoops);
        Add("arrange.closeAllLoops", "Close All Loop_s", () => _session.CloseAllLoops(), can: () => _session.CanCloseAllLoops);
        Add("arrange.removeInnerLoops", "Remove All _Inner Loops", () => _session.RemoveAllInnerLoops(), can: () => _session.CanRemoveInnerLoops);

        Add("debug.stress", "Debug: Add 5,000 Test Shapes", () => DemoContent.AddStress(_session, 5000, Environment.TickCount));
        Add("image.vectorize", "_Vectorize...", () => _ = Shell.VectorizeFlow.RunAsync(this, _session));   // not bound until the shortcut map is agreed
        Add("help.feedback", "Send _Feedback...", () => _ = ShowFeedbackAsync());
        Add("help.about", "_About", ShowAbout);

        RegisterImageCommands();   // file.import and the Image menu (MainWindow.Images.cs)
        RegisterEffectCommands();   // the Effects menu (MainWindow.Effects.cs)
        RegisterArrangeCommands();   // Arrange > Align, Mirror, Locked (MainWindow.Arrange.cs)
        RegisterOptionsCommands();   // Tools > Options, View > Interface Text Size (MainWindow.Options.cs)
        RegisterThemeCommands();   // View > Dark Menus / Dark Canvas (MainWindow.Theme.cs)
        RegisterTransparencyCommand();   // Tools > Show Transparency (MainWindow.Theme.cs)
        RegisterLayoutCommands();   // Layout > Guides, Layout Properties, Size to Fit (MainWindow.Layout.cs)

        KeyMap.Apply(_commands, KeyMap.Default);
    }

    private void BuildToolbar()
    {
        foreach (var id in ToolbarLayout)
        {
            if (id == "-")
            {
                MainToolbar.Children.Add(ContextBars.Separator());
                continue;
            }
            var c = _commands.Get(id);
            var glyph = new TextBlock { Text = c.Glyph ?? "?" };
            glyph.Classes.Add("glyph");
            var b = new Button { Content = glyph, Focusable = false };   // keep keyboard focus on the canvas
            b.Classes.Add("tb");
            ToolTip.SetTip(b, c.ToolTip);
            b.Click += (_, _) => { if (c.CanExecute()) c.Execute(); };
            MainToolbar.Children.Add(b);
            _toolbarButtons.Add(new ToolBarButton(b, c));
        }
    }

    private void ToggleRulers()
    {
        bool show = !TopRuler.IsVisible;
        TopRuler.IsVisible = LeftRuler.IsVisible = show;
        if (TopRuler.Parent is Grid g)
        {
            g.RowDefinitions[0].Height = new GridLength(show ? 20 : 0);
            g.ColumnDefinitions[0].Width = new GridLength(show ? 20 : 0);
        }
    }

    private void Reorder(int value, bool absolute)
    {
        // R22: the order of drawing is separate from grouping; EditorSession.ChangeOrder / StackOrder say what each command does to a group.
        bool up = value > 0;
        _session.ChangeOrder(absolute ? (up ? StackMove.ToFront : StackMove.ToBack) : (up ? StackMove.Forward : StackMove.Backward));
    }

    private void ShowAbout() => new Updates.AboutDialog().ShowDialog(this);

    // ---- Object context menu (left double-click, R1; "Part/Group Editing Tools" popups) ----

    private static readonly string[] GroupMenu = ["arrange.ungroup", "arrange.combine", "arrange.breakApart", "text.toCurves", "-", "edit.cut", "edit.copy", "edit.delete", "-", "arrange.toTop", "arrange.toBottom"];
    private static readonly string[] CombinedMenu = ["arrange.breakApart", "-", "edit.cut", "edit.copy", "edit.delete", "-", "arrange.toTop", "arrange.toBottom"];
    private static readonly string[] ImageMenu = ["image.editPicture", "image.flatten", "image.rotateCW", "image.rotateCCW", "image.flipH", "image.flipV", "image.invert", "-", "image.crop", "image.resample", "image.zoom11", "image.vectorize", "-", "edit.cut", "edit.copy", "edit.delete", "-", "arrange.toTop", "arrange.toBottom"];
    private static readonly string[] PartMenu = ["edit.cut", "edit.copy", "edit.delete", "-", "arrange.toTop", "arrange.toBottom"];
    /// <summary>Vector popup (double-click a vector in the vector tool).</summary>
    private static readonly string[] VectorMenu = ["vector.insert", "vector.toLine", "vector.toArc", "vector.toBezier", "vector.breakLoop", "vector.setStart", "vector.join", "-", "vector.delete"];
    /// <summary>Loop Editing menu (double-click a loop in the Loop Edit tool): Reverse Dir., Open Loop, Close Loop, To Circle, Cut, Copy, Delete; plus Break Out.</summary>
    private static readonly string[] LoopMenu = ["loop.reverse", "loop.open", "loop.close", "loop.toCircle", "-", "edit.cut", "edit.copy", "loop.delete", "-", "loop.breakOut"];

    /// <summary>The menu shown by the last double-click (for tests).</summary>
    internal ContextMenu? LastObjectMenu { get; private set; }

    private void ShowObjectMenu(ContextMenuRequest req)
    {
        var menu = new ContextMenu { Placement = Avalonia.Controls.PlacementMode.Pointer };
        var ids = req.Kind switch
        {
            MenuKind.Vector => VectorMenu,
            MenuKind.Loop => LoopMenu,
            _ => req.Target switch { GroupObject => GroupMenu, CombinedObject => CombinedMenu, ImageObject => ImageMenu, _ => PartMenu },
        };
        foreach (var id in ids)
        {
            var item = MenuNode.Build(MenuNode.Cmd(id), _commands);
            if (item is MenuItem { Tag: AppCommand c } mi) mi.IsEnabled = c.CanExecute();
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            Surface.Focus();
            // After any item's click has run, keys act on the vector under the cursor again.
            if (req.Kind == MenuKind.Vector)
                Dispatcher.UIThread.Post(() => _session.GetTool<VectorEditTool>().MenuClosed(), DispatcherPriority.Background);
            else if (req.Kind == MenuKind.Loop)
                Dispatcher.UIThread.Post(() => _session.GetTool<LoopEditTool>().MenuClosed(), DispatcherPriority.Background);
        };
        LastObjectMenu = menu;
        menu.Open(Surface);
    }

    // ---- Keyboard ----

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl on its own changes what the select tool's top and bottom middle handles do (R21): let a hover or a drag follow it.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            _session.ModifiersChanged(DeSignMage.App.Canvas.DesignCanvas.ToModifiers(e.KeyModifiers) | ModifierKeys.Control);

        // Let text fields keep their keys (except Escape, which returns focus to the canvas).
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            if (e.Key == Key.Escape) { Surface.Focus(); e.Handled = true; }
            return;
        }

        // Text edit mode (R6): plain keys belong to the text. Caret/edit keys go to the text tool; letters, digits and Space are
        // left alone so they arrive as text input and never trigger a shortcut (that includes the plain X / C / V for Cut, Copy and Paste). Ctrl/Alt shortcuts (undo, ...) still work.
        if (_session.IsTextEditing)
        {
            bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;
            if (!command)
            {
                var editKey = e.Key switch
                {
                    Key.Escape => EditorKey.Escape, Key.Enter => EditorKey.Enter, Key.Back => EditorKey.Back, Key.Delete => EditorKey.Delete,
                    Key.Left => EditorKey.Left, Key.Right => EditorKey.Right, Key.Up => EditorKey.Up, Key.Down => EditorKey.Down,
                    Key.Home => EditorKey.Home, Key.End => EditorKey.End, _ => EditorKey.Other,
                };
                if (editKey != EditorKey.Other && _session.KeyDown(editKey, DeSignMage.App.Canvas.DesignCanvas.ToModifiers(e.KeyModifiers))) e.Handled = true;
                return;
            }
            if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control) { _session.TextSelectAll(); e.Handled = true; return; }
            if (e.Key is Key.C or Key.X or Key.V && e.KeyModifiers == KeyModifiers.Control) { e.Handled = true; return; }   // no object cut/paste mid-edit
        }

        if (e.Key == Key.Space)
        {
            if (!_session.SpaceHeld) _session.SpaceHeld = true;
            e.Handled = true;
            return;
        }

        var key = e.Key switch
        {
            Key.Escape => EditorKey.Escape,
            Key.Delete => EditorKey.Delete,
            Key.Back => EditorKey.Back,
            Key.Enter => EditorKey.Enter,
            Key.Left => EditorKey.Left,
            Key.Right => EditorKey.Right,
            Key.Up => EditorKey.Up,
            Key.Down => EditorKey.Down,
            Key.Tab => EditorKey.Tab,   // the Gradient Blend tool cycles its colors with it; elsewhere nothing takes it and focus moves on
            _ => EditorKey.Other,
        };
        // Tool keys first (Escape cancels a drag, arrows nudge), then global shortcuts.
        if (key != EditorKey.Other && key != EditorKey.Delete && _session.KeyDown(key, DeSignMage.App.Canvas.DesignCanvas.ToModifiers(e.KeyModifiers)))
        {
            e.Handled = true;
            return;
        }
        // Plain letter shortcuts (U = Ungroup, ...) only while the canvas has the keyboard focus (R3).
        var focused = FocusManager?.GetFocusedElement();
        if (_commands.TryHandle(e, canvasFocused: focused is null || ReferenceEquals(focused, Surface)))
        {
            e.Handled = true;
            return;
        }
        if (key == EditorKey.Back && _session.KeyDown(key, DeSignMage.App.Canvas.DesignCanvas.ToModifiers(e.KeyModifiers))) e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            _session.ModifiersChanged(DeSignMage.App.Canvas.DesignCanvas.ToModifiers(e.KeyModifiers) & ~ModifierKeys.Control);
        if (e.Key == Key.Space && _session.SpaceHeld)
        {
            _session.SpaceHeld = false;
            e.Handled = true;
        }
    }

    // ---- Panels ----

    private void OnToolChanged()
    {
        AppLog.Info("tool", $"active tool: {_session.ActiveTool.Id}");
        ContextBarHost.Content = null;
        ShowContextBar();
        MenuStrip.Refresh();   // the Tools strip shows the active tool pressed, like the Toolbox
    }

    /// <summary>The Context Bar is the active tool's, or the guide bar while a ruler guide is selected.</summary>
    private void ShowContextBar()
    {
        if (_session.SelectedGuide is not null)
        {
            if (ContextBarHost.Content is not GuideContextBar) ContextBarHost.Content = new GuideContextBar(_session);
        }
        else if (ContextBarHost.Content is null or GuideContextBar)
        {
            ContextBarHost.Content = ContextBars.Create(_session.ActiveTool, _session);
        }
        HintText.Text = _session.Hint;
        QueueRefresh();
    }

    private void OnPointerDocChanged()
    {
        var p = Surface.PointerDoc;
        TopRuler.SetCursor(p);
        LeftRuler.SetCursor(p);
        if (p is { } d)
        {
            var (x, y) = Units.ToUser(d, _session.Document.Page);
            CursorText.Text = string.Create(CultureInfo.InvariantCulture, $"X: {x:0.000}  Y: {y:0.000} in");
        }
        else CursorText.Text = "";
    }

    private bool _infoShowsPagePreview;

    /// <summary>While the page's border is dragged (R20) the Info Line shows the page's size; afterwards it goes back to its normal text.</summary>
    private void ShowPagePreviewSize()
    {
        if (_session.PagePreview is { } page)
        {
            InfoLine.Text = PageBorder.SizeText(page);
            _infoShowsPagePreview = true;
        }
        else if (_infoShowsPagePreview)
        {
            _infoShowsPagePreview = false;
            QueueRefresh();
        }
    }

    /// <summary>Coalesces many change events into one panel refresh per UI loop turn.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshPanels();
        }, DispatcherPriority.Background);
    }

    private void RefreshPanels()
    {
        var doc = _session.Document;
        foreach (var r in _refreshables) r.Refresh();
        MenuStrip.Refresh();
        if (ContextBarHost.Content is IRefreshable bar) bar.Refresh();
        foreach (var tb in _toolbarButtons)
        {
            tb.Button.IsEnabled = tb.Command.CanExecute();
            if (tb.Command.IsChecked is { } isOn) tb.Button.Classes.Set("on", isOn());
        }

        var b = _session.Selection.GetBounds();
        int groups = doc.GroupCount;
        long bitmapBytes = BitmapStats.TotalBytes(doc);
        // A single selected picture: its pixels and resolution, as plain information.
        string pictureInfo = _session.Selection.Items is [ImageObject picture] && ImageSizing.InfoText(picture) is { } text ? "    " + text : "";
        // While a path tool has an object open, the info line describes that graphic (R8 item 7).
        InfoLine.Text = _session.ActiveTool is PathEditToolBase { HasTarget: true } pathTool
            ? pathTool.InfoLine()
            : string.Create(CultureInfo.InvariantCulture,
            $"{(b.IsEmpty ? "" : $"{Units.ToInches(b.Width):0.0000} x {Units.ToInches(b.Height):0.0000}    ")}" +
            $"{doc.ObjectCount - groups} Objects    {groups} Groups    Bitmaps: {bitmapBytes / 1048576.0:0.00} MB total{pictureInfo}");
        ZoomText.Text = $"{_session.Viewport.ZoomPercent:0.#}%";
        Title = $"{_documentTitle}{(_session.History.IsModified ? " *" : "")} - DeSign Mage";

        RefreshLayers();
        RefreshHistory();
    }

    // ---- History tab: every step, the current one highlighted; a click takes the document to that step ----

    private sealed record HistoryRow(string Text, bool Redo);

    private bool _syncingHistory;

    private void RefreshHistory()
    {
        var h = _session.History;
        var rows = new List<HistoryRow>(h.UndoList.Count + 1) { new("(start)", false) };
        rows.AddRange(h.UndoList.Select(c => new HistoryRow(c.Description, false)));
        rows.AddRange(h.RedoList.Select(c => new HistoryRow(c.Description, true)));   // later rows: still redo-able
        _syncingHistory = true;
        HistoryList.ItemsSource = rows;
        HistoryList.SelectedIndex = h.UndoList.Count;
        _syncingHistory = false;
    }

    private void OnHistoryListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingHistory) return;
        int row = HistoryList.SelectedIndex;
        if (row < 0 || row == _session.History.UndoList.Count) return;
        if (!_session.JumpToHistory(row)) RefreshHistory();   // not possible right now: put the highlight back
    }

    private void RefreshLayers()
    {
        // Flat list for now (a tree view comes with the Layers panel task): each layer, then its objects top to bottom in DRAWING order, as
        // they are painted. Grouping is only a note on the row ("in Group 12"), because a group's members need not be next to each other in the
        // drawing order (R22); a row is never moved to sit with its group. Clicking a row selects the whole group, like a click on the canvas.
        // (R22, open: how the panel should show groups whose members are apart is for the user to decide.)
        var rows = new List<DocObject?>();
        var text = new List<string>();
        for (int li = _session.Document.Layers.Count - 1; li >= 0; li--)
        {
            var layer = _session.Document.Layers[li];
            rows.Add(null);
            text.Add((layer.Visible ? "● " : "○ ") + layer.Name);
            for (int i = layer.Children.Count - 1; i >= 0; i--)
            {
                var o = layer.Children[i];
                rows.Add(o);
                text.Add("    " + o.KindName + GroupNote(o) + (o.TopLevel.Locked ? "   · Locked" : ""));   // a locked object (or a member of a locked group) is marked
            }
        }

        static string GroupNote(DocObject o)
        {
            if (o.Parent is not GroupObject) return "";
            var chain = new List<string>();
            for (var g = o.Parent as GroupObject; g is not null; g = g.Parent as GroupObject)
                chain.Insert(0, string.IsNullOrEmpty(g.Name) ? $"Group {g.Id.Value}" : g.Name);
            return "   · in " + string.Join(" > ", chain);
        }

        _syncingLayers = true;
        _layerRows = rows;
        LayersList.ItemsSource = text;
        // A selected group highlights its topmost row.
        LayersList.SelectedIndex = _session.Selection.Count == 1 ? rows.FindIndex(r => r is not null && ReferenceEquals(r.TopLevel, _session.Selection.Items[0])) : -1;
        _syncingLayers = false;
    }

    private void OnLayersListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingLayers) return;
        int i = LayersList.SelectedIndex;
        if (i >= 0 && i < _layerRows.Count && _layerRows[i] is { } o) _session.Selection.Set(o.TopLevel);
    }

    // ---- Screenshot (development) ----

    /// <summary>
    /// Startup benchmark (<c>--first-frame-exit PATH</c>): when the first frame of the design has been drawn, writes the milliseconds since
    /// the process started (and since <c>Main</c>) to <paramref name="path"/> and closes the window.
    /// </summary>
    private void ExitAfterFirstFrame(string path)
    {
        void OnFirstFrame()
        {
            double sinceProcess = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            double sinceMain = Environment.TickCount64 - Program.MainStartTicks;
            try { File.WriteAllText(path, FormattableString.Invariant($"{sinceProcess:0} {sinceMain:0} {Program.MarksText()}")); }
            catch (IOException) { }
            Dispatcher.UIThread.Post(() => { _closeConfirmed = true; Close(); });
        }
        Canvas.SkiaDrawOperation.FirstFrameDrawn += OnFirstFrame;
        Closed += (_, _) => Canvas.SkiaDrawOperation.FirstFrameDrawn -= OnFirstFrame;
    }

    private void ScheduleScreenshot(string path)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                double scale = RenderScaling;
                var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
                using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                rtb.Render(this);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#pragma warning disable CS0618 // replacement overload's options type not needed for a plain PNG
                rtb.Save(path);
#pragma warning restore CS0618
            }
            finally
            {
                _closeConfirmed = true;   // a development screenshot never asks about unsaved changes
                Close();
            }
        };
        timer.Start();
    }
}



