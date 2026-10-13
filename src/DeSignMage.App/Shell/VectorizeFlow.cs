using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Vectorize;
using DeSignMage.Vectorize.Imaging;

namespace DeSignMage.App.Shell;

/// <summary>
/// Image ▸ Vectorize: vectorizes the selected bitmap, or asks for an image file; shows the options dialog; inserts the
/// traced parts as one group (one undo step).
/// </summary>
internal static class VectorizeFlow
{
    private static readonly string[] ImagePatterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.ico", "*.wbmp"];

    public static async Task RunAsync(Window owner, EditorSession session)
    {
        RasterImage source;
        string name;
        Affine2 placement;
        var selected = session.Selection.Items.Count == 1 ? session.Selection.Items[0] as ImageObject : null;

        try
        {
            if (selected is not null && session.Document.Assets.Get(selected.AssetId) is { } asset)
            {
                source = await BackgroundWork.RunAsync(owner, "Vectorize", "Opening the picture...", _ => SkiaImaging.Decode(asset.Data));
                name = selected.Name ?? "selected bitmap";
                placement = PlacementOver(selected, source);
            }
            else
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Vectorize: choose a picture",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Pictures") { Patterns = ImagePatterns },
                        FilePickerFileTypes.All,
                    ],
                });
                if (files.Count == 0) return;
                await using var stream = await files[0].OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                source = await BackgroundWork.RunAsync(owner, "Vectorize", "Opening the picture...", _ => SkiaImaging.Decode(bytes));
                name = files[0].Name;
                placement = CenteredOnPage(source, session.Document.Page);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DeSignMage.Feedback.AppLog.Warn("vectorize", $"picture could not be opened: {ex.GetType().Name}: {ex.Message}");
            await Message(owner, "Vectorize", "That picture could not be opened.\n" + ex.Message);
            return;
        }

        var dialog = new VectorizeDialog(source, name);
        await dialog.ShowDialog<bool?>(owner);
        if (dialog.Result is not { } result) return;
        if (result.Shapes.Count == 0)
        {
            await Message(owner, "Vectorize", "Nothing was found to trace. Try more colors or a smaller noise filter.");
            return;
        }
        await ApplyAsync(owner, session, result, placement);
    }

    /// <summary>
    /// <see cref="Apply"/> with the path building (every traced shape is transformed onto the page) on a worker thread, which matters for
    /// traces with tens of thousands of parts. The undo step is added on the UI thread.
    /// </summary>
    internal static async Task<GroupObject> ApplyAsync(Window owner, EditorSession session, VectorizeResult result, Affine2 placement)
    {
        var layer = session.Document.ActiveLayer;
        var cmd = await BackgroundWork.RunAsync(owner, "Vectorize", "Building the shapes...",
            _ => new InsertVectorGroupCommand(layer, result, placement, strokeWidthPoints: 0.5));
        session.History.Execute(cmd);
        session.Selection.Set(cmd.Group);
        return cmd.Group;
    }

    /// <summary>Inserts the result as a group on the active layer and selects it.</summary>
    internal static GroupObject Apply(EditorSession session, VectorizeResult result, Affine2 placement)
    {
        // Outline strokes are about a hairline on the page, whatever the picture's size.
        var cmd = new InsertVectorGroupCommand(session.Document.ActiveLayer, result, placement, strokeWidthPoints: 0.5);
        session.History.Execute(cmd);
        session.Selection.Set(cmd.Group);
        return cmd.Group;
    }

    /// <summary>Maps picture pixels onto the frame of a placed bitmap object.</summary>
    internal static Affine2 PlacementOver(ImageObject image, RasterImage pixels) =>
        Affine2.Scale(image.Width / pixels.Width, image.Height / pixels.Height) * image.GetWorldTransform();

    /// <summary>A new picture: 96 dpi size, shrunk to fit 90 % of the page if bigger, centered on it.</summary>
    internal static Affine2 CenteredOnPage(RasterImage pixels, PageSetup page)
    {
        double scale = 72.0 / 96.0;
        double w = pixels.Width * scale, h = pixels.Height * scale;
        double fit = Math.Min(1, Math.Min(0.9 * page.Width / w, 0.9 * page.Height / h));
        scale *= fit;
        w *= fit; h *= fit;
        return Affine2.Scale(scale, scale) * Affine2.Translation(page.X + (page.Width - w) / 2, page.Y + (page.Height - h) / 2);
    }

    private static async Task Message(Window owner, string title, string text)
    {
        var ok = new Button { Content = "OK", IsDefault = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }.ScaledMinWidth(80);
        var panel = new StackPanel { Margin = UiScale.Space(16), Spacing = UiScale.Sp(14) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(ok);
        var w = new Window { Title = title, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = DialogScale.Scrollable(panel) };
        DialogScale.Attach(w, 380);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }
}
