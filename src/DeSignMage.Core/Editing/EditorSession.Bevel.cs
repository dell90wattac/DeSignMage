using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

// Effects ▸ Easy Bevel (notes/effects/easy-bevel.md).
public sealed partial class EditorSession
{
    public bool CanBevel => !EffectBusy && BevelEffect.CanRun(this);

    /// <summary>The menu check mark: something in the selection already has a bevel.</summary>
    public bool HasBevel => BevelEffect.HasBevel(this);

    /// <summary>Effects ▸ Easy Bevel: opens the tool on the selection (the bevel shows at once). False if nothing in it can be beveled.</summary>
    public bool BeginBevel()
    {
        if (!CanBevel) return false;
        SetTool("bevel");
        return GetTool<BevelTool>().Begin();
    }

    /// <summary>Bevels the selection with these settings as one undo step, without the tool (tests, automation). Returns the top-level objects that now hold bevels.</summary>
    public IReadOnlyList<DocObject> ApplyBevel(BevelSettings settings)
    {
        if (BooleanService is not { } booleans || OffsetService is not { } offsets) return [];
        var sources = BevelEffect.Sources(this);
        var plan = BevelEffect.Plan(sources, BevelEffect.ComputeAll(booleans, offsets, sources, settings), settings);   // live crescents
        if (plan is null) return [];
        History.Execute(plan);
        var tops = plan.TopLevels.ToList();
        Selection.Set(tops);
        return tops;
    }

    /// <summary>Removes the bevel from everything selected (one undo step). False when there was none.</summary>
    public bool RemoveBevel()
    {
        var crescents = BevelEffect.Leaves(this).SelectMany(BevelEffect.Existing).ToList();
        if (crescents.Count == 0) return false;
        using (History.BeginTransaction("Remove Bevel"))
            History.Execute(new DeleteObjectsCommand(crescents));
        return true;
    }
}