using System.Text.Json;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>What a codec gets while writing: the JSON writer plus recursion into children, styles and paints.</summary>
public sealed class DsmWriteContext
{
    internal DsmWriteContext(Utf8JsonWriter json, DsmCodecRegistry registry)
    {
        Json = json;
        Registry = registry;
    }

    public Utf8JsonWriter Json { get; }
    public DsmCodecRegistry Registry { get; }

    /// <summary>Where each group stands in the file's <c>groups</c> list. Objects and groups refer to the group they belong to by this number.</summary>
    internal Dictionary<GroupObject, int> GroupIndexes { get; } = [];

    /// <summary>Writes <c>"name": [ object, object, ... ]</c>.</summary>
    public void WriteObjectList(string name, IEnumerable<DocObject> objects)
    {
        Json.WriteStartArray(name);
        foreach (var o in objects) WriteObject(o);
        Json.WriteEndArray();
    }

    public void WriteObject(DocObject o)
    {
        var codec = Registry.ForObject(o)
            ?? throw new DsmFormatException($"{o.GetType().Name} objects cannot be saved yet (no file codec is registered for them).");
        Json.WriteStartObject();
        Json.WriteString("kind", codec.Kind);
        if (o.Name is { } name) Json.WriteString("name", name);
        if (!o.Visible) Json.WriteBoolean("visible", false);
        if (o.Locked) Json.WriteBoolean("locked", true);
        if (!o.Transform.IsIdentity) Json.WriteTransform("transform", o.Transform);
        // Membership, not position: the group an object (or a group) belongs to, by its number in the "groups" list. The position in the
        // drawing order is the position in the layer's list (R22).
        if (o.Parent is GroupObject g && GroupIndexes.TryGetValue(g, out int groupIndex)) Json.WriteNumber("group", groupIndex);
        if (o.Style.Fill is not null || o.Style.Stroke is not null || o.Style.HasSoftEffects) WriteStyle("style", o.Style);
        codec.Write(o, this);
        Json.WriteEndObject();
    }

    public void WriteStyle(string name, ObjectStyle style)
    {
        Json.WriteStartObject(name);
        if (style.Fill is { } fill) WritePaint("fill", fill);
        if (style.Stroke is { } stroke)
        {
            Json.WriteStartObject("stroke");
            WritePaint("paint", stroke.Paint);
            Json.WriteNum("width", stroke.Width);
            if (stroke.Join != LineJoin.Miter) Json.WriteEnum("join", stroke.Join);
            if (stroke.Cap != LineCap.Butt) Json.WriteEnum("cap", stroke.Cap);
            if (stroke.MiterLimit != 4) Json.WriteNum("miterLimit", stroke.MiterLimit);
            Json.WriteEndObject();
        }
        // soft paint (R8): parameters only, the renderer makes the softness when it draws
        if (style.Blur is { } blur)
        {
            Json.WriteStartObject("blur");
            Json.WriteEnum("kind", blur.Kind);
            Json.WriteNum("opacity", blur.Opacity);
            Json.WriteNum("softness", blur.Softness);
            Json.WriteNum("reference", blur.Reference);
            if (blur.Hs != 0) Json.WriteNum("hs", blur.Hs);
            if (blur.Vs != 0) Json.WriteNum("vs", blur.Vs);
            if (blur.Slant != 0) Json.WriteNum("slant", blur.Slant);
            if (blur.HeightScale != 1) Json.WriteNum("heightScale", blur.HeightScale);
            if (blur.Inset != 0) Json.WriteNum("inset", blur.Inset);
            Json.WriteEndObject();
        }
        if (style.Feather is { } feather)
        {
            Json.WriteStartObject("feather");
            Json.WriteNum("amount", feather.Amount);
            Json.WriteNum("inset", feather.Inset);
            Json.WriteEndObject();
        }
        Json.WriteEndObject();
    }

    public void WritePaint(string name, Paint paint)
    {
        var codec = Registry.ForPaint(paint)
            ?? throw new DsmFormatException($"{paint.GetType().Name} fills cannot be saved yet (no file codec is registered for them).");
        Json.WriteStartObject(name);
        Json.WriteString("type", codec.Type);
        codec.Write(paint, this);
        Json.WriteEndObject();
    }
}

/// <summary>What a codec gets while reading: the document being built, recursion into children, styles, paints and warnings.</summary>
public sealed class DsmReadContext
{
    private const int MaxWarnings = 500;

    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, string> _assetIds;
    private int _depth;
    private int _suppressed;
    private readonly List<(GroupObject? Group, int Parent)> _shells = [];   // the file's "groups" list: each group and the number of the group around it (-1 = none)

    internal DsmReadContext(Document document, DsmCodecRegistry registry, Dictionary<string, string> assetIds)
    {
        Document = document;
        Registry = registry;
        _assetIds = assetIds;
    }

    public Document Document { get; }
    public DsmCodecRegistry Registry { get; }
    public int SkippedObjects { get; private set; }

    internal IReadOnlyList<string> Warnings =>
        _suppressed == 0 ? _warnings : [.. _warnings, $"… and {_suppressed} more messages."];

    /// <summary>Records something that was skipped or approximated (shown to the user after opening).</summary>
    public void Warn(string message)
    {
        if (_warnings.Count < MaxWarnings) _warnings.Add(message);
        else _suppressed++;
    }

    /// <summary>The asset id to use for a stored id (they differ only if the stored bytes did not match their name).</summary>
    public string ResolveAssetId(string stored) => _assetIds.GetValueOrDefault(stored, stored);

    /// <summary>
    /// Reads the file's <c>groups</c> list (format 2): the groups, outer ones first, each naming the group around it. They are only made
    /// ready here; a group enters the document when its first member does (<see cref="ReadObjectsInto"/>), which is also where it is on.
    /// </summary>
    public void ReadGroups(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new DsmFormatException("A list of groups was expected.");
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                if (ReadOne(element) is not var (obj, _)) { _shells.Add((null, -1)); continue; }
                if (obj is not GroupObject group) throw new DsmFormatException("The groups list holds something that is not a group.");
                _shells.Add((group, element.Int("group", -1)));
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                _shells.Add((null, -1));
                Warn($"Skipped a damaged group: {ex.Message}");
            }
        }
    }

    /// <summary>The group with this number in the file's list, put in the document (with the groups around it) on <paramref name="layer"/> if it is not there yet.</summary>
    private GroupObject? GroupNumber(int number, Layer layer, int depth = 0)
    {
        if (number < 0 || number >= _shells.Count || _shells[number].Group is not { } group) return null;
        if (Document.Contains(group)) return group;
        if (depth > DsmFormat.MaxGroupDepth) return null;
        var outer = GroupNumber(_shells[number].Parent, layer, depth + 1);
        if (outer is null) Document.Insert(layer, -1, group);
        else Document.Insert(outer, -1, group);
        return group;
    }

    /// <summary>
    /// Reads each element of <paramref name="array"/>, inserts it at the top of <paramref name="container"/> (a layer or
    /// group) and lets its codec read its children. An object that cannot be read is skipped with a warning.
    /// An element with a <c>group</c> number (format 2) also becomes a member of that group: grouping and drawing order are separate.
    /// </summary>
    public void ReadObjectsInto(IObjectContainer container, JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new DsmFormatException("A list of objects was expected.");
        if (++_depth > DsmFormat.MaxGroupDepth)
        {
            _depth--;
            throw new DsmFormatException("Groups are nested too deeply.");
        }
        try
        {
            foreach (var element in array.EnumerateArray())
            {
                try
                {
                    if (ReadOne(element) is not var (obj, codec)) continue;
                    Document.Insert(container, -1, obj);
                    codec.ReadChildren(obj, element, this);
                    if (container is Layer layer && element.TryGet("group", out var number) && number.ValueKind == JsonValueKind.Number)
                    {
                        if (GroupNumber(number.GetInt32(), layer) is { } group) Document.SetGroup(obj, group);
                        else Warn($"An object refers to group {number.GetInt32()}, which does not exist; it was left outside any group.");
                    }
                }
                catch (Exception ex) when (IsDataError(ex))
                {
                    SkippedObjects++;
                    Warn($"Skipped a damaged object: {ex.Message}");
                }
            }
        }
        finally { _depth--; }
    }

    /// <summary>Reads solo objects that are not placed in the document (the parts of a combined object). Groups are not allowed here.</summary>
    public List<DocObject> ReadDetached(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new DsmFormatException("A list of objects was expected.");
        var result = new List<DocObject>();
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                if (ReadOne(element) is not var (obj, _)) continue;
                if (obj is IObjectContainer or CombinedObject)
                {
                    SkippedObjects++;
                    Warn("Skipped a group inside a combined object (not allowed).");
                    continue;
                }
                result.Add(obj);
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                SkippedObjects++;
                Warn($"Skipped a damaged object: {ex.Message}");
            }
        }
        return result;
    }

    private (DocObject Object, ObjectCodec Codec)? ReadOne(JsonElement e)
    {
        var kind = e.Str("kind") ?? throw new DsmFormatException("An object has no 'kind'.");
        if (Registry.ForKind(kind) is not { } codec)
        {
            SkippedObjects++;
            Warn($"Skipped an object of unknown kind '{kind}' (saved by a newer version?).");
            return null;
        }
        var o = codec.Read(e, this);
        o.Name = e.Str("name");
        o.Transform = e.ReadTransform("transform");
        o.Style = ReadStyle(e, "style");
        o.Visible = e.Bool("visible", true);
        o.Locked = e.Bool("locked");
        return (o, codec);
    }

    public ObjectStyle ReadStyle(JsonElement owner, string name)
    {
        if (!owner.TryGet(name, out var s)) return ObjectStyle.None;
        Paint? fill = s.TryGet("fill", out var f) ? ReadPaint(f) : null;
        StrokeStyle? stroke = null;
        if (s.TryGet("stroke", out var st))
        {
            var paint = st.TryGet("paint", out var p) ? ReadPaint(p) : null;
            if (paint is not null)
                stroke = new StrokeStyle(paint, st.Num("width"), st.EnumOf("join", LineJoin.Miter, this), st.EnumOf("cap", LineCap.Butt, this), st.Num("miterLimit", 4));
        }
        BlurStyle? blur = null;
        if (s.TryGet("blur", out var b))
            blur = new BlurStyle(b.EnumOf("kind", SoftKind.SoftShadow, this), b.Num("opacity", 0.5), b.Num("softness", 0.5), b.Num("reference"),
                b.Num("hs"), b.Num("vs"), b.Num("slant"), b.Num("heightScale", 1), b.Num("inset"));
        FeatherStyle? feather = null;
        if (s.TryGet("feather", out var fe) && fe.Num("amount") > 0) feather = new FeatherStyle(fe.Num("amount"), fe.Num("inset"));
        return new ObjectStyle(fill, stroke, blur, feather);
    }

    /// <summary>A paint, or null (with a warning) if its type is not known to this version.</summary>
    public Paint? ReadPaint(JsonElement e)
    {
        var type = e.Str("type") ?? throw new DsmFormatException("A fill or stroke has no 'type'.");
        if (Registry.ForPaintType(type) is not { } codec)
        {
            Warn($"A fill or stroke of unknown type '{type}' was left out (saved by a newer version?).");
            return null;
        }
        return codec.Read(e, this);
    }

    private static bool IsDataError(Exception ex) =>
        ex is DsmFormatException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException
            or InvalidCastException or OverflowException or JsonException;
}

