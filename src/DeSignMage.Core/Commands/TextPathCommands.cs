using DeSignMage.Core.Model;
using DeSignMage.Core.Text;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Sets, changes or removes a text's path (Text on Path). The text itself, its font and its spacing are not touched; removing the path
/// puts the straight text back exactly as it was. Used for every change the tool and its Context Bar make, so each is one undo step.
/// </summary>
public sealed class SetTextOnPathCommand : IDocumentCommand
{
    private readonly TextObject _text;
    private readonly TextOnPath? _old, _new;

    public SetTextOnPathCommand(TextObject text, TextOnPath? value, string description = "Text on Path")
    {
        _text = text;
        _old = text.OnPath;
        _new = value;
        Description = description;
    }

    public string Description { get; }

    public void Do(Document doc) => _text.OnPath = _new;
    public void Undo(Document doc) => _text.OnPath = _old;
}
