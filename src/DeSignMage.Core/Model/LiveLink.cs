using DeSignMage.Core.Effects;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Model;

/// <summary>
/// What a live effect object follows: the object it was made from (<see cref="Source"/>) and the settings it was made with (<see cref="Spec"/>).
/// An effect object that carries one (<see cref="DocObject.Live"/>) is rebuilt whenever its source changes shape or moves away from it, as part
/// of the same undo step as the edit (<c>LiveEffects</c>). The effect object stays an ordinary object in the document: grouped with its source
/// or standalone, recolorable, selectable; only its outline is made by the effect.
/// <para>
/// <see cref="Frame"/> maps the space the effect is computed in to the effect object's own geometry space (null = the same). When an effect is
/// made, it is computed in document coordinates and stored in the coordinates of the group it lives in, so the frame is the inverse of that
/// group's world transform at that moment (<see cref="Create"/>). Keeping it means a later rebuild comes out exactly where the first one did,
/// however the group was turned or scaled since.
/// </para>
/// <para>
/// <see cref="SourceFrame"/> maps the effect object's own space to the space the source is read in (null = the inverse of <see cref="Frame"/>). It differs
/// only after the effect object was moved (or turned, scaled) by itself: the source is then read as if it had moved along, so the effect keeps the place it
/// was moved to through later rebuilds.
/// </para>
/// </summary>
public sealed record LiveLink(DocObject Source, LiveEffectSpec Spec, Affine2? Frame = null, Affine2? SourceFrame = null)
{
    /// <summary>The link for an effect object that is (or will be) drawn with the world transform <paramref name="effectWorld"/> and was computed in document coordinates.</summary>
    public static LiveLink Create(DocObject source, LiveEffectSpec spec, Affine2 effectWorld) =>
        new(source, spec, effectWorld.IsIdentity || !effectWorld.IsInvertible ? null : effectWorld.Invert());

    /// <summary>The link for an effect object that joins the group <paramref name="source"/> is in (shadows, soft effects, bevel crescents), with an identity transform of its own.</summary>
    public static LiveLink BesideSource(DocObject source, LiveEffectSpec spec) =>
        Create(source, spec, source.Parent is DocObject p ? p.GetWorldTransform() : Affine2.Identity);

    /// <summary>The frame as a matrix (identity when none).</summary>
    public Affine2 FrameMatrix => Frame ?? Affine2.Identity;

    /// <summary>Effect space to the space the source is read in (the inverse of the frame unless the effect was moved by itself).</summary>
    public Affine2 SourceFrameMatrix => SourceFrame ?? (Frame is { IsInvertible: true } f ? f.Invert() : Affine2.Identity);
}

/// <summary>Which effect a live effect object is, with the settings it is rebuilt with. Immutable; a change of settings is a new link.</summary>
public abstract record LiveEffectSpec
{
    /// <summary>The effect's name as the user sees it ("Outline", "Shadow", "Soft Shadow", "Bevel Highlight"...).</summary>
    public abstract string DisplayName { get; }
}

/// <summary>Effects ▸ Outline / Inline. The object is standalone (not grouped with its source): an outline below it, an inline above.</summary>
public sealed record LiveOutline(OutlineKind Kind, OutlineSettings Settings) : LiveEffectSpec
{
    public override string DisplayName => OutlineEffect.NameOf(Kind);
}

/// <summary>Effects ▸ Shadow ▸ (all four forms). Grouped with its source, just below it.</summary>
public sealed record LiveShadow(ShadowSettings Settings) : LiveEffectSpec
{
    public override string DisplayName => ShadowEffect.NameOf(Settings.Form);
}

/// <summary>
/// Effects ▸ Soft Shadow / Glow. The settings are not repeated here: they are the effect object's own <see cref="BlurStyle"/> (shift, slant,
/// height, inset or thickness, opacity, softness) and its fill color, as they always were, so recoloring the object recolors the effect.
/// </summary>
public sealed record LiveSoft(SoftKind Kind) : LiveEffectSpec
{
    public override string DisplayName => SoftEffect.NameOf(Kind);
}

/// <summary>Which of the two Easy Bevel crescents an object is.</summary>
public enum BevelPart : byte { Highlight, Shadow }

/// <summary>Effects ▸ Easy Bevel: one of the two crescents, directly above its source, grouped with it. Both crescents carry the same settings.</summary>
public sealed record LiveBevel(BevelSettings Settings, BevelPart Part) : LiveEffectSpec
{
    public override string DisplayName => Part == BevelPart.Highlight ? BevelEffect.HighlightName : BevelEffect.ShadowName;
}
