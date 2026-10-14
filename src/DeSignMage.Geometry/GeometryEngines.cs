using DeSignMage.Core.Geometry;

namespace DeSignMage.Geometry;

/// <summary>Ready-made engine instances (stateless and thread-safe), for the app to plug into Core.</summary>
public static class GeometryEngines
{
    public static IBooleanService Booleans { get; } = new ClipperBooleanService();
    public static IOffsetService Offsets { get; } = new ClipperOffsetService();
    public static IOffsetPreviewService OffsetPreview { get; } = new ClipperOffsetPreview();
}
