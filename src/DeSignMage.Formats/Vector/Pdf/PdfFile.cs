using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>A PDF document seen as objects: lookup of indirect references, typed access to dictionary entries and stream decoding.</summary>
internal sealed class PdfFile(PdfDocument document) : IDisposable
{
    private const int MaxReferenceDepth = 32;
    private readonly IPdfTokenScanner _scanner = document.Structure.TokenScanner;

    public PdfDocument Document { get; } = document;

    public void Dispose() => Document.Dispose();

    // ---------------------------------------------------------------- objects

    /// <summary>Follows indirect references until a direct object (or null).</summary>
    public IToken? Resolve(IToken? token)
    {
        for (int i = 0; i < MaxReferenceDepth && token is IndirectReferenceToken r; i++)
        {
            token = _scanner.Get(r.Data)?.Data;
        }
        return token is IndirectReferenceToken or NullToken ? null : token;
    }

    public DictionaryToken? Dict(IToken? token) => Resolve(token) switch
    {
        DictionaryToken d => d,
        StreamToken s => s.StreamDictionary,
        _ => null,
    };

    public StreamToken? Stream(IToken? token) => Resolve(token) as StreamToken;

    public IToken? Get(DictionaryToken? dict, string key) =>
        dict is not null && dict.Data.TryGetValue(key, out var v) ? Resolve(v) : null;

    /// <summary>The entry without following a reference (to read the reference itself, e.g. to identify an object).</summary>
    public IToken? GetRaw(DictionaryToken? dict, string key) =>
        dict is not null && dict.Data.TryGetValue(key, out var v) ? v : null;

    public DictionaryToken? GetDict(DictionaryToken? dict, string key) => Dict(Get(dict, key));

    public double? Number(IToken? token) => Resolve(token) switch
    {
        NumericToken n => n.Data,
        _ => null,
    };

    public double GetNumber(DictionaryToken? dict, string key, double fallback) => Number(Get(dict, key)) ?? fallback;

    public string? Name(IToken? token) => Resolve(token) is NameToken n ? n.Data : null;

    public string? GetName(DictionaryToken? dict, string key) => Name(Get(dict, key));

    public bool GetBool(DictionaryToken? dict, string key, bool fallback) => Get(dict, key) is BooleanToken b ? b.Data : fallback;

    public ArrayToken? Array(IToken? token) => Resolve(token) as ArrayToken;

    /// <summary>The numbers of an array entry (null when it is missing or holds something else).</summary>
    public double[]? Numbers(IToken? token)
    {
        if (Array(token) is not { } a) return null;
        var list = new double[a.Length];
        for (int i = 0; i < list.Length; i++)
        {
            if (Number(a[i]) is not { } v) return null;
            list[i] = v;
        }
        return list;
    }

    public string? StringValue(IToken? token) => Resolve(token) switch
    {
        StringToken s => s.Data,
        HexToken h => h.Data,
        NameToken n => n.Data,
        _ => null,
    };

    /// <summary>The bytes of a string object (the characters are bytes: a PDF string is not text).</summary>
    public byte[]? StringBytes(IToken? token) => Resolve(token) switch
    {
        StringToken s => System.Text.Encoding.Latin1.GetBytes(s.Data),
        HexToken h => h.Bytes.ToArray(),
        _ => null,
    };

    // ---------------------------------------------------------------- streams

    private static readonly HashSet<string> ImageFilters = new(StringComparer.Ordinal) { "DCTDecode", "DCT", "JPXDecode", "CCITTFaxDecode", "CCF", "JBIG2Decode" };

    /// <summary>The names of the filters of a stream, in order.</summary>
    public List<string> FilterNames(DictionaryToken dict)
    {
        var list = new List<string>();
        var f = Get(dict, "Filter") ?? Get(dict, "F");
        if (f is NameToken n) list.Add(n.Data);
        else if (Array(f) is { } a)
            foreach (var item in a.Data) if (Name(item) is { } nm) list.Add(nm);
        return list;
    }

    /// <summary>
    /// The decoded bytes of a stream. A picture's image codec (JPEG, JPEG 2000, fax, JBIG2) is not undone: decoding stops before it and
    /// <paramref name="imageFilter"/> names it, with the bytes it would decode.
    /// </summary>
    public byte[] Decode(StreamToken stream, out string? imageFilter)
    {
        imageFilter = null;
        var dict = stream.StreamDictionary;
        var names = FilterNames(dict);
        if (names.Count == 0) return stream.Data.ToArray();

        var provider = Document.Structure.FilterProvider;
        var filters = provider.GetFilters(dict, _scanner);
        var data = stream.Data;
        for (int i = 0; i < filters.Count; i++)
        {
            string name = i < names.Count ? names[i] : "";
            if (ImageFilters.Contains(name)) { imageFilter = name; break; }
            data = filters[i].Decode(data, dict, DefaultFilterProvider.Instance, i);
        }
        return data.ToArray();
    }
}
