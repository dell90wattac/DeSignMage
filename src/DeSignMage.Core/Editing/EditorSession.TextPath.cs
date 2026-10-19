using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Core.Editing;

// Effects ▸ Text on Path (TASKS C.28; notes/effects/text-on-path.md).
public sealed partial class EditorSession
{
    /// <summary>
    /// The text Text on Path would act on and the shape to take the path from, if one was selected with it: exactly one text, or one text plus one
    /// other (non-text, non-group) object. Null when the selection is anything else.
    /// </summary>
    private (TextObject Text, DocObject? Source)? TextPathParts()
    {
        var items = Selection.Items;
        if (items.Count is < 1 or > 2) return null;
        var texts = items.OfType<TextObject>().ToList();
        if (texts.Count != 1 || texts[0].Locked) return null;
        var others = items.Where(o => o is not TextObject).ToList();
        if (others.Count == 0) return (texts[0], null);
        if (others.Count == 1 && others[0] is not GroupObject && !others[0].Locked && others[0].GetLocalGeometry() is { IsEmpty: false }) return (texts[0], others[0]);
        return null;
    }

    /// <summary>True when Effects ▸ Text on Path can run: one text, or one text with one shape to follow.</summary>
    public bool CanTextOnPath => !EffectBusy && TextPathParts() is not null;

    /// <summary>The menu check mark: the selected text already follows a path.</summary>
    public bool HasTextOnPath => TextPathParts() is { Text.IsOnPath: true };

    /// <summary>
    /// Effects ▸ Text on Path. With a text and a shape selected, the text starts following the shape's outline (its first loop; the shape stays as it was, an ordinary
    /// object, neither grouped nor linked). With just a text, it gets the default gentle S-curve along its baseline, ready to be shaped. A text that already follows a
    /// path just opens its path. Opens the tool; the first step is one undo step ("Text on Path").
    /// </summary>
    public bool BeginTextOnPath()
    {
        if (TextPathParts() is not { } parts) return false;
        var (text, source) = parts;
        MarkNextMode();   // Cancel in the tool also takes the first "Text on Path" step back
        if (!text.IsOnPath || source is not null)
        {
            var settings = source is null ? TextOnPath.DefaultFor(text) : (text.OnPath ?? new TextOnPath(PathGeometry.EmptyPath)) with { Path = PathFollowing(text, source) };
            if (source is null || settings.Path is { IsEmpty: false }) History.Execute(new SetTextOnPathCommand(text, settings));
        }
        SetTool("textpath");
        return GetTool<TextPathTool>().Begin(text);
    }

    /// <summary>The outline of <paramref name="source"/> in the text's own coordinate space.</summary>
    private static PathGeometry PathFollowing(TextObject text, DocObject source)
    {
        var textWorld = text.GetWorldTransform();
        var toText = textWorld.IsInvertible ? source.GetWorldTransform() * textWorld.Invert() : source.GetWorldTransform();
        return source.GetLocalGeometry()!.Transform(toText);
    }

    /// <summary>Puts <paramref name="text"/> on <paramref name="settings"/> as one undo step, without the tool (tests, automation).</summary>
    public void ApplyTextOnPath(TextObject text, TextOnPath? settings) =>
        History.Execute(new SetTextOnPathCommand(text, settings, settings is null ? "Remove Text on Path" : "Text on Path"));
}