using DeSignMage.Core.Commands;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

// Effects ▸ Soft Shadow, Glow, Feather (notes/effects/soft-shadow.md, glow.md, feather.md).
public sealed partial class EditorSession
{
    // ---- Menu state ----

    public bool CanSoftEffect => SoftEffect.CanRun(this);
    public bool CanFeather => FeatherTool.CanRun(this);

    /// <summary>The menu check mark: the selection already has this effect (a Soft Shadow or Glow object in its group, or a feather).</summary>
    public bool HasSoftEffect(SoftKind kind) => Selection.Items.Any(i => SoftEffect.Find(i, kind) is not null);

    public bool HasFeather => Selection.Items.Any(i => i.Style.Feather is not null || (i is GroupObject g && g.Leaves().Any(l => l.Style.Feather is not null)));

    // ---- Tools ----

    /// <summary>Effects ▸ Soft Shadow / Glow: opens the tool on the selection (the effect shows at once). False if nothing in it has an area.</summary>
    public bool BeginSoftEffect(SoftKind kind)
    {
        if (!CanSoftEffect) return false;
        SetTool("softeffect");
        return GetTool<SoftEffectTool>().Begin(kind);
    }

    /// <summary>Effects ▸ Feather: opens the tool on the selection (a 4 % feather shows at once).</summary>
    public bool BeginFeather()
    {
        if (!CanFeather) return false;
        SetTool("feather");
        return GetTool<FeatherTool>().Begin();
    }

    // ---- Direct commands (tests, automation, and what the tools do underneath) ----

    /// <summary>Makes (or changes) the soft effect of the selection with these settings as one undo step, without the tool. Returns the effect objects.</summary>
    public IReadOnlyList<PathObject> ApplySoftEffect(SoftSettings settings)
    {
        var plan = SoftEffect.Plan(this, SoftEffect.Sources(this), settings.Normalized());
        if (plan is null) return [];
        History.Execute(plan);
        var groups = plan.Targets.Select((t, i) => (DocObject)(plan.GroupOf(i) ?? t.Source as GroupObject ?? t.Source)).ToList();
        Selection.Set(groups);
        return plan.Effects.ToArray();
    }

    /// <summary>Removes the soft effect of this kind from everything selected (one undo step). False when there was none.</summary>
    public bool RemoveSoftEffect(SoftKind kind)
    {
        var effects = Selection.Items.Select(i => SoftEffect.Find(i, kind)).OfType<PathObject>().ToList();
        if (effects.Count == 0) return false;
        using (History.BeginTransaction("Remove " + SoftEffect.NameOf(kind)))
            History.Execute(new DeleteObjectsCommand(effects));
        return true;
    }

    /// <summary>Feathers the selection (one undo step); an amount of 0 removes the feather. False when nothing can be feathered.</summary>
    public bool SetFeather(double amount, double inset)
    {
        var targets = FeatherTool.Targets(this);
        if (targets.Count == 0) return false;
        var feather = amount > 0 ? new FeatherStyle(amount, inset) : null;
        History.Execute(new FeatherCommand(feather is null ? "Remove Feather" : "Feather", targets, feather));
        return true;
    }
}
