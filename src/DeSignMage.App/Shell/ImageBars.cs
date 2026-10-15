using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Imaging;

namespace DeSignMage.App.Shell;

/// <summary>The Image menu operations as the menu, the Context Bar and the object menu all run them.</summary>
internal static class ImageActions
{
    /// <summary>The shell shows a failure (an unreadable picture) with this; null in tests that don't care.</summary>
    public static Action<string>? ShowError { get; set; }

    /// <summary>Opens the Resample dialog (the window sets it).</summary>
    public static Action? OpenResample { get; set; }

    /// <summary>Opens the Picture Editor (the window sets it).</summary>
    public static Action? OpenEditor { get; set; }

    public static bool Run(EditorSession session, ImageOp op, string description)
    {
        int done = session.ApplyImageOp(op, description);
        if (done == 0 && session.LastImageError is { } error) ShowError?.Invoke(error);
        return done > 0;
    }

    public static bool RotateClockwise(EditorSession s) => Run(s, new ImageOp.Rotate90(true), "Rotate 90 Degrees Clockwise");
    public static bool RotateCounterClockwise(EditorSession s) => Run(s, new ImageOp.Rotate90(false), "Rotate 90 Degrees Counter-clockwise");
    public static bool FlipHorizontal(EditorSession s) => Run(s, new ImageOp.Flip(true), "Flip Horizontal");
    public static bool FlipVertical(EditorSession s) => Run(s, new ImageOp.Flip(false), "Flip Vertical");
    public static bool Invert(EditorSession s) => Run(s, new ImageOp.Invert(), "Invert");
}

/// <summary>
/// A section of the select tool's Context Bar while a bitmap is selected (reduced to what exists):
/// pixel size and resolution, and buttons for the Image menu operations.
/// </summary>
public sealed class ImageContextRow : BarRow, IRefreshable
{
    private readonly EditorSession _session;
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center };

    public ImageContextRow(EditorSession session)
    {
        _session = session;
        this.Spaced(SpacingProperty, ContextBars.RowSpacing).Spaced(LineSpacingProperty, ContextBars.LineSpacing);
        IsVisible = false;
        Children.Add(_info);
        // Edit Photo is for one picture; Flatten only for pictures that have edits to bake in (R26: hidden, not greyed, otherwise).
        EditButton = AddButton("Edit Photo", "Edit Photo: crop, rotate, adjust colors and add effects (the original is kept)", () => ImageActions.OpenEditor?.Invoke());
        FlattenButton = AddButton("Flatten", "Flatten Picture: make the edits permanent, and drop the original and what was cropped away", () => session.FlattenPictures());
        AddButton("Rotate ↻","Rotate 90 degrees clockwise", () => ImageActions.RotateClockwise(session));
        AddButton("Rotate ↺", "Rotate 90 degrees counter-clockwise", () => ImageActions.RotateCounterClockwise(session));
        AddButton("Flip H", "Flip Horizontal", () => ImageActions.FlipHorizontal(session));
        AddButton("Flip V", "Flip Vertical", () => ImageActions.FlipVertical(session));
        AddButton("Invert", "Invert colors", () => ImageActions.Invert(session));
        AddButton("Crop","Crop Tool", () => session.BeginCrop());
        AddButton("Resample", "Resample: set the pixel size or resolution (the size on the page stays)", () => ImageActions.OpenResample?.Invoke());
        AddButton("1:1", "1:1 Zoom: one picture pixel per screen pixel", () => session.ZoomToImagePixels());
    }

    /// <summary>The Edit Photo button (shown while exactly one picture is selected).</summary>
    internal Button EditButton { get; }

    /// <summary>The Flatten button (shown while a selected picture has edits to bake in).</summary>
    internal Button FlattenButton { get; }

    private Button AddButton(string text, string tip, Action run)
    {
        var button = ContextBars.Action(text, () => { run(); ContextBars.FocusCanvas?.Invoke(); }, tip);
        Children.Add(button);
        return button;
    }

    public void Refresh()
    {
        var images = _session.SelectedImages();
        IsVisible = images.Count > 0;
        if (images.Count == 0) return;
        foreach (var b in Children.OfType<Button>()) b.IsEnabled = _session.ImageService is not null;
        EditButton.IsVisible = _session.CanEditPicture;
        FlattenButton.IsVisible = _session.CanFlatten;
        if (images.Count > 1)
        {
            _info.Text = $"{images.Count} bitmaps";
            return;
        }
        var img = images[0];
        string name = img.Name ?? img.SourceName ?? "Bitmap";
        _info.Text = img.HasPixelSize
            ? FormattableString.Invariant($"{name}: {img.PixelWidth} x {img.PixelHeight} px, {img.EffectiveDpiX:0.#} dpi")
            : name;
    }
}

/// <summary>Crop tool bar: the box size in pixels (typed values apply to the box), the check mark that crops, and cancel.</summary>
public sealed class CropContextBar : ContextBarBase, IRefreshable
{
    private readonly EditorSession _session;
    private readonly CropTool _tool;
    private readonly TextBox _w, _h;
    private readonly Button _apply;
    private readonly TextBlock _hint = ContextBars.Hint("");
    private readonly Control[] _fields;   // the W and H fields: shown only while there is a crop box
    private bool _refreshing;

    public CropContextBar(EditorSession session, CropTool tool)
    {
        _session = session;
        _tool = tool;
        _w = ContextBars.NumberBox(OnTyped);
        _h = ContextBars.NumberBox(OnTyped);
        _apply = ContextBars.Action("✓ Crop", () => { _tool.Apply(); ContextBars.FocusCanvas?.Invoke(); }, "Crop to the box (Enter)");
        var cancel = ContextBars.Action("✕ Cancel", () => { _session.SetTool("select"); ContextBars.FocusCanvas?.Invoke(); }, "Leave without cropping (Esc)");

        Row1.Children.Add(ContextBars.Label("Crop"));
        _fields = [ContextBars.Field("W", _w, "px"), ContextBars.Field("H", _h, "px")];
        foreach (var field in _fields) Row1.Children.Add(field);
        Row1.Children.Add(ContextBars.Separator());
        Row1.Children.Add(_apply);
        Row1.Children.Add(cancel);
        Row2.Children.Add(_hint);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _session.RedrawRequested += OnRedraw;   // the box changes while dragging, which is not a document change
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _session.RedrawRequested -= OnRedraw;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRedraw(object? sender, EventArgs e) => Refresh();

    public void Refresh()
    {
        _refreshing = true;
        var px = _tool.BoxPixels;
        if (!_w.IsFocused) _w.Text = px is { } r ? r.Width.ToString() : "";
        if (!_h.IsFocused) _h.Text = px is { } r2 ? r2.Height.ToString() : "";
        ContextBars.Valid(_apply, px is not null);
        foreach (var field in _fields) field.IsVisible = px is not null;
        _hint.Text = _tool.Hint;   // changes once a box exists
        _refreshing = false;
    }

    private void OnTyped()
    {
        if (_refreshing || _tool.Target is null) return;
        if (!int.TryParse(_w.Text, out int w) || !int.TryParse(_h.Text, out int h) || w < 1 || h < 1) { Refresh(); return; }
        if (_tool.BoxPixels is { } cur && cur.Width == w && cur.Height == h) return;
        _tool.SetBoxPixelSize(w, h);
    }
}
