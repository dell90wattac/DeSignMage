using Avalonia.Controls;
using Avalonia.Threading;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;
using DeSignMage.Core.Effects;
using DeSignMage.Geometry;
using DeSignMage.App.Shell;

namespace DeSignMage.App;

/// <summary>
/// Effects menu, step 2: Outline, Inline, Knife, Weld, Common Weld, Cookie Cutter. The commands are thin:
/// the work is in Core (<c>DeSignMage.Core.Effects</c>) on top of the geometry engines this file plugs into every session.
/// A long job runs on a worker thread; after a moment the status bar shows what it is doing and a progress bar.
/// </summary>
public partial class MainWindow
{
    private ProgressBar? _busyBar;
    private DispatcherTimer? _busyTimer;

    private void RegisterEffectCommands()
    {
        void Add(string id, string header, Action run, Func<bool> can, Func<bool>? isChecked = null) =>
            _commands.Add(new AppCommand(id, header, run) { CanExecute = can, IsChecked = isChecked });

        // No shortcuts yet: the usual ones (Ctrl+I, Ctrl+Shift+W, Ctrl+Shift+K) wait for the user's approval.
        Add("effects.outline", "_Outline...", () => _session.BeginOutline(OutlineKind.Outline), () => _session.CanOutline,
            () => _session.ActiveTool is OutlineTool { Kind: OutlineKind.Outline });
        Add("effects.inline", "_Inline...", () => _session.BeginOutline(OutlineKind.Inline), () => _session.CanOutline,
            () => _session.ActiveTool is OutlineTool { Kind: OutlineKind.Inline });
        Add("effects.knife", "_Knife...", () => _session.BeginKnife(), () => _session.CanKnife, () => _session.ActiveTool is KnifeTool);
        Add("effects.weld", "_Weld", () => RunEffect(_session.WeldAsync()), () => _session.CanWeld);
        Add("effects.commonWeld", "Co_mmon Weld", () => RunEffect(_session.CommonWeldAsync()), () => _session.CanCommonWeld);
        Add("effects.cookieCutter", "Cookie _Cutter...", () => _session.BeginCookieCutter(), () => _session.CanCookieCut, () => _session.ActiveTool is CookieCutterTool);

        // Shadow: one tool, the form is also a field of its Context Bar. Each item opens it in that form.
        bool InShadow(ShadowForm form) => _session.ActiveTool is ShadowTool t && t.Settings.Form == form;
        Add("effects.shadow", "_Shadow...", () => _session.BeginShadow(ShadowForm.Flat), () => _session.CanShadow, () => InShadow(ShadowForm.Flat));
        Add("effects.shadowExtruded", "E_xtruded Shadow...", () => _session.BeginShadow(ShadowForm.Extruded), () => _session.CanShadow, () => InShadow(ShadowForm.Extruded));
        Add("effects.shadowPerspective", "_Perspective Shadow...", () => _session.BeginShadow(ShadowForm.Perspective), () => _session.CanShadow, () => InShadow(ShadowForm.Perspective));
        Add("effects.shadowCast", "Cas_t Shadow...", () => _session.BeginShadow(ShadowForm.Cast), () => _session.CanShadow, () => InShadow(ShadowForm.Cast));

        // Soft Shadow, Glow, Feather: tools that show the effect at once (no shortcuts yet). The check mark shows when the selection already has the effect.
        Add("effects.softShadow", "Soft S_hadow...", () => _session.BeginSoftEffect(DeSignMage.Core.Model.SoftKind.SoftShadow), () => _session.CanSoftEffect,
            () => _session.ActiveTool is SoftEffectTool { Kind: DeSignMage.Core.Model.SoftKind.SoftShadow } || (_session.ActiveTool is not SoftEffectTool && _session.HasSoftEffect(DeSignMage.Core.Model.SoftKind.SoftShadow)));
        Add("effects.glow", "_Glow...", () => _session.BeginSoftEffect(DeSignMage.Core.Model.SoftKind.Glow), () => _session.CanSoftEffect,
            () => _session.ActiveTool is SoftEffectTool { Kind: DeSignMage.Core.Model.SoftKind.Glow } || (_session.ActiveTool is not SoftEffectTool && _session.HasSoftEffect(DeSignMage.Core.Model.SoftKind.Glow)));
        Add("effects.feather", "_Feather...", () => _session.BeginFeather(), () => _session.CanFeather,
            () => _session.ActiveTool is FeatherTool || (_session.ActiveTool is not FeatherTool && _session.HasFeather));

        // Gradient Blend: the control-point tool on the selected shape or text (no shortcut yet).
        // Easy Bevel: the tool shows the bevel at once (no shortcut yet). The check mark shows when the selection already has one.
        Add("effects.easyBevel", "Easy _Bevel...", () => _session.BeginBevel(), () => _session.CanBevel,
            () => _session.ActiveTool is BevelTool || (_session.ActiveTool is not BevelTool && _session.HasBevel));

        // Text on Path: one text (or a text and a shape to follow) selected; the tool opens on the path. The check mark shows when the text already follows one (no shortcut).
        Add("effects.textOnPath", "Text on _Path...", () => _session.BeginTextOnPath(), () => _session.CanTextOnPath,
            () => _session.ActiveTool is TextPathTool || (_session.ActiveTool is not TextPathTool && _session.HasTextOnPath));

        Add("effects.gradientBlend", "_Gradient Blend...", () => _session.BeginGradient(), () => _session.CanGradient, () => _session.ActiveTool is GradientTool);

        // Live effects follow their sources; Make Plain stops that for the selected effects (or the effects of the selected objects). No shortcut.
        Add("effects.makePlain", "Make Plai_n", () => _session.MakePlain(), () => _session.CanMakePlain);
    }

    /// <summary>Starts an effect that was kicked off with an async method; a crash inside is logged and shown, never thrown at the UI loop.</summary>
    private void RunEffect(Task<EffectOutcome> task) => _ = ObserveAsync(task);

    private async Task ObserveAsync(Task<EffectOutcome> task)
    {
        try { await task; }
        catch (Exception e)
        {
            DeSignMage.Feedback.AppLog.Warn("effects", $"effect failed: {e.GetType().Name}: {e.Message}");
            Flash("That effect could not be done.");
        }
    }

    /// <summary>Plugs the geometry engines into a session and shows what its effects are doing.</summary>
    private void AttachEffects(EditorSession session)
    {
        session.BooleanService = GeometryEngines.Booleans;
        session.OffsetService = GeometryEngines.Offsets;
        session.OffsetPreviewService = GeometryEngines.OffsetPreview;
        session.UiPost = action => Dispatcher.UIThread.Post(action);
        EnsureBusyBar();

        session.EffectBusyChanged += (_, _) => OnEffectBusyChanged(session);
        session.EffectFinished += (_, outcome) =>
        {
            if (outcome.Message is { Length: > 0 } message) Flash(message);
            QueueRefresh();
        };
    }

    private void EnsureBusyBar()
    {
        if (_busyBar is not null) return;
        // The Info Line is a one-line panel (pointer, frame times); the progress bar goes at its right end and is never given up for lack of room.
        _busyBar = new ProgressBar
        {
            IsIndeterminate = true, IsVisible = false, Width = 120, Height = 10, MinHeight = 10,
            Margin = new Avalonia.Thickness(0, 0, 4, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Shell.InfoLinePanel.SetRight(_busyBar, true);
        InfoBar.Children.Add(_busyBar);
    }

    private void OnEffectBusyChanged(EditorSession session)
    {
        _busyTimer?.Stop();
        if (!session.EffectBusy)
        {
            if (_busyBar is not null) _busyBar.IsVisible = false;
            QueueRefresh();
            return;
        }
        // Quick jobs finish before this fires and never flash a progress bar.
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _busyTimer.Tick += (_, _) =>
        {
            _busyTimer?.Stop();
            if (!session.EffectBusy) return;
            if (_busyBar is not null) _busyBar.IsVisible = true;
            if (session.EffectBusyText is { } text) Flash(text + "  (Esc cancels)");
        };
        _busyTimer.Start();
        QueueRefresh();
    }
}
