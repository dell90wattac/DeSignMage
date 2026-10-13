using DeSignMage.Core.Effects;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Geometry;

namespace DeSignMage.Core.Editing;

// Effects ▸ Outline, Inline, Weld, Common Weld, Cookie Cutter, Knife.
public sealed partial class EditorSession
{
    private CancellationTokenSource? _effectCts;

    /// <summary>The path booleans (Weld, Common Weld, Cookie Cutter, Knife). The window plugs in <c>GeometryEngines.Booleans</c>; without one the commands are unavailable.</summary>
    public IBooleanService? BooleanService { get; set; }

    /// <summary>The offsets (Outline, Inline, Cookie Cutter outline). The window plugs in <c>GeometryEngines.Offsets</c>.</summary>
    public IOffsetService? OffsetService { get; set; }

    /// <summary>
    /// Runs something on the UI thread. The window sets it; null runs the action right away. Previews and effect results computed
    /// on worker threads come back through it.
    /// </summary>
    public Action<Action>? UiPost { get; set; }

    /// <summary>True while an effect is being computed on a worker thread (the status bar shows a progress indicator).</summary>
    public bool EffectBusy { get; private set; }

    /// <summary>What the busy indicator says ("Welding...").</summary>
    public string? EffectBusyText { get; private set; }

    public event EventHandler? EffectBusyChanged;

    /// <summary>The note of the last effect that finished or could not run (why nothing happened), for the status bar; null after a plain success.</summary>
    public string? LastEffectMessage { get; private set; }

    /// <summary>Raised on the UI thread when an effect command finished (applied or not).</summary>
    public event EventHandler<EffectOutcome>? EffectFinished;

    /// <summary>The newest effect started with <see cref="RunEffectAsync"/> (completed when it is done). Tests await it.</summary>
    public Task EffectIdle { get; private set; } = Task.CompletedTask;

    internal PreviewRunner NewPreviewRunner() => new(a => { if (UiPost is { } post) post(a); else a(); });

    // ---- Running a plan ----

    /// <summary>Computes and applies a plan right here, on this thread. Used by tests and small jobs.</summary>
    public EffectOutcome RunEffect(EffectPlan plan)
    {
        if (EffectBusy) return EffectOutcome.Nothing("Another effect is still working.");
        EffectOutcome outcome;
        try { outcome = plan.Compute(CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException) { outcome = Failed(plan, e); }
        return Finish(plan, outcome);
    }

    /// <summary>
    /// Computes on a worker thread (the window stays alive; the status bar shows a progress indicator) and applies the result back
    /// on the UI thread as one undo step. If the document changed meanwhile the result is dropped. Esc calls <see cref="CancelEffect"/>.
    /// </summary>
    public Task<EffectOutcome> RunEffectAsync(EffectPlan plan)
    {
        if (EffectBusy) return Task.FromResult(EffectOutcome.Nothing("Another effect is still working."));
        var task = RunAsyncCore(plan);
        EffectIdle = task;
        return task;
    }

    private async Task<EffectOutcome> RunAsyncCore(EffectPlan plan)
    {
        var cts = _effectCts = new CancellationTokenSource();
        long revision = Document.Revision;
        SetBusy(true, plan.BusyText);
        EffectOutcome outcome;
        try
        {
            // Esc lets go of the window at once; the worker finishes in the background and its result is thrown away.
            var work = Task.Run(() => plan.Compute(cts.Token));
            var cancelled = new TaskCompletionSource();
            using (cts.Token.Register(() => cancelled.TrySetResult()))
            {
                if (await Task.WhenAny(work, cancelled.Task) != work)
                {
                    _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new OperationCanceledException(cts.Token);
                }
            }
            outcome = await work;
        }
        catch (OperationCanceledException)
        {
            outcome = EffectOutcome.Nothing("Cancelled.");
        }
        catch (Exception e)
        {
            outcome = Failed(plan, e);
        }
        finally
        {
            _effectCts = null;
            SetBusy(false, null);
        }
        if (outcome.ApplyStep is not null && Document.Revision != revision)
            outcome = EffectOutcome.Nothing("The drawing changed while the effect was working, so it was not applied.");
        return Finish(plan, outcome);
    }

    /// <summary>Esc: abandons the running effect (its result is discarded).</summary>
    public bool CancelEffect()
    {
        if (_effectCts is null) return false;
        _effectCts.Cancel();
        return true;
    }

    private static EffectOutcome Failed(EffectPlan plan, Exception e) =>
        EffectOutcome.Nothing($"{plan.Description} could not be done ({e.GetType().Name}).");

    private EffectOutcome Finish(EffectPlan plan, EffectOutcome outcome)
    {
        if (outcome.ApplyStep is { } apply)
        {
            var tx = History.BeginTransaction(plan.Description);
            try
            {
                apply(this);
                tx.Dispose();
                outcome.Applied = true;
            }
            catch (Exception e)
            {
                tx.Rollback();
                outcome = Failed(plan, e);
            }
        }
        LastEffectMessage = outcome.Message;
        EffectFinished?.Invoke(this, outcome);
        return outcome;
    }

    private void SetBusy(bool busy, string? text)
    {
        EffectBusy = busy;
        EffectBusyText = text;
        EffectBusyChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Menu state ----

    public bool CanOutline => !EffectBusy && OutlineEffect.CanRun(this);
    public bool CanWeld => !EffectBusy && WeldEffect.CanRun(this);
    public bool CanCommonWeld => !EffectBusy && CommonWeldEffect.CanRun(this);
    public bool CanCookieCut => !EffectBusy && CookieCutEffect.CanRun(this);
    public bool CanKnife => !EffectBusy && KnifeEffect.CanRun(this);
    public bool CanShadow => !EffectBusy && ShadowEffect.CanRun(this);

    // ---- Immediate commands ----

    /// <summary>Effects ▸ Weld, computed here and now (tests). The window uses <see cref="WeldAsync"/>.</summary>
    public EffectOutcome Weld() => WeldEffect.Plan(this) is { } plan ? RunEffect(plan) : Unavailable("Weld needs at least two closed shapes.");

    public Task<EffectOutcome> WeldAsync() => WeldEffect.Plan(this) is { } plan ? RunEffectAsync(plan) : Task.FromResult(Unavailable("Weld needs at least two closed shapes."));

    public EffectOutcome CommonWeld() => CommonWeldEffect.Plan(this) is { } plan ? RunEffect(plan) : Unavailable("Common Weld needs at least two overlapping shapes.");

    public Task<EffectOutcome> CommonWeldAsync() => CommonWeldEffect.Plan(this) is { } plan ? RunEffectAsync(plan) : Task.FromResult(Unavailable("Common Weld needs at least two overlapping shapes."));

    /// <summary>Effects ▸ Cookie Cutter with the selection as the cutter, computed here and now (tests). <paramref name="outline"/> in points.</summary>
    public EffectOutcome CookieCut(double outline = 0, Vec2 moveBy = default) =>
        CookieCutEffect.Plan(this, outline, moveBy) is { } plan ? RunEffect(plan) : Unavailable("Cookie Cutter needs a shape selected to use as the cutter.");
    private EffectOutcome Unavailable(string message)
    {
        LastEffectMessage = message;
        var o = EffectOutcome.Nothing(message);
        EffectFinished?.Invoke(this, o);
        return o;
    }

    // ---- Tools ----

    /// <summary>Effects ▸ Outline / Inline: opens the tool on the selection. False if nothing in it can be outlined.</summary>
    public bool BeginOutline(OutlineKind kind)
    {
        if (!CanOutline) return false;
        SetTool("outline");
        GetTool<OutlineTool>().Begin(kind);
        return true;
    }

    /// <summary>Effects ▸ Cookie Cutter: the selection becomes the cutter.</summary>
    public bool BeginCookieCutter()
    {
        if (!CanCookieCut) return false;
        SetTool("cookie");
        GetTool<CookieCutterTool>().Begin();
        return true;
    }

    /// <summary>Effects ▸ Shadow / Extruded / Perspective / Cast Shadow: opens the tool on the selection in that form. False if nothing in it has an area.</summary>
    public bool BeginShadow(ShadowForm form)
    {
        if (!CanShadow) return false;
        SetTool("shadow");
        GetTool<ShadowTool>().Begin(form);
        return true;
    }

    /// <summary>Makes the shadow of the selection with these settings right here (tests); the tool's Apply does the same from the Context Bar.</summary>
    public EffectOutcome Shadow(ShadowSettings settings) =>
        ShadowEffect.Plan(this, settings) is { } plan ? RunEffect(plan) : Unavailable("A shadow needs a shape selected and a shift that is not zero.");

    /// <summary>Effects ▸ Knife.</summary>
    public bool BeginKnife()
    {
        if (!CanKnife) return false;
        SetTool("knife");
        return true;
    }
}

