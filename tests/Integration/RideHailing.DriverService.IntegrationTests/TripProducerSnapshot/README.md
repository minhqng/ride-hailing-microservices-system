# A Release producer contract snapshot

The two C# files are byte-for-byte production sources from A PR #36 head
`5f61cff4fc7a6dd1c178c7ffac3d6709369f7bf3`:

- `src/Services/Trip/RideHailing.TripService/Integration/Messaging/TripOutboxIntents.cs`
- `src/Services/Trip/RideHailing.TripService/Integration/Matching/TripCommandContracts.cs`

They compile only into the B integration test assembly and execute the real
producer builder against Trip domain objects. A's branch is parallel and has
not been merged. This avoids rewriting its payload as anonymous B-authored JSON.
These sources are not a persisted Saga, nor a substitute for full REL-09.
When integrating A #36, remove this snapshot and use the production assembly's
types directly. Reverify source equality before updating the pinned revision.
