using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Lyt;

internal sealed partial class LytWriteSession
{
    private int _picturesStraightened;

    /// <summary>
    /// A picture is a rectangle with an embedded picture fill. The rectangle's corners are in inches of the picture's own (stretched and
    /// turned) frame, so the record's rotation carries the turn, as when the legacy program rotates a bitmap.
    /// </summary>
    private void AddPicture(ImageObject image, Affine2 world, int group, uint layerId, string name)
    {
        var asset = doc.Assets.Get(image.AssetId);
        var bytes = asset is null ? null : StoredPicture(asset);
        if (bytes is null || image.Width <= 0 || image.Height <= 0)
        {
            result.SkippedCount++;
            if (asset is not null) result.Warnings.Add($"A picture ({asset.MediaType}) could not be stored: only JPEG and PNG pictures are written as they are, and no re-encoder was given.");
            return;
        }

        double w = image.Width, h = image.Height;
        double a, b, rotation;
        Vec2 bottomLeft;
        if (Decompose(world, out a, out b, out rotation))
        {
            bottomLeft = world.Transform(new Vec2(0, h));
        }
        else
        {
            // Skewed or mirrored: the picture is written upright in the bounding box of where it lies.
            _picturesStraightened++;
            var box = new Rect2(0, 0, w, h).Transform(world);
            a = box.Width / w; b = box.Height / h; rotation = 0;
            bottomLeft = new Vec2(box.Left, box.Bottom);
        }
        double nodeW = w * a / Units.PointsPerInch, nodeH = h * b / Units.PointsPerInch;
        if (!(nodeW > 1e-9 && nodeH > 1e-9) || !double.IsFinite(nodeW + nodeH + rotation)) { result.SkippedCount++; return; }

        var spec = WithOpacityAndFeather(new FillSpec { Type = 4, TransType = 0, Image = bytes }, 1, image.Style.Feather)!;
        var part = new PartRecord
        {
            Kind = PartKind.Picture, Name = LytEncoding.Sanitize(name.Length > 0 ? name : "Bitmap"), Scale = 1, XScale = 1, Rotation = rotation,
            SizeW = nodeW, SizeH = nodeH, ActualLeft = 0, ActualTop = nodeH, ActualRight = nodeW, ActualBottom = 0,
            Type = 1, Type2 = 0x2000, LayerId = layerId, Fill = spec,
            Color = new LytColor("0,0,0", "", LytColor.ModelRgb, 0, 0, 0, 0),
        };
        (part.OrgX, part.OrgY) = Units.ToUser(bottomLeft, Page);
        part.Nodes.Add(OutNode.Point(LytNode.FlagMove | LytNode.FlagOuter, 0, 0));
        part.Nodes.Add(OutNode.Point(0x2 | LytNode.FlagOuter, 0, (float)nodeH));
        part.Nodes.Add(OutNode.Point(0x2 | LytNode.FlagOuter, (float)nodeW, (float)nodeH));
        part.Nodes.Add(OutNode.Point(0x2 | LytNode.FlagOuter, (float)nodeW, 0));
        part.Nodes.Add(OutNode.Point(0x2 | LytNode.FlagOuter | LytNode.FlagClose, 0, 0));
        SetPageBounds(part, image.GetWorldBounds());
        Add(part, group);
        result.PictureCount++;
    }

    /// <summary>The picture as a JPEG or PNG file: its own bytes when it is one, else what the encoder in the options makes of it.</summary>
    private byte[]? StoredPicture(Asset asset)
    {
        if (IsJpeg(asset.Data) || IsPng(asset.Data)) return asset.Data;
        var encoded = options.ImageEncoder?.Invoke(asset);
        return encoded is not null && (IsJpeg(encoded) || IsPng(encoded)) ? encoded : null;
    }

    private static bool IsJpeg(byte[] d) => d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8;

    private static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G';
}
