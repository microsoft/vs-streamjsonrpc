#if POLYTYPE

using Nerdbank.MessagePack;

namespace StreamJsonRpc;

internal static class SerializationContextExtensions
{
    internal static object FormatterKey { get; } = new();

    internal static NerdbankMessagePackFormatter GetFormatter(this in SerializationContext context)
        => ((NerdbankMessagePackFormatter?)context[FormatterKey]) ?? throw new InvalidOperationException("This converter may only be used within the context of its owning formatter.");
}

#endif
