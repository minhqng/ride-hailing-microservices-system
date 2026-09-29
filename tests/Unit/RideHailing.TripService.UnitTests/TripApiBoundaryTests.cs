using System.Reflection;
using RideHailing.TripService.Api;

namespace RideHailing.TripService.UnitTests;

// Service-boundary audit as executable tests:
//   HTTP DTO != Domain aggregate != Persistence entity != Integration message.
// Trip Api DTOs must not expose EF/DbContext, Saga state, Outbox rows,
// MatchingAttempt, Payment internals, or Driver aggregate internals.
// Assignment appears only as opaque Guid references (tripId-scoped), never as
// nested domain objects.
public sealed class TripApiBoundaryTests
{
    private static readonly HashSet<string> AllowedPropertyTypes = new(StringComparer.Ordinal)
    {
        "Guid", "Guid?", "Int32", "String", "Double",
        nameof(TripLocationDto),
    };

    [Fact]
    public void ApiDtos_ExposeOnly_PlainTransportTypes()
    {
        var apiTypes = typeof(CreateRideRequest).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "RideHailing.TripService.Api" && t.IsClass && !t.IsAbstract)
            .Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal)
                || t.Name.EndsWith("Response", StringComparison.Ordinal)
                || t.Name.EndsWith("Dto", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(apiTypes);

        foreach (var type in apiTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var typeName = property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>)
                    ? property.PropertyType.GetGenericArguments()[0].Name + "?"
                    : property.PropertyType.Name;

                Assert.True(
                    AllowedPropertyTypes.Contains(typeName),
                    $"{type.Name}.{property.Name} exposes forbidden type '{property.PropertyType.FullName}'. " +
                    "Api DTOs must stay plain transport shapes.");
            }
        }
    }

    [Fact]
    public void ApiDtos_DoNot_ExposeForbiddenMemberNames()
    {
        string[] forbidden = ["DbContext", "Outbox", "Saga", "MatchingAttempt", "PaymentOperation", "DriverReserved", "EntityFramework"];
        var apiTypes = typeof(CreateRideRequest).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "RideHailing.TripService.Api");

        foreach (var type in apiTypes)
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (var bad in forbidden)
                {
                    Assert.DoesNotContain(bad, member.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void TripService_DoesNot_ReferenceForbiddenServiceProjects()
    {
        var referenced = typeof(CreateRideRequest).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        foreach (var forbidden in new[] { "RideHailing.DriverService", "RideHailing.MatchingService", "RideHailing.PaymentService" })
        {
            Assert.DoesNotContain(forbidden, referenced);
        }
    }
}
