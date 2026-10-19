using DeSignMage.Core.Commands;
using DeSignMage.Core.Effects;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Editing;

// Live effects: effect objects that follow their sources (notes/effects/live-effects.md).
public sealed partial class EditorSession
{
    /// <summary>Rebuilds live effects as part of every undo step (plugged into <see cref="CommandHistory.FollowUp"/>).</summary>
    public LiveEffectUpdater LiveUpdater { get; }

    /// <summary>Effects ▸ Make Plain is available: the selection holds live effects, or objects that have them.</summary>
    public bool CanMakePlain => !SelectionLocked && LiveEffects.PlainTargets(Document, Selection.Items).Count > 0;

    /// <summary>
    /// Effects ▸ Make Plain: the live effects in the selection, and those of the selected objects, stop following their sources. They stay as they are,
    /// as ordinary objects. One undo step. Returns how many effects changed.
    /// </summary>
    public int MakePlain()
    {
        if (SelectionLocked) return 0;
        var targets = LiveEffects.PlainTargets(Document, Selection.Items);
        if (targets.Count == 0) return 0;
        History.Execute(new SetLiveLinkCommand("Make Plain", targets.Select(t => (t, (LiveLink?)null))));
        return targets.Count;
    }

    /// <summary>The one live effect that is selected (alone, or as the only live effect in a selected group), for the Info Line; null otherwise.</summary>
    public PathObject? SelectedLiveEffect
    {
        get
        {
            if (Selection.Count != 1) return null;
            var item = Selection.Items[0];
            if (item is PathObject { Live: not null } p) return p;
            return null;
        }
    }

    /// <summary>The live effects that follow the selected objects (or lie inside selected groups), for the Info Line.</summary>
    public int SelectedLiveEffectCount => LiveEffects.PlainTargets(Document, Selection.Items).Count;
}
