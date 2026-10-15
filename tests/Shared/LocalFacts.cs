using Xunit;

namespace DeSignMage.TestSupport;

/// <summary>A test that needs files from <c>local-testdata/</c>: skipped when they are missing.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LocalFactAttribute : FactAttribute
{
    public LocalFactAttribute(params string[] needs)
    {
        if (LocalData.Missing(needs) is { } why) Skip = why;
    }
}

/// <summary>A theory that needs local files; see <see cref="LocalFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LocalTheoryAttribute : TheoryAttribute
{
    public LocalTheoryAttribute(params string[] needs)
    {
        if (LocalData.Missing(needs) is { } why) Skip = why;
    }
}
