#pragma warning disable SA1303 // Const field names should begin with upper-case letter

namespace StreamJsonRpc.Protocol
{
    using System;
    using Microsoft;

    internal static class Constants
    {
        internal const string jsonrpc = "jsonrpc";
        internal const string id = "id";

        internal static class Request
        {
            internal const string method = "method";
            internal const string @params = "params";
            internal const string traceparent = "traceparent";
            internal const string tracestate = "tracestate";

            internal static bool IsPropertyReserved(string propertyName)
            {
                Requires.NotNull(propertyName, nameof(propertyName));

                return
                    propertyName.Equals(jsonrpc, StringComparison.Ordinal) ||
                    propertyName.Equals(method, StringComparison.Ordinal) ||
                    propertyName.Equals(@params, StringComparison.Ordinal) ||
                    propertyName.Equals(traceparent, StringComparison.Ordinal) ||
                    propertyName.Equals(tracestate, StringComparison.Ordinal) ||
                    propertyName.Equals(id, StringComparison.Ordinal);
            }
        }

        internal static class Result
        {
            internal const string result = "result";

            internal static bool IsPropertyReserved(string propertyName)
            {
                Requires.NotNull(propertyName, nameof(propertyName));

                return
                    propertyName.Equals(jsonrpc, StringComparison.Ordinal) ||
                    propertyName.Equals(result, StringComparison.Ordinal) ||
                    propertyName.Equals(id, StringComparison.Ordinal);
            }
        }

        internal static class Error
        {
            internal const string error = "error";

            internal static bool IsPropertyReserved(string propertyName)
            {
                Requires.NotNull(propertyName, nameof(propertyName));

                return
                    propertyName.Equals(jsonrpc, StringComparison.Ordinal) ||
                    propertyName.Equals(error, StringComparison.Ordinal) ||
                    propertyName.Equals(id, StringComparison.Ordinal);
            }
        }
    }
}
