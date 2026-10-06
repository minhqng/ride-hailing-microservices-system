// B-W05-05: MatchingRound aggregate (Doc 01 §8.1-8.2, Doc 02 §12.2).
// W5 scope: identity + PROCESSING lifecycle + snapshot/ordinal bookkeeping.
// Terminal transitions (RESERVED/NO_DRIVER/...) need reserve outcomes (W6)
// and Outbox atomicity (W9): they do NOT exist here by design.
// Stage stays S0: advancement needs UNKNOWN semantics (W7).
namespace RideHailing.MatchingService.Domain.Matching;

public enum MatchingRoundStatus
{
    Processing = 0,
    Reserved = 1,
    NoDriver = 2,
    Cancelled = 3,
    Failed = 4,
}

public sealed class MatchingRound
{
    public Guid TripId { get; }
    public int Round { get; }
    public Guid InputMessageId { get; }
    public MatchingRoundStatus Status { get; }
    public string CurrentStage { get; }
    public string RankingConfigVersion { get; }
    public string? CandidateSnapshotHash { get; private set; }
    public string? CandidateSnapshotJson { get; private set; }
    public int CandidateCount { get; private set; }
    public int NextAttemptOrdinal { get; private set; }

    public MatchingRound(Guid tripId, int round, Guid inputMessageId, string rankingConfigVersion)
    {
        if (tripId == Guid.Empty)
        {
            throw new ArgumentException("tripId must not be empty.", nameof(tripId));
        }

        if (round < 1)
        {
            throw new ArgumentException("matchingRound must be >= 1.", nameof(round));
        }

        if (inputMessageId == Guid.Empty)
        {
            throw new ArgumentException("inputMessageId must not be empty.", nameof(inputMessageId));
        }

        if (string.IsNullOrWhiteSpace(rankingConfigVersion))
        {
            throw new ArgumentException("rankingConfigVersion must not be empty.", nameof(rankingConfigVersion));
        }

        TripId = tripId;
        Round = round;
        InputMessageId = inputMessageId;
        Status = MatchingRoundStatus.Processing;
        CurrentStage = "S0";
        RankingConfigVersion = rankingConfigVersion;
    }

    public void AttachSnapshot(string hash, string json, int count, int nextOrdinal)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException("snapshot hash must not be empty.", nameof(hash));
        }

        if (count < 0 || nextOrdinal < 0)
        {
            throw new ArgumentException("count/ordinal must be >= 0.");
        }

        CandidateSnapshotHash = hash;
        CandidateSnapshotJson = json;
        CandidateCount = count;
        NextAttemptOrdinal = nextOrdinal;
    }

    public static string ToDb(MatchingRoundStatus status) => status switch
    {
        MatchingRoundStatus.Processing => "PROCESSING",
        MatchingRoundStatus.Reserved => "RESERVED",
        MatchingRoundStatus.NoDriver => "NO_DRIVER",
        MatchingRoundStatus.Cancelled => "CANCELLED",
        MatchingRoundStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static MatchingRoundStatus FromDb(string status) => status switch
    {
        "PROCESSING" => MatchingRoundStatus.Processing,
        "RESERVED" => MatchingRoundStatus.Reserved,
        "NO_DRIVER" => MatchingRoundStatus.NoDriver,
        "CANCELLED" => MatchingRoundStatus.Cancelled,
        "FAILED" => MatchingRoundStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), $"Unknown round status '{status}'."),
    };
}
