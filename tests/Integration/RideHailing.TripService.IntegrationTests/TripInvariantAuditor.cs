// A-W12-06 Trip-side invariant auditor (Doc 03 §11). TEST-SCOPE VERIFICATION
// TOOLING, not application business code: read-only SQL over the Trip-owned
// database in disposable test/fault environments.
//
// HONESTY CONTRACT. A check the Trip database cannot authoritatively answer
// is reported NOT RUN with its owner/dependency — never silently PASS. A
// Trip-only dataset cannot prove Driver exclusivity (AUD-01/02/04) or the
// Payment side of operation-key uniqueness (AUD-05) and matching-terminal
// uniqueness (AUD-06); those Trip-side PROXIES are labelled as proxies and
// the full properties stay NOT RUN until cross-service read access exists.

using Npgsql;

namespace RideHailing.TripService.IntegrationTests;

public enum AuditStatus
{
    /// <summary>Level A: the authoritative owner database of the invariant was inspected.</summary>
    Pass,

    /// <summary>
    /// Level B: a Trip-local condition supports a distributed invariant but does
    /// not prove the other service's authoritative final state. Supporting
    /// evidence only — never counts as a full G4 PASS for the property.
    /// </summary>
    LocalProxyPass,

    Fail,

    NotRun,
}

public sealed record AuditorFinding(string CheckId, string Detail);

public sealed record AuditorCheck(string Id, string Title, AuditStatus Status, string Note,
    IReadOnlyList<AuditorFinding> Findings)
{
    public static AuditorCheck Pass(string id, string title, string note = "") =>
        new(id, title, AuditStatus.Pass, note, []);

    public static AuditorCheck LocalProxyPass(string id, string title, string note) =>
        new(id, title, AuditStatus.LocalProxyPass, note, []);

    public static AuditorCheck Fail(string id, string title, string note, IReadOnlyList<AuditorFinding> findings) =>
        new(id, title, AuditStatus.Fail, note, findings);

    public static AuditorCheck NotRun(string id, string title, string note) =>
        new(id, title, AuditStatus.NotRun, note, []);
}

/// <summary>Scenario-dependent acceptance bounds and observation mode.</summary>
public sealed record AuditorPolicy(
    TimeSpan MaxUnresolvedOutboxAge,
    TimeSpan MaxNonTerminalSagaAge,
    bool Quiesced)
{
    public static AuditorPolicy QuiescedDefaults { get; } = new(
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), Quiesced: true);
}

public sealed record AuditorReport(
    string RunId, string Commit, DateTimeOffset ExecutedAtUtc,
    IReadOnlyList<AuditorCheck> Checks)
{
    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Run: {RunId}",
            $"Commit: {Commit}",
            $"ExecutedAtUtc: {ExecutedAtUtc:O}",
            "Legend: PASS = authoritative owner database inspected (Level A);",
            "       LOCAL_PROXY_PASS = Trip-local supporting proof only (Level B),",
            "       never a full G4 pass for the distributed property;",
            "       NOT RUN = required proof not executed (owner/dependency recorded).",
        };
        foreach (var check in Checks)
        {
            var verdict = check.Status switch
            {
                AuditStatus.Pass => "PASS",
                AuditStatus.LocalProxyPass => "LOCAL_PROXY_PASS",
                AuditStatus.Fail => "FAIL",
                _ => "NOT RUN",
            };
            lines.Add($"{check.Id} {check.Title.PadRight(38)} {verdict}");
            foreach (var finding in check.Findings)
                lines.Add($"    ! {finding.Detail}");
            if (check.Status != AuditStatus.Pass && check.Note.Length > 0)
                lines.Add($"    - {check.Note}");
        }

        var overall = Checks.Any(c => c.Status == AuditStatus.Fail)
            ? "FAIL"
            : Checks.All(c => c.Status == AuditStatus.Pass) ? "PASS" : "PARTIAL (NOT RUN/LOCAL_PROXY present)";
        lines.Add($"Overall: {overall}");
        return string.Join(Environment.NewLine, lines);
    }
}

public static class TripInvariantAuditor
{
    public const string CommitPlaceholder = "<source-revision>";

    public static async Task<AuditorReport> AuditAsync(
        string connectionString, AuditorPolicy policy, string runId, string commit,
        CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);

        var checks = new List<AuditorCheck>
        {
            AuditDriverExclusiveReservation(),
            AuditDriverStateCoherence(),
            await AuditSingleTripAssignmentAsync(connection, token),
            AuditDriverTripAgreement(),
            await AuditPaymentUniquenessProxyAsync(connection, token),
            await AuditMatchingTerminalProxyAsync(connection, token),
            await AuditLeakedReservationAsync(connection, policy, token),
            await AuditUnresolvedAgeAsync(connection, policy, token),
        };
        return new AuditorReport(runId, commit, DateTimeOffset.UtcNow, checks);
    }

    // AUD-01/02/04: the Trip database cannot answer Driver-owned invariants.
    private static AuditorCheck AuditDriverExclusiveReservation() =>
        AuditorCheck.NotRun("AUD-01", "Exclusive Driver reservation",
            "Requires the Driver-owned database (Member B); cross-service read access is not configured in A's test environment.");

    private static AuditorCheck AuditDriverStateCoherence() =>
        AuditorCheck.NotRun("AUD-02", "Driver state coherence",
            "Requires the Driver-owned database (Member B); cross-service read access is not configured in A's test environment.");

    private static AuditorCheck AuditDriverTripAgreement() =>
        AuditorCheck.NotRun("AUD-04", "Driver/Trip agreement",
            "Requires Driver current reservation state (Member B); cross-service read access is not configured in A's test environment.");

    /// <summary>
    /// AUD-03 Trip-side executable form: the Trip holds at most one current
    /// assignment reference. Extra active references appear as OPEN
    /// (unconfirmed) release intents that point at a reservation other than
    /// the current assignment, or at more than one distinct reservation.
    /// </summary>
    private static async Task<AuditorCheck> AuditSingleTripAssignmentAsync(NpgsqlConnection connection, CancellationToken token)
    {
        var sql = """
            SELECT c.trip_id, c.key, c.reservation_id
            FROM trip_cleanup_intents c
            JOIN trips t ON t.trip_id = c.trip_id
            WHERE c.kind = 'ReleaseReservation'
              AND c.completed_at IS NULL
              AND (
                t.assigned_driver_id IS NULL
                OR t.reservation_id IS DISTINCT FROM c.reservation_id
                OR EXISTS (
                    SELECT 1 FROM trip_cleanup_intents c2
                    WHERE c2.trip_id = c.trip_id AND c2.kind = 'ReleaseReservation'
                      AND c2.completed_at IS NULL
                      AND c2.reservation_id IS DISTINCT FROM c.reservation_id)
              )
            """;
        var findings = new List<AuditorFinding>();
        await using (var command = new NpgsqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                findings.Add(new AuditorFinding("AUD-03",
                    $"Open release intent {reader.GetString(1)} on trip {reader.GetGuid(0)} references reservation " +
                    $"{(reader.IsDBNull(2) ? "<null>" : reader.GetGuid(2))} which is not the single current assignment."));
        }

        return findings.Count == 0
            ? AuditorCheck.Pass("AUD-03", "Single current Trip assignment",
                "Level A: the assignment triple and release obligations are Trip-authoritative rows (trips + trip_cleanup_intents).")
            : AuditorCheck.Fail("AUD-03", "Single current Trip assignment",
                $"{findings.Count} trip(s) hold conflicting active assignment references.", findings);
    }

    /// <summary>
    /// AUD-05 Trip-side PROXY: saga operation keys are globally unique and
    /// follow the frozen per-trip lineage. The Payment-side one-logical-effect
    /// half of AUD-05 stays NOT RUN (C-owned data). Both halves are also
    /// structurally enforced on this database (UNIQUE trip_id plus the
    /// operation-lineage CHECK); the auditor re-verifies read-only.
    /// </summary>
    private static async Task<AuditorCheck> AuditPaymentUniquenessProxyAsync(NpgsqlConnection connection, CancellationToken token)
    {
        var findings = new List<AuditorFinding>();
        var duplicateSql = """
            SELECT key, COUNT(*)::int FROM (
                SELECT authorize_operation_key AS key FROM ride_sagas WHERE authorize_operation_key IS NOT NULL
                UNION ALL
                SELECT capture_operation_key FROM ride_sagas WHERE capture_operation_key IS NOT NULL
                UNION ALL
                SELECT void_operation_key FROM ride_sagas WHERE void_operation_key IS NOT NULL
            ) keys GROUP BY key HAVING COUNT(*) > 1
            """;
        await using (var command = new NpgsqlCommand(duplicateSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                findings.Add(new AuditorFinding("AUD-05",
                    $"Operation key '{reader.GetString(0)}' is used by {reader.GetInt32(1)} sagas."));
        }

        var lineageSql = """
            SELECT trip_id, authorize_operation_key FROM ride_sagas
            WHERE authorize_operation_key IS NOT NULL
              AND authorize_operation_key <> trip_id::text || ':authorize:v1'
            """;
        await using (var command = new NpgsqlCommand(lineageSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                findings.Add(new AuditorFinding("AUD-05",
                    $"Saga for trip {reader.GetGuid(0)} carries off-lineage authorize key '{reader.GetString(1)}'."));
        }

        return findings.Count == 0
            ? AuditorCheck.LocalProxyPass("AUD-05", "Payment logical uniqueness (Trip-side proxy)",
                "Level B: Trip-side obligation ledger only — saga operation keys unique + frozen lineage (also DB-CHECK-enforced), re-verified read-only. The authoritative Payment effect ledger (one logical effect per key) is NOT inspected: full AUD-05 requires C-owned data (DEPENDENCY_PENDING).")
            : AuditorCheck.Fail("AUD-05", "Payment logical uniqueness (Trip-side proxy)",
                $"{findings.Count} operation-key violation(s) in the Trip-owned ledger.", findings);
    }

    /// <summary>
    /// AUD-06 Trip-side PROXY: no trip has both an applied MatchingFailedV1
    /// and an applied DriverReservedV1 for the same matching round. The
    /// Matching-ledger half stays NOT RUN (B-owned data).
    /// </summary>
    private static async Task<AuditorCheck> AuditMatchingTerminalProxyAsync(NpgsqlConnection connection, CancellationToken token)
    {
        var sql = """
            SELECT r1.trip_id, COALESCE(r1.envelope_json->'payload'->>'matchingRound', '?') AS round
            FROM trip_message_receipts r1
            WHERE r1.message_type = 'MatchingFailedV1' AND r1.disposition = 'Applied'
              AND EXISTS (
                SELECT 1 FROM trip_message_receipts r2
                WHERE r2.trip_id = r1.trip_id AND r2.message_type = 'DriverReservedV1'
                  AND r2.disposition = 'Applied'
                  AND COALESCE(r2.envelope_json->'payload'->>'matchingRound', '?')
                      = COALESCE(r1.envelope_json->'payload'->>'matchingRound', '?'))
            """;
        var findings = new List<AuditorFinding>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            findings.Add(new AuditorFinding("AUD-06",
                $"Trip {reader.GetGuid(0)} round {reader.GetString(1)} has applied BOTH DriverReserved and MatchingFailed terminal facts."));

        return findings.Count == 0
            ? AuditorCheck.LocalProxyPass("AUD-06", "Matching terminal uniqueness (Trip-side proxy)",
                "Level B: Trip's applied-fact ledger shows no contradictory applied terminal facts per (trip, round). The authoritative Matching ledger is NOT inspected: full AUD-06 requires B-owned data (DEPENDENCY_PENDING).")
            : AuditorCheck.Fail("AUD-06", "Matching terminal uniqueness (Trip-side proxy)",
                $"{findings.Count} contradictory terminal fact pair(s) in the Trip-owned ledger.", findings);
    }

    /// <summary>
    /// AUD-07: after scenario quiescence, a terminal pre-start Trip must not
    /// still hold a Driver reservation. Assigned terminal trips must carry a
    /// ReleaseReservation obligation; a terminal trip with a release
    /// obligation (including a DS03 rejection, which clears the Trip-side
    /// assignment but preserves the exact obligation) must have it completed
    /// when the scenario is quiesced.
    /// </summary>
    private static async Task<AuditorCheck> AuditLeakedReservationAsync(
        NpgsqlConnection connection, AuditorPolicy policy, CancellationToken token)
    {
        var sql = """
            SELECT t.trip_id,
                   COALESCE(t.reservation_id, (
                       SELECT c.reservation_id FROM trip_cleanup_intents c
                       WHERE c.trip_id = t.trip_id AND c.kind = 'ReleaseReservation'
                       ORDER BY c.created_at LIMIT 1)) AS reservation_id,
                   (SELECT COUNT(*)::int FROM trip_cleanup_intents c
                    WHERE c.trip_id = t.trip_id AND c.kind = 'ReleaseReservation') AS release_intents,
                   (SELECT COUNT(*)::int FROM trip_cleanup_intents c
                    WHERE c.trip_id = t.trip_id AND c.kind = 'ReleaseReservation' AND c.completed_at IS NULL) AS open_intents
            FROM trips t
            WHERE t.state IN ('CANCELLED', 'MATCH_FAILED')
              AND (t.assigned_driver_id IS NOT NULL OR EXISTS (
                  SELECT 1 FROM trip_cleanup_intents c
                  WHERE c.trip_id = t.trip_id AND c.kind = 'ReleaseReservation'))
            """;
        var findings = new List<AuditorFinding>();
        var pending = 0;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<(Guid TripId, Guid Reservation, int Releases, int Open)>();
        while (await reader.ReadAsync(token))
            rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3)));

        foreach (var row in rows)
        {
            if (row.Releases == 0)
            {
                findings.Add(new AuditorFinding("AUD-07",
                    $"Terminal trip {row.TripId} still holds reservation {row.Reservation} with NO release obligation: leaked pre-start reservation."));
            }
            else if (row.Open > 0 && policy.Quiesced)
            {
                findings.Add(new AuditorFinding("AUD-07",
                    $"Terminal trip {row.TripId} holds reservation {row.Reservation} whose release obligation is still open after quiescence."));
            }
            else if (row.Open > 0)
            {
                pending++;
            }
        }

        var note = policy.Quiesced
            ? "Quiesced scenario: open release obligations on terminal trips are failures."
            : $"Mid-flight observation: {pending} recognized-pending release obligation(s) on terminal trips.";
        // Level B: Trip proves its obligation ledger is coherent and that
        // Driver AUTHORITY confirmed each release (completed_at is set only by
        // DriverReservationReleasedV1 intake), but the canonical property also
        // asserts the Driver's own reservation state — B-owned data.
        return findings.Count == 0
            ? AuditorCheck.LocalProxyPass("AUD-07", "No leaked pre-start reservation", note +
                " Level B: Trip ledger + Driver-confirmed releases; full AUD-07 additionally requires the Driver reservation table (B-owned, DEPENDENCY_PENDING).")
            : AuditorCheck.Fail("AUD-07", "No leaked pre-start reservation", note, findings);
    }

    /// <summary>
    /// AUD-08: nothing is silently stuck — pending/failed outbox rows and
    /// non-terminal sagas must resolve inside the scenario's accepted bound.
    /// </summary>
    private static async Task<AuditorCheck> AuditUnresolvedAgeAsync(
        NpgsqlConnection connection, AuditorPolicy policy, CancellationToken token)
    {
        var findings = new List<AuditorFinding>();
        await using (var command = new NpgsqlCommand("""
            SELECT message_id, EXTRACT(EPOCH FROM (now() - occurred_at))::bigint
            FROM outbox_messages
            WHERE status = 'PENDING' AND occurred_at < now() - (@maxAge || ' seconds')::interval
            """, connection))
        {
            command.Parameters.AddWithValue("maxAge", (long)policy.MaxUnresolvedOutboxAge.TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                findings.Add(new AuditorFinding("AUD-08",
                    $"Outbox row {reader.GetString(0)} pending for {reader.GetInt64(1)}s (> {policy.MaxUnresolvedOutboxAge.TotalSeconds}s)."));
        }

        await using (var command = new NpgsqlCommand(
            "SELECT message_id FROM outbox_messages WHERE status = 'FAILED_VISIBLE'", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                findings.Add(new AuditorFinding("AUD-08",
                    $"Outbox row {reader.GetString(0)} is in visible terminal failure (FAILED_VISIBLE)."));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT trip_id, status, EXTRACT(EPOCH FROM (now() - updated_at))::bigint
            FROM ride_sagas
            WHERE status IN ('ACTIVE', 'COMPENSATING', 'REQUIRES_REVIEW')
              AND updated_at < now() - (@maxAge || ' seconds')::interval
            """, connection))
        {
            command.Parameters.AddWithValue("maxAge", (long)policy.MaxNonTerminalSagaAge.TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                findings.Add(new AuditorFinding("AUD-08",
                    $"Saga for trip {reader.GetGuid(0)} is {reader.GetString(1)} for {reader.GetInt64(2)}s (> {policy.MaxNonTerminalSagaAge.TotalSeconds}s) without resolution."));
        }

        return findings.Count == 0
            ? AuditorCheck.Pass("AUD-08", "Outbox/Saga resolution within policy",
                "Level A: outbox_messages and ride_sagas are Trip-owned tables.")
            : AuditorCheck.Fail("AUD-08", "Outbox/Saga resolution within policy",
                $"{findings.Count} unresolved row(s) beyond policy.", findings);
    }
}
