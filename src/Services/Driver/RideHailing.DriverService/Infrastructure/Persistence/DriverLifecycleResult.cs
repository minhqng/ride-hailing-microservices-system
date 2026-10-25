namespace RideHailing.DriverService.Infrastructure.Persistence;

// Version and event identity refer to the original effect, even after later trips.
public sealed record DriverLifecycleResult(string Disposition, int? Version = null,
    string? EventMessageId = null, string? Reason = null);
