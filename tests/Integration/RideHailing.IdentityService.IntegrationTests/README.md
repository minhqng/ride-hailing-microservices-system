# SEC-01–04 handoff

AuthNegativeTests is an executable skeleton, not evidence of production authentication. Without RIDE_AUTH_FIXTURE xUnit explicitly skips the theory. A/B supply a local JSON fixture with baseUrl, controlPath, controlTokenEnvironment, databaseEnvironment, snapshotSql and cases.

Cases: missing-token, invalid-signature, expired-token, malformed-token (401); wrong-role, foreign-object, spoofed-actor (403). Each case contains method, path, body and tokenEnvironment (null only for missing-token). Environment variables contain actual secrets; never commit those values.

ControlPath must be a real authenticated business read returning 200. SnapshotSql must return deterministic authoritative fixture state (including row counts and values), not a constant. It executes read-only. Negative requests must otherwise be valid. Run on isolated data without concurrent writers. A/B must confirm whether foreign-object concealment uses 404, or spoofed actor is ignored in favor of authenticated identity, before adjusting those assertions. C does not redefine owner security policy.
