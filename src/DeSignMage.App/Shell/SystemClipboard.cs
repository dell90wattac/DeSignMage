using System.Runtime.InteropServices;
using System.Text;

namespace DeSignMage.App.Shell;

/// <summary>What the system clipboard currently offers (a cheap check; nothing is read).</summary>
[Flags]
public enum ClipboardContent
{
    None = 0,
    /// <summary>A picture as PNG (Snipping Tool, browsers, Paint, Photoshop ...).</summary>
    Png = 1,
    /// <summary>A picture as a device-independent bitmap (CF_DIBV5 / CF_DIB), the format every Windows program can offer.</summary>
    Dib = 2,
    /// <summary>A list of files (copied in Explorer).</summary>
    Files = 4,
    /// <summary>Objects copied inside this program (see <see cref="ISystemClipboard.OwnToken"/>).</summary>
    Own = 8,
}

/// <summary>A picture to offer other programs when copying one: PNG, plus a DIBV5 for programs that don't read PNG.</summary>
public sealed record ClipboardPicture(byte[] Png, byte[] DibV5);

/// <summary>
/// The Windows clipboard as the editor needs it. A small interface so tests can use a fake; <see cref="WindowsClipboard"/> is the real one.
/// </summary>
public interface ISystemClipboard
{
    ClipboardContent Available();

    /// <summary>The tag a copy of this program's objects left on the clipboard, or null if the clipboard holds something else.</summary>
    string? OwnToken();

    byte[]? GetPng();

    /// <summary>The picture as a DIB (the V5 form with its alpha channel when there is one), or null.</summary>
    byte[]? GetDib();

    /// <summary>Paths copied in Explorer (empty when the clipboard holds no files).</summary>
    IReadOnlyList<string> GetFiles();

    /// <summary>Replaces the clipboard with this program's objects, tagged <paramref name="token"/>, and optionally a picture for other programs.</summary>
    void SetOwn(string token, ClipboardPicture? picture);
}

/// <summary>The real clipboard, through the Win32 API (formats the cross-platform clipboard API does not give: PNG, DIBV5 with alpha, the file list).</summary>
public sealed class WindowsClipboard : ISystemClipboard
{
    private const uint CF_DIB = 8, CF_HDROP = 15, CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static readonly uint PngFormat = RegisterClipboardFormat("PNG");
    private static readonly uint OwnFormat = RegisterClipboardFormat("DeSignMage.Objects");

    public ClipboardContent Available()
    {
        var c = ClipboardContent.None;
        if (IsClipboardFormatAvailable(PngFormat)) c |= ClipboardContent.Png;
        if (IsClipboardFormatAvailable(CF_DIBV5) || IsClipboardFormatAvailable(CF_DIB)) c |= ClipboardContent.Dib;
        if (IsClipboardFormatAvailable(CF_HDROP)) c |= ClipboardContent.Files;
        if (IsClipboardFormatAvailable(OwnFormat)) c |= ClipboardContent.Own;
        return c;
    }

    public string? OwnToken() => Read(OwnFormat) is { } b ? Encoding.UTF8.GetString(b).TrimEnd('\0') : null;

    public byte[]? GetPng() => Read(PngFormat);

    public byte[]? GetDib() => Read(CF_DIBV5, CF_DIB);

    public IReadOnlyList<string> GetFiles()
    {
        if (Read(CF_HDROP) is not { Length: >= 20 } d) return [];
        // DROPFILES: offset of the list (4), point (8), non-client flag (4), wide-characters flag (4); then the paths, each ended by a 0, the list by a double 0.
        int offset = BitConverter.ToInt32(d, 0);
        bool wide = BitConverter.ToInt32(d, 16) != 0;
        var text = wide ? Encoding.Unicode.GetString(d, offset, d.Length - offset) : Encoding.Default.GetString(d, offset, d.Length - offset);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public void SetOwn(string token, ClipboardPicture? picture)
    {
        if (!Open()) return;
        try
        {
            EmptyClipboard();
            Put(OwnFormat, Encoding.UTF8.GetBytes(token));
            if (picture is null) return;
            Put(PngFormat, picture.Png);
            if (picture.DibV5.Length > 0) Put(CF_DIBV5, picture.DibV5);
        }
        finally { CloseClipboard(); }
    }

    // ---- Win32 plumbing ----

    /// <summary>Another program can hold the clipboard open for a moment; try for about a quarter of a second.</summary>
    private static bool Open()
    {
        for (int i = 0; i < 25; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private static byte[]? Read(params uint[] formats)
    {
        if (!Open()) return null;
        try
        {
            foreach (var f in formats)
            {
                if (!IsClipboardFormatAvailable(f)) continue;
                var handle = GetClipboardData(f);
                if (handle == IntPtr.Zero) continue;
                long size = (long)GlobalSize(handle);
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero || size <= 0 || size > int.MaxValue) continue;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally { GlobalUnlock(handle); }
            }
            return null;
        }
        finally { CloseClipboard(); }
    }

    private static void Put(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) return;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) { GlobalFree(handle); return; }
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == IntPtr.Zero) GlobalFree(handle);   // on success the system owns the memory
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr newOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
}
