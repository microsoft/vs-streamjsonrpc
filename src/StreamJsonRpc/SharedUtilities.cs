// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Utilities that are source-shared between the library and its tests.
/// </summary>
internal static class SharedUtilities
{
    /// <summary>
    /// The various modes that can be used to test the <see cref="JsonRpcEventSource"/> class.
    /// </summary>
    internal enum EventSourceTestMode
    {
        /// <summary>
        /// ETW events are not forced on.
        /// </summary>
        None,

        /// <summary>
        /// ETW events are forced on and exceptions are swallowed as they would be in production.
        /// </summary>
        EmulateProduction,

        /// <summary>
        /// ETW events are forced on and exceptions are not swallowed, allowing tests to detect errors in ETW logging.
        /// </summary>
        DoNotSwallowExceptions,
    }

    internal static EventSourceTestMode GetEventSourceTestMode() => Environment.GetEnvironmentVariable("StreamJsonRpc_TestWithEventSource") switch
    {
        "1" => EventSourceTestMode.EmulateProduction,
        "2" => EventSourceTestMode.DoNotSwallowExceptions,
        _ => EventSourceTestMode.None,
    };
}
