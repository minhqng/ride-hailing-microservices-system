// RideHailing.Contracts: integration DTOs/contracts only.
// Must NOT contain domain entities. Remains almost empty during bootstrap.
// TODO(Member C, W4+): add versioned MessageEnvelope/contract catalog per Doc 02 §5-6.
namespace RideHailing.Contracts;

/// <summary>Bootstrap marker. Proves the assembly compiles and is referenceable.</summary>
public static class BootstrapMarker
{
    public const string Purpose = "Integration contracts only. No domain entities.";
}
