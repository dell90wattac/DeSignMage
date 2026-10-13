using DeSignMage.Core.Editing;

namespace DeSignMage.Core.Effects;

/// <summary>
/// One effect command, split in two so the heavy part can leave the UI thread (interactive
/// preview): <see cref="Compute"/> works only on immutable snapshots taken when the plan was built (world-space
/// <c>PathGeometry</c> values, copied styles) and the stateless engines; the <see cref="EffectOutcome"/> it returns carries an
/// <c>Apply</c> step that runs on the UI thread inside ONE undo transaction.
/// </summary>
public sealed class EffectPlan
{
    internal EffectPlan(string description, string busyText, Func<CancellationToken, EffectOutcome> compute)
    {
        Description = description;
        BusyText = busyText;
        Compute = compute;
    }

    /// <summary>Undo step name ("Weld", "Outline", "Knife Cut", ...).</summary>
    public string Description { get; }

    /// <summary>What the status bar says while a long job runs ("Welding...").</summary>
    public string BusyText { get; }

    internal Func<CancellationToken, EffectOutcome> Compute { get; }
}

/// <summary>What a computed effect wants to do to the document, or why it has nothing to do.</summary>
public sealed class EffectOutcome
{
    private EffectOutcome(Action<EditorSession>? apply, string? message)
    {
        ApplyStep = apply;
        Message = message;
    }

    /// <summary>Nothing changes; <paramref name="message"/> is shown in the status bar.</summary>
    public static EffectOutcome Nothing(string message) => new(null, message);

    internal static EffectOutcome Change(Action<EditorSession> apply, string? message = null) => new(apply, message);

    internal Action<EditorSession>? ApplyStep { get; }

    /// <summary>A short note for the status bar (why nothing happened, or what was made), or null.</summary>
    public string? Message { get; internal set; }

    /// <summary>True once the document was changed by this outcome (one undo step).</summary>
    public bool Applied { get; internal set; }
}
