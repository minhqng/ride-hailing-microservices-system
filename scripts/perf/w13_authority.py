"""Independent owning-database checks for the declared no-fleet API workload."""
import time

import w13lib as lib


def audit_no_fleet() -> dict:
    # E02 measures real public intake without a Driver fleet. Every accepted
    # ride must resolve to MATCH_FAILED through production Matching and broker
    # paths. Payment is expected to stay idle; G4 proves its exercised paths.
    deadline = time.monotonic() + 120
    while True:
        trips = lib.psql_json_rows("trip_db", "SELECT trip_id::text,state,assigned_driver_id::text,reservation_id::text FROM trips")
        rounds = lib.psql_json_rows("matching_db", "SELECT trip_id::text,matching_round,status FROM matching_rounds")
        pending = {database: int(lib.psql_scalar(database, "SELECT count(*) FROM outbox_messages WHERE status <> 'PUBLISHED'"))
                   for database in ("trip_db", "matching_db", "driver_db", "payment_db")}
        cleanup = int(lib.psql_scalar("trip_db", "SELECT count(*) FROM trip_cleanup_intents WHERE completed_at IS NULL"))
        if (trips and all(trip["state"] == "MATCH_FAILED" for trip in trips)
                and all(item["status"] == "NO_DRIVER" for item in rounds)
                and not any(pending.values()) and cleanup == 0):
            break
        if time.monotonic() >= deadline:
            break
        time.sleep(2)
    drivers = int(lib.psql_scalar("driver_db", "SELECT count(*) FROM drivers"))
    sagas = int(lib.psql_scalar("trip_db", "SELECT count(*) FROM ride_sagas"))
    payments = int(lib.psql_scalar("payment_db", "SELECT count(*) FROM payment_operations"))
    effects = int(lib.psql_scalar("payment_db", "SELECT count(*) FROM provider_effects"))
    checks = {
        "nonemptyMeasuredIntake": bool(trips),
        "allTripsTerminalNoDriver": all(trip["state"] == "MATCH_FAILED" for trip in trips),
        "noAssignmentWithoutFleet": drivers == 0 and all(trip["assigned_driver_id"] is None and trip["reservation_id"] is None for trip in trips),
        "oneTerminalRoundPerTrip": len(rounds) == len(trips) == len({item["trip_id"] for item in rounds}),
        "allRoundsNoDriver": all(item["status"] == "NO_DRIVER" for item in rounds),
        "noSpuriousSagaOrPaymentEffect": sagas == payments == effects == 0,
        "allOutboxesDrained": not any(pending.values()),
        "noUnresolvedCleanup": cleanup == 0,
    }
    return {"status": "PASS" if all(checks.values()) else "FAIL", "checks": checks,
            "proofBoundary": "Independent read-only owning-database oracle after real no-fleet public intake; not an exercised Payment/Saga throughput claim",
            "tripCountIncludingPreflight": len(trips), "roundCount": len(rounds), "driverCount": drivers,
            "sagaCount": sagas, "paymentCount": payments, "providerEffectCount": effects,
            "pendingOutboxes": pending, "pendingCleanup": cleanup}
