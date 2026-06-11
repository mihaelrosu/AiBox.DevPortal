using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchDiffPathNormalizerTests
{
    [Fact]
    public void Normalize_StandardGitDiffHeaders_RewritesAllPathHeaders()
    {
        const string diff = """
            diff --git a/source/File.cs b/source/File.cs
            index 1234567..89abcde 100644
            --- a/source/File.cs
            +++ b/source/File.cs
            @@ -1 +1 @@
            -old
            +new
            """;

        var normalized = PatchDiffPathNormalizer.Normalize(diff, "Components/File.cs", out var headers);

        Assert.StartsWith("diff --git a/Components/File.cs b/Components/File.cs", normalized, StringComparison.Ordinal);
        Assert.Contains("--- a/Components/File.cs", normalized, StringComparison.Ordinal);
        Assert.Contains("+++ b/Components/File.cs", normalized, StringComparison.Ordinal);
        var header = Assert.Single(headers);
        Assert.Equal("a/source/File.cs", header.ParsedOldPath);
        Assert.Equal("b/source/File.cs", header.ParsedNewPath);
    }

    [Fact]
    public void Normalize_NoIndexTemporaryPaths_DoesNotDuplicatePrefixesOrRelativePath()
    {
        const string diff = """
            diff --git a/tmp/work/original/Components/Pages/Coder.razor b/tmp/work/updated/Components/Pages/Coder.razor
            --- a/tmp/work/original/Components/Pages/Coder.razor
            +++ b/tmp/work/updated/Components/Pages/Coder.razor
            """;

        var normalized = PatchDiffPathNormalizer.Normalize(diff, "Components/Pages/Coder.razor", out _);

        Assert.Contains("diff --git a/Components/Pages/Coder.razor b/Components/Pages/Coder.razor", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("aa/", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("bb/", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("Coder.razorComponents", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/Components/Pages/Coder.razor")]
    [InlineData("b/Components/Pages/Coder.razor")]
    [InlineData("./Components/Pages/Coder.razor")]
    [InlineData("\\Components\\Pages\\Coder.razor")]
    public void NormalizeRelativePath_RemovesDiffPrefixesAndNormalizesSeparators(string path)
    {
        Assert.Equal("Components/Pages/Coder.razor", PatchDiffPathNormalizer.NormalizeRelativePath(path));
    }
}
