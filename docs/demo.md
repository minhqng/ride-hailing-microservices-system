# Team demonstration — 7–10 minutes

This runbook demonstrates the DS03 ride-hailing system on a clean checkout. It
covers the course's required sequence: independent services, an available
driver, ride creation, matching, acceptance, completion, two passengers
competing for one driver, a service stop during a workflow, message
retry/recovery and restart. Every step is observable through HTTP, PostgreSQL
and RabbitMQ; no mocked business state is presented as proof.

## 0. Prepare (once)

```bash
git clone https://github.com/minhqng/ride-hailing-microservices-system.git
cd ride-hailing-microservices-system
cp .env.example .env
python3 scripts/security/init-local-auth.py
```

## 1. Start services independently

```bash
docker compose up -d --build
docker compose ps
```

Expected: `postgres`, `redis`, `rabbitmq`, `gateway`, `identity`, `driver`,
`trip`, `matching`, `payment` and `notification` are running. Check readiness:

```bash
for port in 8080 8101 8102 8103 8104 8105 8106; do
  curl -fsS "http://localhost:${port}/health/ready" && echo
done
```

RabbitMQ management UI: <http://localhost:15672> (`ride_app` / the development
password from `.env`). Open **Queues** and keep it visible for the demo.

## 2. Register and authenticate a Driver and a Rider

```bash
API=http://localhost:8080
DRIVER_USER=$(curl -fsS -X POST "$API/auth/register" -H 'Content-Type: application/json' \
  -d '{"username":"demo-driver","password":"demo-password-1","role":"Driver"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["userId"])')
DRIVER_TOKEN=$(curl -fsS -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"username":"demo-driver","password":"demo-password-1"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')
RIDER_TOKEN=$(curl -fsS -X POST "$API/auth/register" -H 'Content-Type: application/json' \
  -d '{"username":"demo-rider","password":"demo-password-1","role":"Rider"}' >/dev/null && \
  curl -fsS -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"username":"demo-rider","password":"demo-password-1"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')
```

## 3. Make the Driver available

```bash
DRIVER=$(curl -fsS -X POST "$API/drivers" -H "Authorization: Bearer $DRIVER_TOKEN" \
  -H 'Content-Type: application/json' -d "{\"userId\":\"$DRIVER_USER\"}")
DRIVER_ID=$(echo "$DRIVER" | python3 -c 'import json,sys;print(json.load(sys.stdin)["driverId"])')
VERSION=$(echo "$DRIVER" | python3 -c 'import json,sys;print(json.load(sys.stdin)["version"])')
curl -fsS -X POST "$API/drivers/$DRIVER_ID/online" -H "Authorization: Bearer $DRIVER_TOKEN" \
  -H 'Content-Type: application/json' -d "{\"expectedVersion\":$VERSION}"
curl -fsS -X POST "$API/drivers/$DRIVER_ID/location" -H "Authorization: Bearer $DRIVER_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"deviceSessionId":"demo-session","sequenceNo":1,"capturedAt":"2026-11-06T08:00:00Z","latitude":10.7769,"longitude":106.7009}'
```

Expected: the Driver is `AVAILABLE`; the Redis GEO projection contains the
location. The Matching service consumes the Driver event asynchronously.

## 4. Create a ride and observe matching

```bash
TRIP=$(curl -fsS -X POST "$API/trips" -H "Authorization: Bearer $RIDER_TOKEN" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(python3 -c 'import uuid;print(uuid.uuid4())')" \
  -d '{"pickup":{"latitude":10.7769,"longitude":106.7009},"dropoff":{"latitude":10.78,"longitude":106.71}}')
TRIP_ID=$(echo "$TRIP" | python3 -c 'import json,sys;print(json.load(sys.stdin)["tripId"])')
curl -fsS "$API/trips/$TRIP_ID" -H "Authorization: Bearer $RIDER_TOKEN"
```

Expected within a few seconds: `status` becomes `DRIVER_RESERVED`;
`assignedDriverId` and `reservationId` are present. In RabbitMQ you can show
`RideRequestedV1` flowing Trip → Matching → Driver. In PostgreSQL, the Driver
row is `RESERVED` with `reserved_trip_id` and the Matching round is `RESERVED`
with the same reservation ID.

## 5. Driver accepts, starts and completes the ride

```bash
ASSIGNED=$(curl -fsS "$API/trips/$TRIP_ID" -H "Authorization: Bearer $RIDER_TOKEN")
RESERVATION=$(echo "$ASSIGNED" | python3 -c 'import json,sys;print(json.load(sys.stdin)["reservationId"])')
BODY="{\"driverId\":\"$DRIVER_ID\",\"reservationId\":\"$RESERVATION\"}"
curl -fsS -X POST "$API/trips/$TRIP_ID/accept"   -H "Authorization: Bearer $DRIVER_TOKEN" -H 'Content-Type: application/json' -d "$BODY"
curl -fsS -X POST "$API/trips/$TRIP_ID/start"    -H "Authorization: Bearer $DRIVER_TOKEN" -H 'Content-Type: application/json' -d "$BODY"
curl -fsS -X POST "$API/trips/$TRIP_ID/complete" -H "Authorization: Bearer $DRIVER_TOKEN" -H 'Content-Type: application/json' -d "$BODY"
curl -fsS "$API/trips/$TRIP_ID" -H "Authorization: Bearer $RIDER_TOKEN"
```

Expected: `READY_TO_START` → `IN_PROGRESS` → `COMPLETED`; the Saga row is
`COMPLETED`; Payment has one `AUTHORIZE` and one `CAPTURE` operation, both
`SUCCEEDED`; the Driver returns to `AVAILABLE` with no reservation.

Show notifications:

```bash
curl -fsS "$API/notifications/me" -H "Authorization: Bearer $RIDER_TOKEN"
curl -fsS "$API/notifications/me" -H "Authorization: Bearer $DRIVER_TOKEN"
```

Expected: rider inbox has `RIDER_RIDE_REQUESTED`, `RIDER_DRIVER_ASSIGNED` and
`RIDER_TRIP_COMPLETED`; driver inbox has `DRIVER_TRIP_ASSIGNED`.

## 6. Stop a service during a workflow (recovery drill)

Create a fresh ride while the Driver is `AVAILABLE`, then stop Trip immediately
after the ride is created and before the Driver acts:

```bash
TRIP_ID=$(curl -fsS -X POST "$API/trips" -H "Authorization: Bearer $RIDER_TOKEN" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(python3 -c 'import uuid;print(uuid.uuid4())')" \
  -d '{"pickup":{"latitude":10.7769,"longitude":106.7009},"dropoff":{"latitude":10.78,"longitude":106.71}}' \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["tripId"])')
docker compose stop trip
docker compose exec -T postgres psql -U postgres -d trip_db -c \
  "select message_type, status from outbox_messages order by created_at desc limit 3;"
```

Expected: the ride row exists and its `RideRequestedV1` Outbox intent is
`PENDING` — the business commit and the message intent are atomic. No message
was invented by the broker.

```bash
docker compose start trip
sleep 20
curl -fsS "$API/trips/$TRIP_ID" -H "Authorization: Bearer $RIDER_TOKEN"
docker compose exec -T postgres psql -U postgres -d trip_db -c \
  "select message_type, status, attempts from outbox_messages order by created_at desc limit 3;"
```

Expected: the intent is `PUBLISHED` and the ride reaches `DRIVER_RESERVED`
after the relay reconnects. This is durable-intent recovery after a process
restart, not a replayed mock.

Release that reservation before the next step:

```bash
ASSIGNED=$(curl -fsS "$API/trips/$TRIP_ID" -H "Authorization: Bearer $RIDER_TOKEN")
RESERVATION=$(echo "$ASSIGNED" | python3 -c 'import json,sys;print(json.load(sys.stdin)["reservationId"])')
curl -fsS -X POST "$API/trips/$TRIP_ID/accept" -H "Authorization: Bearer $DRIVER_TOKEN" \
  -H 'Content-Type: application/json' -d "{\"driverId\":\"$DRIVER_ID\",\"reservationId\":\"$RESERVATION\"}"
curl -fsS -X POST "$API/trips/$TRIP_ID/cancel" -H "Authorization: Bearer $RIDER_TOKEN" \
  -H 'Content-Type: application/json' -d '{}'
```

Expected: the Trip is `CANCELLED`, the Saga is `COMPENSATED`, Payment has an
`AUTHORIZE` and a `VOID`, and the Driver is `AVAILABLE` again.

For the faster automated equivalent, run the Production release smoke:

```bash
python3 scripts/test/release-smoke.py
```

It executes Scenarios A–D (driver reject, completed ride with inboxes,
notification-worker outage with exactly-once delivery after restart, and
reject-commit crash/replay), applies the real migrations, asserts the
authorities and tears down only its own isolated Compose project. The result
is written to `plans/reports/release-smoke.json`.

## 7. Two passengers compete for the same Driver

Create a second Rider, then submit one ride from each Rider without waiting:

```bash
RIDER2_TOKEN=$(curl -fsS -X POST "$API/auth/register" -H 'Content-Type: application/json' \
  -d '{"username":"demo-rider-2","password":"demo-password-1","role":"Rider"}' >/dev/null && \
  curl -fsS -X POST "$API/auth/login" -H 'Content-Type: application/json' \
  -d '{"username":"demo-rider-2","password":"demo-password-1"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')

# Fire both ride requests; one wins the single AVAILABLE Driver.
for token in "$RIDER_TOKEN" "$RIDER2_TOKEN"; do
  curl -fsS -X POST "$API/trips" -H "Authorization: Bearer $token" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: $(python3 -c 'import uuid;print(uuid.uuid4())')" \
    -d '{"pickup":{"latitude":10.7769,"longitude":106.7009},"dropoff":{"latitude":10.78,"longitude":106.71}}' &
done
wait
```

Read both Trips after a few seconds:

```bash
docker compose exec -T postgres psql -U postgres -d trip_db -c \
  "select trip_id, state, failure_reason from trips order by created_at desc limit 2;"
```

Expected: exactly one Trip is `DRIVER_RESERVED`; the other finishes
`MATCH_FAILED` with `failure_reason = NO_DRIVER_AVAILABLE`. The Driver
authority contains exactly one reservation. This is the single-assignment
invariant, backed by the PostgreSQL OCC reservation (Redis never grants it).

## 8. Close the demo

```bash
docker compose down          # keep volumes
# docker compose down -v     # only when deliberately deleting demo data
```

## What the audience should take away

- The Driver PostgreSQL row is the only reservation authority; Redis is a
  derived index and cannot assign a Driver.
- Business state and message intent commit atomically (Outbox); transport is
  at-least-once with idempotent consumers, not an exactly-once claim.
- Lost responses remain `UNKNOWN` and are reconciled with the same identity.
- A stopped service does not lose committed work; restart resumes it.
- Notifications are asynchronous projections and cannot block ride truth.
