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
    /// (transition table).
    /// </summary>
    IllegalTransition = 1,

    /// <summary>
    /// The Trip is terminal (COMPLETED, CANCELLED, MATCH_FAILED) and no
    /// active transition may reactivate it.
    /// </summary>
    TerminalState = 2,

    /// <summary>
    /// The operation requires an assignment identity that was not supplied:
    /// empty driver/reservation identity on intake, or no current assignment
    /// where the transition requires one.
    /// </summary>
    AssignmentRequired = 3,

    /// <summary>
    /// The supplied driver/reservation identity does not match the current
    /// assignment. The aggregate is unchanged.
    /// </summary>
    AssignmentIdentityMismatch = 4,

    /// <summary>
    /// The operation would create a second simultaneous current assignment.
    /// The aggregate is unchanged.
    /// </summary>
    AssignmentAlreadyPresent = 5,

    /// <summary>
    /// The supplied round/reservation generation is not the Trip's current
    /// generation: an older/superseded round, an old reservation from a
    /// previous assignment, or an unknown future generation.
    /// The aggregate is unchanged; the caller must reconcile against the
    /// current round/reservation instead of overwriting it.
    /// </summary>
    StaleIdentity = 7,

    /// <summary>
    /// The payment leg cannot quote a positive amount from the frozen fare, so
    /// the ride cannot start its Payment orchestration. The aggregate is
    /// unchanged; the caller must reject the
    /// intake instead of mutating a Trip whose acceptance could never
    /// dispatch AuthorizePayment.
    /// </summary>
    PaymentQuoteRequired = 8,
}
