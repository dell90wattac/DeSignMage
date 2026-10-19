using DeSignMage.Core.Commands;
using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;

namespace DeSignMage.Core.Effects;

/// <summary>
/// Keeps live effects in step with their sources (<see cref="LiveEffects"/>). Plugged into <see cref="CommandHistory.FollowUp"/>: after every new undo step it
/// looks at each live effect object and, in the same step,
/// <list type="bullet">
/// <item>does nothing when neither the source nor the effect changed, or when they moved together (a group moved, scaled or turned: the effect goes with it);</item>
/// <item>moves the effect when the source was only moved (no recomputing; a shift is exact for every effect);</item>
/// <item>rebuilds it from its source and settings when the source changed shape (node edits, a resize of the source alone, new text, another font, text on a path...);</item>
/// <item>keeps it where it is, still live, when only the effect object was moved (the new place is remembered in the link's frame, so the next rebuild comes out there);</item>
/// <item>re-links it to the replacement when its source was replaced by exactly one object (Convert to Curves, a shape turned into a path, Break Apart's copies);</item>
/// <item>makes it plain when its source is gone (deleted, welded, cut into pieces) or when its own outline was edited by hand.</item>
/// </list>
/// Effects of effects (a shadow of a live outline) follow in a further round. Rebuilds of several effects run in parallel on worker threads; the step waits for them.
/// </summary>
public sealed class LiveEffectUpdater(Document document, Func<IBooleanService?> booleans, Func<IOffsetService?> offsets) : IStepFollowUp
{
    /// <summary>What the updater last saw of one effect and its source.</summary>
    private sealed record Seen(LiveLink Link, long SourceKey, Affine2 SourceWorld, Affine2 EffectWorld, int EffectShape);

    /// <summary>Rebuilds done since this updater was made (tests and the perf budget read it).</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Effects moved without recomputing since this updater was made.</summary>
    public int Shifts { get; private set; }

    /// <summary>Above this many rebuilds in one round the work is spread over worker threads.</summary>
    public int ParallelFrom { get; set; } = 2;

    public IDocumentCommand? After(IDocumentCommand step, IDocumentCommand? previous)
    {
        var cmd = Update(Replacements(step));
        if (cmd is null) return previous;
        if (previous is LiveRebuildCommand earlier)
        {
            earlier.Merge(cmd);
            return earlier;
        }
        if (previous is null) return cmd;
        return new CompositeCommand(cmd.Description, [previous, cmd]);
    }

    public void Settled()
    {
        foreach (var e in LiveEffects.All(document)) Remember(e, e.Live!);
    }

    /// <summary>Brings every live effect in step now (outside any undo step: for tests, and for a document whose effects were never seen).</summary>
    public LiveRebuildCommand? Update() => Update([]);

    private LiveRebuildCommand? Update(Dictionary<DocObject, IReadOnlyList<DocObject>> replaced)
    {
        var bool_ = booleans();
        var off = offsets();
        LiveRebuildCommand? cmd = null;
        LiveRebuildCommand Cmd() => cmd ??= new LiveRebuildCommand();
        var bevelCache = new Dictionary<(DocObject, BevelSettings), BevelPieces>();

        for (int round = 0; round < LiveEffects.MaxRounds; round++)
        {
            var jobs = new List<LiveEffects.Job>();
            bool changed = false;
            foreach (var effect in LiveEffects.All(document).ToList())
            {
                var link = effect.Live!;
                var source = link.Source;
                if (!document.Contains(source) || ReferenceEquals(source, effect))
                {
                    if (Follow(source, replaced) is { } next && document.Contains(next) && !ReferenceEquals(next, effect))
                    {
                        var relinked = link with { Source = next };
                        if (LiveEffects.Prepare(effect, relinked) is { } j) { jobs.Add(j); continue; }
                    }
                    MakePlain(Cmd(), effect);
                    changed = true;
                    continue;
                }
                if (effect.LiveSync is not Seen seen || !ReferenceEquals(seen.Link, link))
                {
                    Remember(effect, link);   // a new or newly loaded link: taken as it is
                    continue;
                }
                if (effect.ShapeVersion != seen.EffectShape)
                {
                    MakePlain(Cmd(), effect);   // its own outline was edited by hand: it no longer is what the effect makes
                    changed = true;
                    continue;
                }

                long key = LiveEffects.SourceKey(source);
                var srcWorld = source.GetWorldTransform();
                var fxWorld = effect.GetWorldTransform();
                if (key == seen.SourceKey)
                {
                    if (srcWorld == seen.SourceWorld && fxWorld == seen.EffectWorld) continue;
                    if (Relative(srcWorld, fxWorld) is { } now && Relative(seen.SourceWorld, seen.EffectWorld) is { } before && now.ApproximatelyEquals(before, 1e-9))
                    {
                        Remember(effect, link);   // moved together
                        continue;
                    }
                    if (srcWorld == seen.SourceWorld && seen.EffectWorld.IsInvertible)
                    {
                        // only the effect object was moved: it stays there, and later rebuilds come out there too (the source is read as if it had moved along)
                        var sourceFrame = fxWorld * seen.EffectWorld.Invert() * link.SourceFrameMatrix;
                        var moved = link with { SourceFrame = sourceFrame };
                        Cmd().Set(effect, effect.Geometry, effect.Style, moved);
                        Remember(effect, moved);
                        continue;
                    }
                    if (LiveEffects.FollowsShifts(link.Spec) && ShiftInBuildSpace(link, seen, srcWorld, fxWorld) is { } shift)
                    {
                        var k = link.FrameMatrix;
                        var inEffect = k.IsInvertible ? k.Invert() * Affine2.Translation(shift) * k : Affine2.Translation(shift);
                        Cmd().Set(effect, effect.Geometry.Transform(inEffect), effect.Style, link);
                        Remember(effect, link);
                        Shifts++;
                        changed = true;
                        continue;
                    }
                }
                if (LiveEffects.Prepare(effect, link) is { } job) jobs.Add(job);
                else Remember(effect, link);   // nothing to build from (an empty source): left as it is
            }

            if (jobs.Count > 0)
            {
                var results = new (PathGeometry Geometry, ObjectStyle Style)?[jobs.Count];
                void Run(int i)
                {
                    try { results[i] = LiveEffects.Build(jobs[i], bool_, off, bevelCache); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { results[i] = null; }   // a failed rebuild leaves the effect as it was
                }
                if (jobs.Count >= ParallelFrom) Parallel.For(0, jobs.Count, Run);
                else for (int i = 0; i < jobs.Count; i++) Run(i);

                for (int i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];
                    if (results[i] is { } r)
                    {
                        Cmd().Set(job.Effect, r.Geometry, r.Style, job.Link);
                        Rebuilds++;
                        changed = true;
                    }
                    else if (!ReferenceEquals(job.Effect.Live, job.Link))
                    {
                        Cmd().Set(job.Effect, job.Effect.Geometry, job.Effect.Style, job.Link);   // re-linked even if it could not be rebuilt
                        changed = true;
                    }
                    Remember(job.Effect, job.Link);
                }
            }
            if (!changed) break;
        }
        return cmd is { IsEmpty: false } ? cmd : null;
    }

    private static void MakePlain(LiveRebuildCommand cmd, PathObject effect)
    {
        cmd.Set(effect, effect.Geometry, effect.Style, null);
        effect.LiveSync = null;
    }

    private static void Remember(PathObject effect, LiveLink link) =>
        effect.LiveSync = new Seen(link, LiveEffects.SourceKey(link.Source), link.Source.GetWorldTransform(), effect.GetWorldTransform(), effect.ShapeVersion);

    /// <summary>Where the source is seen from the effect: source to world to effect space.</summary>
    private static Affine2? Relative(Affine2 sourceWorld, Affine2 effectWorld) =>
        effectWorld.IsInvertible ? sourceWorld * effectWorld.Invert() : null;

    /// <summary>When the source only moved by a plain shift as seen in the build space, that shift; null for anything else (scaled, turned, slanted).</summary>
    private static Vec2? ShiftInBuildSpace(LiveLink link, Seen seen, Affine2 srcWorld, Affine2 fxWorld)
    {
        var s = link.SourceFrameMatrix;
        if (!seen.EffectWorld.IsInvertible || !fxWorld.IsInvertible) return null;
        var before = seen.SourceWorld * seen.EffectWorld.Invert() * s;   // source -> build space, then and now
        var now = srcWorld * fxWorld.Invert() * s;
        if (!before.IsInvertible) return null;
        var delta = before.Invert() * now;
        const double eps = 1e-9;
        if (Math.Abs(delta.M11 - 1) > eps || Math.Abs(delta.M22 - 1) > eps || Math.Abs(delta.M12) > eps || Math.Abs(delta.M21) > eps) return null;
        return delta.Offset;
    }

    /// <summary>What replaced <paramref name="source"/> in this step, following replacements of replacements; null when it was not replaced by exactly one object.</summary>
    private static DocObject? Follow(DocObject source, Dictionary<DocObject, IReadOnlyList<DocObject>> replaced)
    {
        var o = source;
        for (int i = 0; i < 16 && replaced.TryGetValue(o, out var next); i++)
        {
            if (next.Count != 1) return null;
            o = next[0];
        }
        return ReferenceEquals(o, source) ? null : o;
    }

    private static Dictionary<DocObject, IReadOnlyList<DocObject>> Replacements(IDocumentCommand step)
    {
        var map = new Dictionary<DocObject, IReadOnlyList<DocObject>>(ReferenceEqualityComparer.Instance);
        Collect(step, map);
        return map;

        static void Collect(IDocumentCommand c, Dictionary<DocObject, IReadOnlyList<DocObject>> map)
        {
            switch (c)
            {
                case CompositeCommand comp: foreach (var sub in comp.Commands) Collect(sub, map); break;
                case FollowedCommand f: Collect(f.Primary, map); break;
            }
            if (c is IReplacesObjects r)
                foreach (var (old, @new) in r.Replacements) map[old] = @new;
        }
    }
}
