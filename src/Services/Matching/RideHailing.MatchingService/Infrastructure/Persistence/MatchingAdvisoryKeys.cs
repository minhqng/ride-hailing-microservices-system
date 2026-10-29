// Stable advisory-lock key namespaces. Every process that must serialize on
// the same durable round/cancel intent derives the identical key string.
namespace RideHailing.MatchingService.Infrastructure.Persistence;

internal static class MatchingAdvisoryKeys
{
    public static string Round(Guid tripId, int round) =>
        $"matching-round:{tripId:N}:{round}";

    public static string Cancel(Guid tripId, int round) =>
        $"matching-cancel:{tripId:N}:{round}";
}
