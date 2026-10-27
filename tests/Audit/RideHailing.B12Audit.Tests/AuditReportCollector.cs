// B-W12-08: report writer. Tests append results immediately (before asserting)
// so a failed assertion still leaves the audited evidence on disk.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RideHailing.B12Audit.Tests;

public static class AuditEnvironment
{
    private static readonly Lazy<string> LazyRepoRoot = new(FindRepoRoot);
    private static readonly Lazy<string> LazyCommit = new(() =>
        Environment.GetEnvironmentVariable("B12_AUDIT_SOURCE_COMMIT") ?? Git("rev-parse HEAD"));
    private static readonly Lazy<string> LazyTree = new(() =>
        Environment.GetEnvironmentVariable("B12_AUDIT_SOURCE_TREE") ?? Git("rev-parse HEAD^{tree}"));
    private static readonly Lazy<string> LazyStatus = new(() =>
        Environment.GetEnvironmentVariable("B12_AUDIT_SOURCE_STATUS") ?? Git("status --porcelain"));

    public static string RepoRoot => LazyRepoRoot.Value;
    public static string SourceCommit => LazyCommit.Value;
    public static string SourceTree => LazyTree.Value;
    public static string SourceStatus => LazyStatus.Value;

    public static string OutputDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("B12_AUDIT_OUTPUT_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            return Path.Combine(RepoRoot, "evidence", "member-b", "w12-observability-g4", "auditor");
        }
    }

    private static string Git(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return "UNBOUND";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output : "UNBOUND";
        }
        catch
        {
            return "UNBOUND";
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RideHailing.Distributed.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

public sealed class AuditReportCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AuditRun> _runs = new(StringComparer.Ordinal);
    private readonly List<NegativeControlRecord> _controls = [];
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;

    public void AddRun(AuditRun run)
    {
        lock (_gate)
        {
            _runs[run.RunId] = run;
            WriteAll();
        }
    }

    public void AddNegativeControl(NegativeControlRecord control)
    {
        lock (_gate)
        {
            _controls.RemoveAll(c => string.Equals(c.ControlId, control.ControlId, StringComparison.Ordinal));
            _controls.Add(control);
            WriteAll();
        }
    }

    private void WriteAll()
    {
        Directory.CreateDirectory(AuditEnvironment.OutputDirectory);

        var runs = _runs.Values
            .OrderBy(r => r.ScenarioId, StringComparer.Ordinal)
            .ThenBy(r => r.RunId, StringComparer.Ordinal)
            .ToArray();
        var controls = _controls
            .OrderBy(c => c.ControlId, StringComparer.Ordinal)
            .ToArray();

        var summary = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PASS"] = 0,
            ["LOCAL_PROXY_PASS"] = 0,
            ["FAIL"] = 0,
            ["NOT_RUN"] = 0,
            ["BLOCKED"] = 0,
        };
        foreach (var check in runs.SelectMany(r => r.Checks))
        {
            var key = check.Status.ToString();
            summary[key] = summary.GetValueOrDefault(key) + 1;
        }

        // Negative/control runs intentionally produce FAIL (detections) and
        // BLOCKED (honesty) verdicts; keep them out of the positive-scenario
        // tally so a reader cannot mistake an expected detection for a defect.
        foreach (var group in new[] { "positive", "control" })
        {
            foreach (var status in summary.Keys.ToArray())
            {
                summary[$"{group}:{status}"] = 0;
            }
        }

        foreach (var run in runs)
        {
            var group = IsControlMode(run.ObservationMode) ? "control" : "positive";
            foreach (var check in run.Checks)
            {
                summary[$"{group}:{check.Status}"] += 1;
            }
        }

        var report = new AuditReport(
            SchemaVersion: 1,
            SourceCommit: AuditEnvironment.SourceCommit,
            SourceTree: AuditEnvironment.SourceTree,
            SourceStatus: AuditEnvironment.SourceStatus,
            GeneratedAt: DateTimeOffset.UtcNow,
            Environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runtime"] = Environment.Version.ToString(),
                ["os"] = Environment.OSVersion.ToString(),
                ["observationMode"] = "POST_SCENARIO_QUIESCED",
                ["driverAuthority"] = "real PostgreSQL 18.6 (Testcontainers, disposable)",
                ["matchingAuthority"] = "real PostgreSQL 18.6 (Testcontainers, disposable)",
                ["tripAuthority"] = "real PostgreSQL 18.6 (Testcontainers, disposable)",
                ["access"] = "read-only verification sessions",
            },
            Runs: runs,
            NegativeControls: controls,
            Summary: summary);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        File.WriteAllText(
            Path.Combine(AuditEnvironment.OutputDirectory, "auditor-results.json"),
            JsonSerializer.Serialize(report, options) + Environment.NewLine,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(AuditEnvironment.OutputDirectory, "auditor-results.md"),
            RenderResultsMarkdown(report),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(AuditEnvironment.OutputDirectory, "negative-controls.md"),
            RenderControlsMarkdown(report),
            new UTF8Encoding(false));
    }

    private static string RenderResultsMarkdown(AuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# B-W12-08 authoritative invariant auditor — results");
        sb.AppendLine();
        sb.AppendLine($"- Source commit: `{report.SourceCommit}`");
        sb.AppendLine($"- Source tree: `{report.SourceTree}`");
        sb.AppendLine($"- Generated: `{report.GeneratedAt:O}`");
        sb.AppendLine($"- Checks (all runs): PASS {report.Summary.GetValueOrDefault("PASS")}, " +
                      $"LOCAL_PROXY_PASS {report.Summary.GetValueOrDefault("LOCAL_PROXY_PASS")}, " +
                      $"FAIL {report.Summary.GetValueOrDefault("FAIL")}, " +
                      $"NOT RUN {report.Summary.GetValueOrDefault("NOT_RUN")}, " +
                      $"BLOCKED {report.Summary.GetValueOrDefault("BLOCKED")}");
        sb.AppendLine($"- Positive scenarios only: PASS {report.Summary.GetValueOrDefault("positive:PASS")}, " +
                      $"FAIL {report.Summary.GetValueOrDefault("positive:FAIL")}, " +
                      $"NOT RUN {report.Summary.GetValueOrDefault("positive:NOT_RUN")}, " +
                      $"BLOCKED {report.Summary.GetValueOrDefault("positive:BLOCKED")}");
        sb.AppendLine($"- Negative/control runs: FAIL {report.Summary.GetValueOrDefault("control:FAIL")} " +
                      "(expected detections), " +
                      $"BLOCKED {report.Summary.GetValueOrDefault("control:BLOCKED")} (expected honesty), " +
                      $"PASS {report.Summary.GetValueOrDefault("control:PASS")}, " +
                      $"NOT RUN {report.Summary.GetValueOrDefault("control:NOT_RUN")}");
        sb.AppendLine();
        sb.AppendLine("Statuses are final per check; `LOCAL_PROXY_PASS` is supporting evidence only");
        sb.AppendLine("and never a full PASS for the distributed invariant. `NOT RUN` records an");
        sb.AppendLine("owner dependency; `BLOCKED` means the required authority was unreachable.");
        sb.AppendLine();

        foreach (var run in report.Runs)
        {
            sb.AppendLine($"## Run `{run.RunId}` — scenario `{run.ScenarioId}`");
            sb.AppendLine();
            sb.AppendLine($"Observation: `{run.ObservationMode}`; elapsed {run.ElapsedTimeMs:F0} ms.");
            sb.AppendLine();
            sb.AppendLine("| Check | Status | Authority | Inspected | Finding codes | Limitation |");
            sb.AppendLine("| --- | --- | --- | ---: | --- | --- |");
            foreach (var check in run.Checks)
            {
                var codes = check.Findings.Count == 0
                    ? "—"
                    : string.Join(", ", check.Findings.Select(f => $"`{f.Code}`"));
                sb.AppendLine(
                    $"| {check.CheckId} — {Escape(check.Requirement)} | **{DisplayStatus(check.Status)}** | " +
                    $"{Escape(string.Join(" + ", check.AuthoritySources))} | {check.InspectedRecordCount} | " +
                    $"{codes} | {Escape(check.Limitation ?? "—")} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Finding detail");
        sb.AppendLine();
        var anyFinding = false;
        foreach (var run in report.Runs)
        {
            foreach (var check in run.Checks.Where(c => c.Findings.Count > 0))
            {
                anyFinding = true;
                sb.AppendLine($"### `{run.ScenarioId}` / {check.CheckId} — {DisplayStatus(check.Status)}");
                foreach (var finding in check.Findings)
                {
                    var record = string.IsNullOrWhiteSpace(finding.Record) ? "" : $" Record: {finding.Record}.";
                    sb.AppendLine($"- `{finding.Code}`: {finding.Message}{record}");
                }

                sb.AppendLine();
            }
        }

        if (!anyFinding)
        {
            sb.AppendLine("No findings. Clean scenarios still record inspected record counts above.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string RenderControlsMarkdown(AuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# B-W12-08 auditor negative controls");
        sb.AppendLine();
        sb.AppendLine("Controls prove the auditor is not vacuous. Corruption is confined to isolated");
        sb.AppendLine("disposable databases created for the control; ordinary scenario databases are");
        sb.AppendLine("never mutated. Where a PostgreSQL constraint makes a corrupt row impossible,");
        sb.AppendLine("the control records the enforced rejection instead of bypassing the guard.");
        sb.AppendLine();

        if (report.NegativeControls.Count == 0)
        {
            sb.AppendLine("_No controls recorded in this run._");
            sb.AppendLine();
            return sb.ToString();
        }

        sb.AppendLine("| Control | Target | Seeded condition | Expected | Observed | Isolation | Notes |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var control in report.NegativeControls)
        {
            sb.AppendLine(
                $"| `{control.ControlId}` | {Escape(control.TargetCheck)} | {Escape(control.SeededCondition)} | " +
                $"{Escape(control.Expected)} | **{Escape(DisplayText(control.Observed))}** | {Escape(control.Isolation)} | " +
                $"{Escape(control.Notes)} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string DisplayStatus(AuditStatus status) =>
        status.ToString().Replace("_", " ", StringComparison.Ordinal);

    private static string DisplayText(string value) =>
        value.Replace("NOT_RUN", "NOT RUN", StringComparison.Ordinal);

    private static bool IsControlMode(string observationMode) =>
        observationMode.Contains("CONTROL", StringComparison.OrdinalIgnoreCase)
        || observationMode.StartsWith("EMPTY_DATASET", StringComparison.OrdinalIgnoreCase);
}
