// Trip domain conflict vocabulary.
//
// Normal domain conflicts (illegal transition, terminal guard, assignment
// identity mismatch) are reported as explicit error codes, not thrown as
// exceptions. Thrown exceptions are reserved for programming and
// data-corruption errors such as invalid construction arguments or
// structurally incoherent persisted state.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Small explicit domain error codes for rejected Trip operations.
/// </summary>
public enum TripErrorCode
{
    /// <summary>No error; the operation succeeded.</summary>
    None = 0,

    /// <summary>
    /// The operation is not a legal edge from the current Trip state
    /// (Doc 01 §6.5 transition table).
    /// </summary>
    IllegalTransition = 1,

    /// <summary>
    /// The Trip is terminal (COMPLETED, CANCELLED, MATCH_FAILED) and no
    /// active transition may reactivate it (DSI-010, DSI-032).
    /// </summary>
    TerminalState = 2,

    /// <summary>
    /// The operation requires an assignment identity that was not supplied:
    /// empty driver/reservation identity on intake, or no current assignment
    /// where the transition requires one (DSI-009, DSI-011).
    /// </summary>
    AssignmentRequired = 3,

    /// <summary>
    /// The supplied driver/reservation identity does not match the current
    /// assignment (DSI-009). The aggregate is unchanged.
    /// </summary>
    AssignmentIdentityMismatch = 4,

    /// <summary>
    /// The operation would create a second simultaneous current assignment
    /// (DSI-008). The aggregate is unchanged.
    /// </summary>
    AssignmentAlreadyPresent = 5,

    /// <summary>
    /// Cancellation from this state depends on product policy that is not
    /// frozen yet, so the operation is left unapplied instead of inventing
    /// an allow/reject semantic. Applies to CREATED and READY_TO_START
    /// until that policy is decided.
    /// </summary>
    CancellationPolicyUnresolved = 6,

    /// <summary>
    /// The supplied round/reservation generation is not the Trip's current
    /// generation: an older/superseded round, an old reservation from a
    /// previous assignment, or an unknown future generation (DSI-016).
    /// The aggregate is unchanged; the caller must reconcile against the
    /// current round/reservation instead of overwriting it.
    /// </summary>
    StaleIdentity = 7,
}
