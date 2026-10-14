using DeSignMage.Core.Geometry;
using DeSignMage.Core.Model;
using UglyToad.PdfPig.Tokens;

namespace DeSignMage.Formats.Vector.Pdf;

/// <summary>A PDF shading (the <c>sh</c> operator or a shading pattern): axial and radial ones become <see cref="GradientPaint"/>.</summary>
internal sealed class PdfShading
{
    public int Type { get; private init; }
    public PdfColorSpace Space { get; private init; } = PdfColorSpace.Gray;
    public double[] Coords { get; private init; } = [];
    public double T0 { get; private init; }
    public double T1 { get; private init; } = 1;
    public PdfFunction? Function { get; private init; }
    public double[]? Background { get; private init; }

    public static PdfShading? Read(PdfFile file, IToken? token, DictionaryToken? resources)
    {
        var dict = file.Dict(token);
        if (dict is null) return null;
        int type = (int)file.GetNumber(dict, "ShadingType", 0);
        var domain = file.Numbers(file.Get(dict, "Domain")) ?? [0, 1];
        return new PdfShading
        {
            Type = type,
            Space = PdfColorSpace.Read(file, file.Get(dict, "ColorSpace"), resources),
            Coords = file.Numbers(file.Get(dict, "Coords")) ?? [],
            T0 = domain.Length >= 2 ? domain[0] : 0,
            T1 = domain.Length >= 2 ? domain[1] : 1,
            Function = PdfFunction.Read(file, file.Get(dict, "Function")),
            Background = file.Numbers(file.Get(dict, "Background")),
        };
    }

    public DocColor? ColorAt(double t)
    {
        double[] comps = Function is null ? Space.Initial : Function.Eval(t);
        return Space.ToColor(comps);
    }

    /// <summary>The color to use when the shading cannot be drawn as a gradient (its middle).</summary>
    public DocColor Fallback => ColorAt((T0 + T1) / 2) ?? DocColor.Black;

    public bool IsGradient => (Type == 2 && Coords.Length >= 4) || (Type == 3 && Coords.Length >= 6);

    /// <summary>
    /// The gradient for an object whose outline in document points has the bounds <paramref name="bounds"/>; <paramref name="toDoc"/> maps the
    /// shading's own space to document points. <paramref name="note"/> says what was approximated.
    /// </summary>
    public Paint ToPaint(Affine2 toDoc, Rect2 bounds, double alpha, out string? note)
    {
        if (Function is null)
        {
            note = null;
            return new SolidPaint(Fallback);
        }
        return GradientRamps.Build(Type, Coords, T0, T1, ColorAt, Function.Breakpoints(), toDoc, bounds, alpha, out note);
    }
}