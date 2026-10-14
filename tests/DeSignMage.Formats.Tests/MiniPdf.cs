using System.IO.Compression;
using System.Text;

namespace DeSignMage.Formats.Tests;

/// <summary>A few lines of PDF writing for tests: numbered objects, streams, one page tree. Not a PDF library.</summary>
internal sealed class MiniPdf
{
    private readonly List<byte[]> _objects = [];

    /// <summary>Adds an object (its body, without "n 0 obj") and returns its number.</summary>
    public int Add(string body)
    {
        _objects.Add(Encoding.Latin1.GetBytes(body));
        return _objects.Count;
    }

    /// <summary>Reserves a number to be filled with <see cref="Set"/>.</summary>
    public int Reserve() => Add("null");

    public void Set(int number, string body) => _objects[number - 1] = Encoding.Latin1.GetBytes(body);

    public int AddStream(string dictEntries, byte[] data, bool flate = false)
    {
        if (flate)
        {
            using var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(data);
            data = z.ToArray();
            dictEntries += " /Filter /FlateDecode";
        }
        var head = Encoding.Latin1.GetBytes($"<< {dictEntries} /Length {data.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        _objects.Add([.. head, .. data, .. tail]);
        return _objects.Count;
    }

    public int AddStream(string dictEntries, string data, bool flate = false) => AddStream(dictEntries, Encoding.Latin1.GetBytes(data), flate);

    /// <summary>One page. <paramref name="resources"/> is the inside of the resource dictionary.</summary>
    public int AddPage(string content, string resources = "", string mediaBox = "[0 0 200 100]", string extra = "")
    {
        int contents = AddStream("", content);
        return Add($"<< /Type /Page /MediaBox {mediaBox} /Contents {contents} 0 R /Resources << {resources} >> {extra} /Parent PAGES >>");
    }

    public byte[] Build(IEnumerable<int> pages, string catalogExtra = "")
    {
        var pageList = pages.ToList();
        int pagesNo = Add($"<< /Type /Pages /Kids [{string.Join(" ", pageList.Select(p => $"{p} 0 R"))}] /Count {pageList.Count} >>");
        int catalog = Add($"<< /Type /Catalog /Pages {pagesNo} 0 R {catalogExtra} >>");
        for (int i = 0; i < _objects.Count; i++)
        {
            string s = Encoding.Latin1.GetString(_objects[i]);
            if (s.Contains("/Parent PAGES")) _objects[i] = Encoding.Latin1.GetBytes(s.Replace("/Parent PAGES", $"/Parent {pagesNo} 0 R"));
        }
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.5\n");
        var offsets = new List<long>();
        for (int i = 0; i < _objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n");
            ms.Write(_objects[i]);
            W("\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The usual one-page file: a page with this content and these resources.</summary>
    public static byte[] OnePage(string content, Action<MiniPdf, StringBuilder>? resources = null, string mediaBox = "[0 0 200 100]", string pageExtra = "", string catalogExtra = "")
    {
        var pdf = new MiniPdf();
        var res = new StringBuilder();
        resources?.Invoke(pdf, res);
        int page = pdf.AddPage(content, res.ToString(), mediaBox, pageExtra);
        return pdf.Build([page], catalogExtra);
    }
}
