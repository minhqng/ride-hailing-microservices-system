namespace RideHailing.B12Audit.Tests;

/// <summary>
/// AUD-09: generated audit reports default to disposable TestResults output;
/// curated evidence updates require an explicit path.
/// </summary>
public sealed class AuditOutputIsolationTests
{
    [Fact]
    public void DefaultOutput_IsDisposableTestResults()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "b12-repo-default");

        var resolved = AuditEnvironment.ResolveOutputDirectory(null, repoRoot);

        Assert.Equal(Path.Combine(repoRoot, "TestResults", "b12-audit"), resolved);
        Assert.DoesNotContain(Path.Combine("evidence", "member-b"), resolved);
    }

    [Fact]
    public void ExplicitOutput_IsHonoredForCuratedEvidence()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "b12-repo-explicit");
        var curated = Path.Combine(Path.GetTempPath(), "b12-curated-" + Guid.NewGuid().ToString("N"));

        var resolved = AuditEnvironment.ResolveOutputDirectory(curated, repoRoot);

        Assert.Equal(Path.GetFullPath(curated), resolved);
    }
}
