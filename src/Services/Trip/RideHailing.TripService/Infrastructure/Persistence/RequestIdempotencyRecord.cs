// Request-idempotency persistence type (A-W04-02).
//
// Durable local request-effect bookkeeping, NOT a Trip domain entity: it
// records that one (scope,key) logical request already produced one Trip
// (ResourceId) for one request meaning (RequestFingerprint). It lives in
// Infrastructure/Persistence, never in BuildingBlocks, and neither the key
// nor the fingerprint enters the Trip aggregate.

namespace RideHailing.TripService.Infrastructure.Persistence;

/// <summary>
/// Persisted effect of one Rider-scoped idempotent CreateRide request.
/// Identity is the composite (Scope, Key).
/// </summary>
public sealed class RequestIdempotencyRecord
{
    /// <summary>Rider-scoped namespace, e.g. "trip:create:<rider-guid-D>".</summary>
    public string Scope { get; private set; } = null!;

    /// <summary>Opaque exact client-supplied Idempotency-Key.</summary>
    public string Key { get; private set; } = null!;

    /// <summary>Semantic request fingerprint (64 lowercase hex SHA-256).</summary>
    public string RequestFingerprint { get; private set; } = null!;

    /// <summary>The canonical Trip produced for this logical request.</summary>
    public Guid ResourceId { get; private set; }

    /// <summary>When the logical creation happened (shared CreateRide timestamp).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    private RequestIdempotencyRecord()
    {
    }

    public RequestIdempotencyRecord(
        string scope,
        string key,
        string requestFingerprint,
        Guid resourceId,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);

        if (scope.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Scope exceeds 64 chars.");
        }

        if (key.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "Key exceeds 128 chars.");
        }

        if (requestFingerprint.Length != 64)
        {
            throw new ArgumentException("Request fingerprint must be the 64-char hash.", nameof(requestFingerprint));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Idempotency record requires a non-empty resource identity.", nameof(resourceId));
        }

        if (createdAt == default)
        {
            throw new ArgumentException("Idempotency record requires a concrete creation timestamp.", nameof(createdAt));
        }

        Scope = scope;
        Key = key;
        RequestFingerprint = requestFingerprint;
        ResourceId = resourceId;
        CreatedAt = createdAt;
    }
}
