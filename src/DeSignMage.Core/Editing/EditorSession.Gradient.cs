using DeSignMage.Core.Editing.Tools;

namespace DeSignMage.Core.Editing;

// Effects ▸ Gradient Blend (the tool is GradientTool).
public sealed partial class EditorSession
{
    /// <summary>Gradient Blend works on one selected shape, path or text.</summary>
    public bool CanGradient => GradientTool.CanTarget(this);

    /// <summary>Effects ▸ Gradient Blend: opens the control-point tool on the selected object. False when nothing suitable is selected.</summary>
    public bool BeginGradient()
    {
        if (!CanGradient) return false;
        SetTool("gradient");
        return true;
    }
}
