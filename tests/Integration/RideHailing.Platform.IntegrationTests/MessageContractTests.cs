using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RideHailing.Messaging.Contracts;

namespace RideHailing.Platform.IntegrationTests;

// In-process contract tests; actual broker/DB proofs run in the Compose smoke scripts.
public class MessageContractTests
{
    private static MessageEnvelope<string> Envelope() => MessageEnvelope<string>.Create(
        "AuthorizePaymentV1", "flow-1", "payload", messageId: "message-1", causationId: "cause-1",
        aggregateType: "Trip", aggregateId: "trip-1", aggregateVersion: 7,
        sagaId: "saga-1", operationKey: "trip-1:authorize:v1");

    [Fact]
    public void WireRoundTripPreservesRecoveryIdentityWithoutTraceDependency()
    {
        var envelope = Envelope();
        var json = JsonSerializer.Serialize(envelope);
        Assert.Contains("\"operationKey\":\"trip-1:authorize:v1\"", json);
        Assert.Contains("\"aggregateVersion\":7", json);
        Assert.DoesNotContain("trace", json, StringComparison.OrdinalIgnoreCase);
        var restored = JsonSerializer.Deserialize<MessageEnvelope<string>>(json)!;
        restored.Validate();
        Assert.Equal(envelope, restored);
        Assert.Equal(envelope.MessageId, MessageProperties.Create(restored).MessageId);
    }

    [Fact]
    public void CriticalPropertiesArePersistentAndCarryStableIdentity()
    {
        var properties = MessageProperties.Create(Envelope());
        Assert.True(properties.Persistent);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal("flow-1", properties.CorrelationId);
        Assert.Equal("AuthorizePaymentV1", properties.Type);
    }

    [Fact]
    public void InvalidEnvelopeVersionsAndIdsAreRejected()
    {
        foreach (var invalid in new[] {
            Envelope() with { SchemaVersion = 2 }, Envelope() with { MessageId = " " },
            Envelope() with { CorrelationId = "" }, Envelope() with { OccurredAt = default },
            Envelope() with { Payload = null! }, Envelope() with { AggregateVersion = -1 },
            Envelope() with { AggregateId = null }, Envelope() with { OperationKey = " " } })
            Assert.Throws<ArgumentException>(invalid.Validate);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("bytes")]
    [InlineData("memory")]
    public void TraceHeadersRoundTripAsRabbitWireValues(string kind)
    {
        var context = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, "vendor=value");
        var headers = new Dictionary<string, object?>();
        TraceHeaderPropagation.Inject(context, headers);
        foreach (var key in headers.Keys.ToArray())
        {
            var value = (string)headers[key]!;
            headers[key] = kind switch { "bytes" => Encoding.UTF8.GetBytes(value),
                "memory" => new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(value)), _ => value };
        }
        Assert.True(TraceHeaderPropagation.TryExtract(headers, out var extracted));
        Assert.Equal(context.TraceId, extracted.TraceId);
        Assert.Equal(context.SpanId, extracted.SpanId);
        Assert.Equal(context.TraceState, extracted.TraceState);
        Assert.True(extracted.IsRemote);
    }

    [Fact]
    public void ReusedHeadersDoNotLeakPreviousContext()
    {
        var headers = new Dictionary<string, object?> { ["traceparent"] = "stale", ["tracestate"] = "vendor=stale" };
        var context = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);
        TraceHeaderPropagation.Inject(context, headers);
        Assert.False(headers.ContainsKey("tracestate"));
        TraceHeaderPropagation.Inject(default, headers);
        Assert.Empty(headers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-trace")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-01")]
    public void InvalidTraceDoesNotPreventEnvelopeValidation(string trace)
    {
        Assert.False(TraceHeaderPropagation.TryExtract(new Dictionary<string, object?> { ["traceparent"] = trace }, out _));
        Envelope().Validate();
    }
}
