using System.Globalization;

namespace DeSignMage.Core.Text;

/// <summary>
/// A text's letters are its grapheme clusters ("text elements"): what the user sees as one character, with its
/// combining marks. The caret, the letter dots and the per-gap kerning offsets all count in these units.
/// </summary>
public static class TextElements
{
    public static IReadOnlyList<string> Split(string text)
    {
        if (text.Length == 0) return [];
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    public static int Count(string text) => text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>UTF-16 offset of the start of every element, plus the total length at the end (length Count + 1).</summary>
    public static int[] Offsets(string text)
    {
        var offsets = new List<int>();
        if (text.Length > 0)
        {
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) offsets.Add(e.ElementIndex);
        }
        offsets.Add(text.Length);
        return offsets.ToArray();
    }

    /// <summary>Drops trailing zeros so "no kerning" always has one representation (an empty list).</summary>
    public static IReadOnlyList<double> NormalizeKerning(IEnumerable<double> kerning)
    {
        var list = kerning.ToList();
        while (list.Count > 0 && Math.Abs(list[^1]) < 1e-9) list.RemoveAt(list.Count - 1);
        return list.Count == 0 ? [] : list.ToArray();
    }

    /// <summary>
    /// The kerning list after replacing <paramref name="removed"/> letters at <paramref name="start"/> by
    /// <paramref name="inserted"/> new ones: the offsets of removed letters go, the new letters start at 0,
    /// everything else keeps its offset. (Offset g sits in the gap after letter g.)
    /// </summary>
    public static IReadOnlyList<double> RemapKerning(IReadOnlyList<double> kerning, int start, int removed, int inserted)
    {
        var list = kerning.ToList();
        while (list.Count < start + removed) list.Add(0);
        list.RemoveRange(start, removed);
        list.InsertRange(start, new double[inserted]);
        return NormalizeKerning(list);
    }

    /// <summary>
    /// Remaps kerning for an arbitrary text change by finding the changed span (common prefix and suffix of the letters).
    /// A same-length change keeps the offsets (a corrected typo keeps its spacing).
    /// </summary>
    public static IReadOnlyList<double> RemapKerning(string oldText, string newText, IReadOnlyList<double> kerning)
    {
        var a = Split(oldText);
        var b = Split(newText);
        int p = 0;
        while (p < a.Count && p < b.Count && a[p] == b[p]) p++;
        int s = 0;
        while (s < a.Count - p && s < b.Count - p && a[a.Count - 1 - s] == b[b.Count - 1 - s]) s++;
        int removed = a.Count - p - s, inserted = b.Count - p - s;
        if (removed == inserted) return NormalizeKerning(kerning);
        return RemapKerning(kerning, p, removed, inserted);
    }
}
