namespace DeSignMage.App.Shell;

/// <summary>Which of an object's two paints the color UI edits (Fill / Stroke radio).</summary>
public enum ColorTarget { Fill, Stroke }

/// <summary>The color model of the Color Mixer's sliders. HSV edits produce plain RGB colors.</summary>
public enum ColorEditMode { Rgb, Cmyk, Hsv }

/// <summary>
/// What the Color Mixer and the Project Color Bar share: the Fill/Stroke target, the slider model and whether the used-colors row
/// shows. One per open document window.
/// </summary>
public sealed class ColorUiState
{
    private ColorTarget _target;
    private ColorEditMode _mode;

    public ColorUiState(ColorUiState? previous = null)
    {
        _target = previous?._target ?? ColorTarget.Fill;
        _mode = previous?._mode ?? ColorEditMode.Rgb;
        _showJobPalette = previous?._showJobPalette ?? true;
    }

    private bool _showJobPalette = true;

    /// <summary>View ▸ Show Job Color Palette: the used-colors row at the left of the Project Color Bar.</summary>
    public bool ShowJobPalette
    {
        get => _showJobPalette;
        set
        {
            if (_showJobPalette == value) return;
            _showJobPalette = value;
            JobPaletteVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? JobPaletteVisibilityChanged;

    public ColorTarget Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ColorEditMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? TargetChanged, ModeChanged;
}
