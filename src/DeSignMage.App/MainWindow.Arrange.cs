using DeSignMage.App.Shell;
using DeSignMage.Core.Editing;
using DeSignMage.Core.Editing.Tools;

namespace DeSignMage.App;

/// <summary>
/// Arrange ▸ Align, Mirror and Locked / Unlock All. They act on the selection of the Layout tool; in the Vector Edit tool the same letters keep
/// their own meaning (L is To Line), because these commands are only enabled while the Layout tool is active.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Align submenu, in the legacy order: the three horizontal edges, the three vertical ones, then the page.</summary>
    internal static readonly string[] AlignMenu =
    [
        "arrange.alignLeft", "arrange.alignCenter", "arrange.alignRight", "-",
        "arrange.alignTop", "arrange.alignMiddle", "arrange.alignBottom", "-",
        "arrange.centerH", "arrange.centerV", "arrange.centerBoth",
    ];

    private bool InLayoutTool => _session.ActiveTool is SelectTool;

    private void RegisterArrangeCommands()
    {
        void Add(string id, string header, Action run, Func<bool> can, Func<bool>? isChecked = null) =>
            _commands.Add(new AppCommand(id, header, run) { CanExecute = can, IsChecked = isChecked });
        void AddAlign(string id, string header, AlignMode mode) =>
            Add(id, header, () => _session.Align(mode), () => InLayoutTool && _session.CanAlign(mode));
        void AddCenter(string id, string header, PageCentering centering) =>
            Add(id, header, () => _session.CenterOnPage(centering), () => InLayoutTool && _session.CanCenterOnPage);

        // Plain L, R and N are the user's keys for Left, Right and Center (see KeyMap); the Ctrl forms of the legacy table are not bound.
        AddAlign("arrange.alignLeft", "Align _Left", AlignMode.Left);
        AddAlign("arrange.alignCenter", "Align C_enter", AlignMode.Center);
        AddAlign("arrange.alignRight", "Align _Right", AlignMode.Right);
        AddAlign("arrange.alignTop", "Align _Top Edge", AlignMode.Top);
        AddAlign("arrange.alignMiddle", "Align _Vertical Center", AlignMode.Middle);
        AddAlign("arrange.alignBottom", "Align _Bottom Edge", AlignMode.Bottom);
        AddCenter("arrange.centerH", "_Horizontal Center of Layout", PageCentering.Horizontal);
        AddCenter("arrange.centerV", "V_ertical Center of Layout", PageCentering.Vertical);
        AddCenter("arrange.centerBoth", "Cent_er of Layout", PageCentering.Both);

        Add("arrange.mirrorH", "Horizontal _Mirror", () => _session.Mirror(horizontal: true), () => InLayoutTool && _session.CanMirror);
        Add("arrange.mirrorV", "Vertical Mi_rror", () => _session.Mirror(horizontal: false), () => InLayoutTool && _session.CanMirror);

        Add("arrange.lock", "Loc_ked", () => _session.ToggleLockSelection(), () => InLayoutTool && _session.CanLock, () => _session.SelectionAllLocked);
        Add("arrange.unlockAll", "Unlock A_ll", () => _session.UnlockAll(), () => _session.HasLockedObjects);
    }
}
