// B-W12-08 result contract (Doc 03 §11.4). Every check reports ONE status;
// proxy evidence never silently becomes a full PASS.
using System.Text.Json.Serialization;

namespace RideHailing.B12Audit.Tests;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditStatus
{
    PASS,
    LOCAL_PROXY_PASS,
    FAIL,
    NOT_RUN,
    BLOCKED,
}

public sealed record AuditFinding(string Code, string Message, string? Record = null);

public sealed record AuditCheckResult(
    string RunId,
    string SourceCommit,
    string ScenarioId,
    string ObservationMode,
    string CheckId,
    string Requirement,
    IReadOnlyList<string> AuthoritySources,
    long InspectedRecordCount,
    AuditStatus Status,
    IReadOnlyList<AuditFinding> Findings,
    double ElapsedTimeMs,
    string? Limitation = null);

public sealed record AuditRun(
    string RunId,
    string ScenarioId,
    string ObservationMode,
    string SourceCommit,
    string SourceTree,
    DateTimeOffset StartedAt,
    double ElapsedTimeMs,
    IReadOnlyList<AuditCheckResult> Checks);

public sealed record NegativeControlRecord(
    string ControlId,
    string TargetCheck,
    string SeededCondition,
    string Expected,
    string Observed,
    string Isolation,
    string Notes);

public sealed record AuditReport(
    int SchemaVersion,
    string SourceCommit,
    string SourceTree,
    string SourceStatus,
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<AuditRun> Runs,
    IReadOnlyList<NegativeControlRecord> NegativeControls,
    IReadOnlyDictionary<string, int> Summary);
