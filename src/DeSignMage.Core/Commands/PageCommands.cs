using DeSignMage.Core.Model;

namespace DeSignMage.Core.Commands;

/// <summary>
/// Gives the document a new page rectangle (dragging the page's border). Only the page changes: objects and guides stay exactly
/// where they are in document space, so they keep their place on screen. Undo restores the old size and position.
/// </summary>
public sealed class ResizePageCommand(PageSetup newPage, string description = "Resize Page") : IDocumentCommand
{
    private PageSetup? _old;

    public PageSetup NewPage { get; } = newPage;
    public string Description { get; } = description;

    public void Do(Document doc)
    {
        _old ??= doc.Page;
        doc.Page = NewPage;
    }

    public void Undo(Document doc)
    {
        if (_old is not null) doc.Page = _old;
    }
}
