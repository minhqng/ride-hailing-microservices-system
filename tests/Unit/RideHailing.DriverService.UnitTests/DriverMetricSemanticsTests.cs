// B-W12-01: label-domain tests. These are pure predicates, so they run without
// infrastructure and pin the bounded semantics used by the counters.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Telemetry;

namespace RideHailing.DriverService.UnitTests;

public sealed class DriverMetricSemanticsTests
{
    [Fact]
    public void StaleRelease_OnlyCoversTokenAndRoundFencing()
    {
        Assert.True(DriverMetrics.IsStaleReleaseBlocked(DriverErrors.StaleReservation));
        Assert.True(DriverMetrics.IsStaleReleaseBlocked("RESERVATION_ROUND_MISMATCH"));

        Assert.False(DriverMetrics.IsStaleReleaseBlocked("RELEASE_PROVENANCE_REQUIRED"));
        Assert.False(DriverMetrics.IsStaleReleaseBlocked("RESERVATION_ROUND_REQUIRED"));
        Assert.False(DriverMetrics.IsStaleReleaseBlocked(DriverErrors.IllegalTransition));
        Assert.False(DriverMetrics.IsStaleReleaseBlocked(null));
    }

    [Fact]
    public void ReasonClasses_AreBoundedAndNeverEchoRawCodes()
    {
        Assert.Equal("foreign_reservation",
            DriverMetrics.ReleaseReasonClass(DriverErrors.StaleReservation));
        Assert.Equal("round_mismatch",
            DriverMetrics.ReleaseReasonClass("RESERVATION_ROUND_MISMATCH"));
        Assert.Equal("domain_conflict",
            DriverMetrics.ReleaseReasonClass("SOMETHING_UNEXPECTED"));

        Assert.Equal("reservation_conflict",
            DriverMetrics.ReserveReasonClass(DriverErrors.DriverNotAvailable));
        Assert.Equal("version_fence",
            DriverMetrics.ReserveReasonClass(DriverErrors.StaleVersion));
        Assert.Equal("stale_identity",
            DriverMetrics.ReserveReasonClass(DriverErrors.StaleReservation));
        Assert.Equal("domain_conflict",
            DriverMetrics.ReserveReasonClass("SOMETHING_UNEXPECTED"));
    }
}
