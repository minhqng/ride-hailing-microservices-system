namespace RideHailing.InvariantAudit.Tests;

/// <summary>
/// Generated audit reports default to disposable TestResults output;
/// curated evidence updates require an explicit path.
/// </summary>
public sealed class AuditOutputIsolationTests
{
    [Fact]
    public void DefaultOutput_IsDisposableTestResults()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "audit-repo-default");

        var resolved = AuditEnvironment.ResolveOutputDirectory(null, repoRoot);

        Assert.Equal(Path.Combine(repoRoot, "TestResults", "invariant-audit"), resolved);
        Assert.DoesNotContain(Path.Combine("evidence", "release"), resolved);
    }

    [Fact]
    public void ExplicitOutput_IsHonoredForCuratedEvidence()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "audit-repo-explicit");
        var curated = Path.Combine(Path.GetTempPath(), "audit-curated-" + Guid.NewGuid().ToString("N"));

        var resolved = AuditEnvironment.ResolveOutputDirectory(curated, repoRoot);

        Assert.Equal(Path.GetFullPath(curated), resolved);
    }
}
