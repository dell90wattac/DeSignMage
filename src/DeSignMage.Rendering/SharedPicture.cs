using SkiaSharp;

namespace DeSignMage.Rendering;

/// <summary>
/// Reference-counted <see cref="SKPicture"/>. Pictures are recorded on the UI thread and replayed on the
/// render thread, so the cache and each in-flight frame hold a reference; the last <see cref="Release"/> disposes.
/// </summary>
public sealed class SharedPicture
{
    private int _refs = 1;

    public SharedPicture(SKPicture picture) => Picture = picture;

    public SKPicture Picture { get; }

    public SharedPicture AddRef()
    {
        Interlocked.Increment(ref _refs);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Picture.Dispose();
    }
}
