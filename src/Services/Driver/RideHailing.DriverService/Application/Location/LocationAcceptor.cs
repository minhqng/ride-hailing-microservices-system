// B-W03-06: monotonic accepted location per session (DSI-006).
// seq-first, capturedAt tie-break. Stale returns Rejected (API maps to 409), not throw:
// staleness is an expected concurrent outcome, not a bug.
using System.Collections.Concurrent;

namespace RideHailing.DriverService.Application.Location;

public enum LocationAcceptOutcome
{
    Accepted,
    Stale,
}

public sealed record LocationAcceptResult(LocationAcceptOutcome Outcome, LocationSample Current);

public sealed class LocationAcceptor
{
    private readonly ConcurrentDictionary<(Guid DriverId, string Session), (long Seq, DateTimeOffset CapturedAt, LocationSample Sample)> _latest = new();

    public LocationAcceptResult Accept(
        LocationSample sample,
        DateTimeOffset? now = null,
        LocationIngressOptions? options = null)
    {
        LocationValidator.Validate(sample, now, options);
        var key = (sample.DriverId, sample.DeviceSessionId);

        while (true)
        {
            if (!_latest.TryGetValue(key, out var current))
            {
                if (_latest.TryAdd(key, (sample.SequenceNo, sample.CapturedAt, sample)))
                {
                    return new LocationAcceptResult(LocationAcceptOutcome.Accepted, sample);
                }

                continue;
            }

            if (sample.SequenceNo > current.Seq ||
                (sample.SequenceNo == current.Seq && sample.CapturedAt > current.CapturedAt))
            {
                if (_latest.TryUpdate(key, (sample.SequenceNo, sample.CapturedAt, sample), current))
                {
                    return new LocationAcceptResult(LocationAcceptOutcome.Accepted, sample);
                }

                continue;
            }

            // Exact replay of the current sample (same seq, timestamp and
            // payload) is the same logical effect: canonical success
            // (DSR-002 idempotent replay), not a conflict.
            if (sample == current.Sample)
            {
                return new LocationAcceptResult(LocationAcceptOutcome.Accepted, current.Sample);
            }

            return new LocationAcceptResult(LocationAcceptOutcome.Stale, current.Sample);
        }
    }

    public LocationSample? GetLatest(Guid driverId, string session)
    {
        return _latest.TryGetValue((driverId, session), out var v) ? v.Sample : null;
    }
}
