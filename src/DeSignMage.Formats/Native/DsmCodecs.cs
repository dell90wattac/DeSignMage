using System.Text.Json;
using DeSignMage.Core.Model;

namespace DeSignMage.Formats.Native;

/// <summary>
/// Reads and writes ONE kind of <see cref="DocObject"/>. This is how a new object type plugs into the file format:
/// write a subclass of <see cref="ObjectCodec{T}"/> (its <see cref="Kind"/> string is stored in the file and must never
/// change) and add it to <see cref="DsmCodecRegistry.CreateDefault"/>. The common properties (name, transform, style,
/// visible, locked) are handled by the writer/reader; a codec handles only its own fields.
/// A test fails if a concrete <see cref="DocObject"/> subclass has no codec.
/// </summary>
public abstract class ObjectCodec
{
    /// <summary>The stable name in the file ("rect", "text", ...).</summary>
    public abstract string Kind { get; }

    public abstract Type ObjectType { get; }

    public abstract void Write(DocObject o, DsmWriteContext ctx);

    /// <summary>Creates the object from its own fields (no id, no parent). Throw <see cref="DsmFormatException"/> if it cannot be read.</summary>
    public abstract DocObject Read(JsonElement e, DsmReadContext ctx);

    /// <summary>
    /// For containers: called after the object has been inserted into the document, so children can be inserted
    /// into it with <see cref="DsmReadContext.ReadObjectsInto"/>.
    /// </summary>
    public virtual void ReadChildren(DocObject o, JsonElement e, DsmReadContext ctx) { }
}

public abstract class ObjectCodec<T> : ObjectCodec where T : DocObject
{
    public sealed override Type ObjectType => typeof(T);

    public sealed override void Write(DocObject o, DsmWriteContext ctx) => WriteFields((T)o, ctx);

    public sealed override DocObject Read(JsonElement e, DsmReadContext ctx) => ReadFields(e, ctx);

    public sealed override void ReadChildren(DocObject o, JsonElement e, DsmReadContext ctx) => ReadChildFields((T)o, e, ctx);

    protected abstract void WriteFields(T o, DsmWriteContext ctx);
    protected abstract T ReadFields(JsonElement e, DsmReadContext ctx);
    protected virtual void ReadChildFields(T o, JsonElement e, DsmReadContext ctx) { }
}

/// <summary>Reads and writes one kind of <see cref="Paint"/> (<c>"type"</c> in the file).</summary>
public abstract class PaintCodec
{
    public abstract string Type { get; }
    public abstract Type PaintType { get; }
    public abstract void Write(Paint paint, DsmWriteContext ctx);
    public abstract Paint Read(JsonElement e, DsmReadContext ctx);
}

public abstract class PaintCodec<T> : PaintCodec where T : Paint
{
    public sealed override Type PaintType => typeof(T);
    public sealed override void Write(Paint paint, DsmWriteContext ctx) => WriteFields((T)paint, ctx);
    protected abstract void WriteFields(T paint, DsmWriteContext ctx);
}

/// <summary>The codecs the reader and writer use. <see cref="Default"/> holds everything built in.</summary>
public sealed class DsmCodecRegistry
{
    private readonly Dictionary<Type, ObjectCodec> _objectsByType = [];
    private readonly Dictionary<string, ObjectCodec> _objectsByKind = [];
    private readonly Dictionary<Type, PaintCodec> _paintsByType = [];
    private readonly Dictionary<string, PaintCodec> _paintsByName = [];

    public static DsmCodecRegistry Default { get; } = CreateDefault();

    public static DsmCodecRegistry CreateDefault()
    {
        var r = new DsmCodecRegistry();
        r.Register(new SolidPaintCodec());
        r.Register(new GradientPaintCodec());
        r.Register(new RectangleCodec());
        r.Register(new EllipseCodec());
        r.Register(new PathCodec());
        r.Register(new GroupCodec());
        r.Register(new CombinedCodec());
        r.Register(new TextCodec());
        r.Register(new BitmapCodec());
        return r;
    }

    public void Register(ObjectCodec codec)
    {
        if (!_objectsByKind.TryAdd(codec.Kind, codec)) throw new ArgumentException($"A codec for kind '{codec.Kind}' is already registered.");
        _objectsByType.Add(codec.ObjectType, codec);
    }

    public void Register(PaintCodec codec)
    {
        if (!_paintsByName.TryAdd(codec.Type, codec)) throw new ArgumentException($"A paint codec for '{codec.Type}' is already registered.");
        _paintsByType.Add(codec.PaintType, codec);
    }

    public IEnumerable<ObjectCodec> ObjectCodecs => _objectsByKind.Values;

    /// <summary>The codec for this object's class (a subclass without its own codec uses its base class's).</summary>
    public ObjectCodec? ForObject(DocObject o)
    {
        for (var t = o.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            if (_objectsByType.TryGetValue(t, out var c)) return c;
        return null;
    }

    public ObjectCodec? ForKind(string kind) => _objectsByKind.GetValueOrDefault(kind);

    public PaintCodec? ForPaint(Paint p)
    {
        for (var t = p.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            if (_paintsByType.TryGetValue(t, out var c)) return c;
        return null;
    }

    public PaintCodec? ForPaintType(string type) => _paintsByName.GetValueOrDefault(type);
}

