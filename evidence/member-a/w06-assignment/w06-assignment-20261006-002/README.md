# A-W06 assignment evidence — run w06-assignment-20261006-002

Claim:
Trip records at most one current Driver-reservation reference per ride
(DSI-008). Two concurrent writers for the same unassigned Trip generation
cannot both persist; the loser gets a typed concurrency conflict and the
authoritative row keeps exactly one assignment. Exact replays write nothing.
Same-round MatchingFailed cannot regress a reserved/accepted Trip. A late
reservation cannot reactivate a terminal MatchFailed Trip.

Setup:
Fresh disposable real PostgreSQL 18.6 per test (Testcontainers,
TripPostgresFixture). Each test creates its own ride, opens matching round 1,
then drives the Trip lifecycle handlers under test. Fixture driver/
reservation identities are authoritative-shaped, not live Driver authority
(B-W06 unavailable).

Race:
CompetingReservations test loads the same MATCHING row in two independent
DbContexts (versions asserted equal, both unassigned) before either save,
applies a different round-1 reservation in each, saves the first, and requires
the second save to raise TripConcurrencyConflictException.

Expected:
One Trip row; exactly one assignment (the first writer's driver,
reservation, round 1); CurrentMatchingRound 1; Version 2; replayed/loser
inputs change nothing (Version/UpdatedAt stable); terminal rows stay terminal.

Observed:
5/5 pass on real PostgreSQL, exit 0. Full Trip regression in the same
session: unit 252/252, integration 94/94 (includes these 5 and the
from-empty/upgrade/no-pending migration proofs).

Final-state assertion:
final-state/assertions.json records the reloaded-row oracle each test enforces
(test file lines cited there). All assertions run against fresh AsNoTracking
rows, never handler return values alone.

Limitations:
Trip-side OCC/single-assignment evidence only. NOT CON-01, NOT Driver
contention, NOT DOM-I1/Driver exclusivity: those need B-W06 ReserveDriver OCC
plus the CON-01 barrier harness on the Driver database (DEPENDENCY_PENDING).
No distributed trace exists for this local proof (see trace-reference.txt).
No contention metrics apply (see metrics/).

Files:
manifest.json (schema docs/03 §25.1; commit is the immutable code-under-test
revision fc7ad69, sourceState pins its tree and test bytes),
test-output.txt (curated W06 proof run), final-state/assertions.json,
trace-reference.txt, metrics/run-metrics.json.
Reproduce: from repository root with the code commit checked out and Docker
available for Testcontainers, run the command in test-output.txt (socket
flags mirror scripts/test/quick.sh; no workstation-specific GID is embedded).
Source identity (from repository root):
  git rev-parse <codeCommit>  (= manifest commit; the exercised revision)
  git show <codeCommit>:tests/Integration/RideHailing.TripService.IntegrationTests/TripW06AssignmentProofsTests.cs | sha256sum  (= sourceState.testSha256)
Line endings are pinned to LF by .gitattributes.
