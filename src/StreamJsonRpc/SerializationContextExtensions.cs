using Nerdbank.MessagePack;

namespace StreamJsonRpc;

internal static class SerializationContextExtensions
{
    private static readonly object FormatterStateKey = new();

    internal static object FormatterKey => FormatterStateKey;

    internal static NerdbankMessagePackFormatter GetFormatter(this ref SerializationContext context)
    {
        return (context[FormatterKey] as NerdbankMessagePackFormatter)
            ?? throw new InvalidOperationException();
    }
}
