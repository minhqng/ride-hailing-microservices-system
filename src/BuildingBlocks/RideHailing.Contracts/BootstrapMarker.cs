// RideHailing.Contracts: integration DTOs/contracts only.
// Must NOT contain domain entities. Remains almost empty during bootstrap.
namespace RideHailing.Contracts;

/// <summary>Bootstrap marker. Proves the assembly compiles and is referenceable.</summary>
public static class BootstrapMarker
{
    public const string Purpose = "Integration contracts only. No domain entities.";
}
