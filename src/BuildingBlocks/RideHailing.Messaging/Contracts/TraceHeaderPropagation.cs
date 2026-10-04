using System.Diagnostics;

namespace RideHailing.Messaging.Contracts;

public static class TraceHeaderPropagation
{
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    public static void Inject(ActivityContext context, IDictionary<string, object?> headers)
    {
        headers.Remove(TraceParentHeader);
        headers.Remove(TraceStateHeader);
        if (context.TraceId == default || context.SpanId == default) return;
        headers[TraceParentHeader] = $"00-{context.TraceId}-{context.SpanId}-{(byte)context.TraceFlags:x2}";
        if (!string.IsNullOrWhiteSpace(context.TraceState))
            headers[TraceStateHeader] = context.TraceState;
    }

    public static bool TryExtract(IDictionary<string, object?>? headers, out ActivityContext context)
    {
        context = default;
        if (headers is null || !headers.TryGetValue(TraceParentHeader, out var parentValue)) return false;
        var parent = HeaderValue(parentValue);
        var state = headers.TryGetValue(TraceStateHeader, out var stateValue) ? HeaderValue(stateValue) : null;
        return ActivityContext.TryParse(parent, state, isRemote: true, out context);
    }

    private static string? HeaderValue(object? value) => value switch
    {
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        ReadOnlyMemory<byte> memory => System.Text.Encoding.UTF8.GetString(memory.Span),
        string text => text,
        _ => null
    };
}
