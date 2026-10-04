// CreateRide request identity: Rider-scoped idempotency namespace plus the
// semantic request fingerprint (A-W04-02, A-W04-03).
//
// SCOPE: "trip:create:<rider-guid-D>" (48 chars, within the 64-char column).
// Same Rider + key share a namespace; different Riders with the same client
// key never collide. Lookups always use (scope, key), never key alone.
//
// FINGERPRINT: exactly the semantic request (rider, pickup lat/lon, dropoff
// lat/lon). It represents REQUEST meaning, never the key, raw JSON, timestamps,
// generated identities or pricing output. Canonical form (versioned, fixed
// field order, fixed separators):
//
//   v1|rider:<guid-D>|pickup:<lat-bits>:<lon-bits>|dropoff:<lat-bits>:<lon-bits>
//
// Each coordinate is canonicalized by normalizing numeric zero to +0.0 (so
// +0.0 and -0.0 share one meaning) and then encoded as its IEEE-754 bit
// pattern in 16 lowercase hex chars (culture-invariant). TripLocation already
// rejects NaN/Infinity/out-of-range values, so no further normalization
// applies. The canonical string is hashed with SHA-256 over UTF-8 and
// persisted as 64 lowercase hex chars.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Application.Trips.CreateRide;

/// <summary>
/// Deterministic Rider-scoped namespace and semantic fingerprint for
/// CreateRide request-idempotency.
/// </summary>
public static class CreateRideRequestFingerprint
{
    /// <summary>Maximum accepted Idempotency-Key length (W4 mechanical rule).</summary>
    public const int MaxKeyLength = 128;

    /// <summary>Maximum persisted scope length.</summary>
    public const int MaxScopeLength = 64;

    private const string ScopePrefix = "trip:create:";

    /// <summary>
    /// Builds the deterministic Rider-scoped namespace for CreateRide keys.
    /// </summary>
    public static string BuildScope(Guid riderId)
    {
        if (riderId == Guid.Empty)
        {
            throw new ArgumentException("Scope requires a non-empty rider identity.", nameof(riderId));
        }

        // Guid "D" format is fixed 36 chars; prefix is 12 chars: total 48.
        var scope = ScopePrefix + riderId.ToString("D", CultureInfo.InvariantCulture);
        if (scope.Length > MaxScopeLength)
        {
            throw new InvalidOperationException($"CreateRide scope exceeds {MaxScopeLength} chars: '{scope}'.");
        }

        return scope;
    }

    /// <summary>
    /// Computes the stable semantic fingerprint of one CreateRide request.
    /// Same semantic values always yield the same 64-char lowercase hex hash.
    /// </summary>
    public static string Compute(Guid riderId, TripLocation pickup, TripLocation dropoff)
    {
        if (riderId == Guid.Empty)
        {
            throw new ArgumentException("Fingerprint requires a non-empty rider identity.", nameof(riderId));
        }

        ArgumentNullException.ThrowIfNull(pickup);
        ArgumentNullException.ThrowIfNull(dropoff);

        var canonical =
            "v1" +
            "|rider:" + riderId.ToString("D", CultureInfo.InvariantCulture) +
            "|pickup:" + EncodeCoordinate(pickup.Latitude) + ":" + EncodeCoordinate(pickup.Longitude) +
            "|dropoff:" + EncodeCoordinate(dropoff.Latitude) + ":" + EncodeCoordinate(dropoff.Longitude);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string EncodeCoordinate(double value)
    {
        // +0.0 and -0.0 are the same coordinate value with different bit
        // patterns; normalize numeric zero to +0.0 before taking the bits.
        var normalized = value == 0d ? 0d : value;
        return BitConverter.DoubleToInt64Bits(normalized).ToString("x16", CultureInfo.InvariantCulture);
    }
}
