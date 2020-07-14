// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Collections.Generic;
    using System.Diagnostics.Tracing;
    using System.Text;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// The ETW source for logging events for this library.
    /// </summary>
    /// <remarks>
    /// We use a fully-descriptive type name because the type name becomes the name
    /// of the ETW Provider.
    /// </remarks>
    [EventSource(Name = "StreamJsonRpc")]
    internal sealed class JsonRpcEventSource : EventSource
    {
        /// <summary>
        /// The singleton instance of this event source.
        /// </summary>
        internal static readonly JsonRpcEventSource Instance = new JsonRpcEventSource();

        /// <summary>
        /// The event ID for the <see cref="SendingNotification"/>.
        /// </summary>
        private const int SendingNotificationEvent = 1;

        /// <summary>
        /// The event ID for the <see cref="SendingRequest"/>.
        /// </summary>
        private const int SendingRequestEvent = 2;

        /// <summary>
        /// The event ID for the <see cref="SendingCancellationRequest"/>.
        /// </summary>
        private const int SendingCancellationRequestEvent = 3;

        /// <summary>
        /// The event ID for the <see cref="ReceivedResult"/>.
        /// </summary>
        private const int ReceivedResultEvent = 4;

        /// <summary>
        /// The event ID for the <see cref="ReceivedError"/>.
        /// </summary>
        private const int ReceivedErrorEvent = 5;

        /// <summary>
        /// The event ID for the <see cref="ReceivedNoResponse"/>.
        /// </summary>
        private const int ReceivedNoResponseEvent = 6;

        /// <summary>
        /// The event ID for the <see cref="ReceivedNotification"/>.
        /// </summary>
        private const int ReceivedNotificationEvent = 20;

        /// <summary>
        /// The event ID for the <see cref="ReceivedRequest"/>.
        /// </summary>
        private const int ReceivedRequestEvent = 21;

        /// <summary>
        /// The event ID for the <see cref="ReceivedCancellationRequest"/>.
        /// </summary>
        private const int ReceivedCancellationRequestEvent = 22;

        /// <summary>
        /// The event ID for the <see cref="SendingResult"/>.
        /// </summary>
        private const int SendingResultEvent = 23;

        /// <summary>
        /// The event ID for the <see cref="SendingError"/>.
        /// </summary>
        private const int SendingErrorEvent = 24;

        /// <summary>
        /// The event ID for the <see cref="TransmissionQueued"/>.
        /// </summary>
        private const int TransmissionQueuedEvent = 30;

        /// <summary>
        /// The event ID for the <see cref="TransmissionCompleted"/>.
        /// </summary>
        private const int TransmissionCompletedEvent = 31;

        /// <summary>
        /// The event ID for the <see cref="TransmissionCompletedSize"/>.
        /// </summary>
        private const int TransmisionCompletedSizeEvent = 32;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcEventSource"/> class.
        /// </summary>
        /// <remarks>
        /// ETW wants to see no more than one instance of this class.
        /// </remarks>
        private JsonRpcEventSource()
        {
        }

        /// <summary>
        /// Signals the transmission of a notification.
        /// </summary>
        /// <param name="method">The name of the method.</param>
        /// <param name="args">A snippet representing the arguments.</param>
        [Event(SendingNotificationEvent, Task = Tasks.Notification, Opcode = EventOpcode.Send, Level = EventLevel.Verbose)]
        public void SendingNotification(string method, string args)
        {
            this.WriteEvent(SendingNotificationEvent, method, args);
        }

        /// <summary>
        /// Signals the transmission of a request.
        /// </summary>
        /// <param name="requestId">The id of the request, if any.</param>
        /// <param name="method">The name of the method.</param>
        /// <param name="args">A snippet representing the arguments.</param>
        [Event(SendingRequestEvent, Task = Tasks.OutboundCall, Opcode = EventOpcode.Start, Level = EventLevel.Verbose)]
        public void SendingRequest(long requestId, string method, string args)
        {
            this.WriteEvent(SendingRequestEvent, requestId, method, args);
        }

        /// <summary>
        /// Signals the receipt of a successful response.
        /// </summary>
        /// <param name="requestId">The ID of the request being responded to.</param>
        [Event(ReceivedResultEvent, Task = Tasks.OutboundCall, Opcode = EventOpcode.Stop, Tags = Tags.Success, Level = EventLevel.Informational)]
        public void ReceivedResult(long requestId)
        {
            this.WriteEvent(ReceivedResultEvent, requestId);
        }

        /// <summary>
        /// Signals the receipt of a response.
        /// </summary>
        /// <param name="requestId">The ID of the request being responded to.</param>
        /// <param name="errorCode">The <see cref="Protocol.JsonRpcErrorCode"/> on the error response.</param>
        [Event(ReceivedErrorEvent, Task = Tasks.OutboundCall, Opcode = EventOpcode.Stop, Message = "Request {0} failed with: {1}", Tags = Tags.Error, Level = EventLevel.Warning)]
        public void ReceivedError(long requestId, JsonRpcErrorCode errorCode)
        {
            this.WriteEvent(ReceivedErrorEvent, requestId, (long)errorCode);
        }

        /// <summary>
        /// Signals that the connection dropped before a response to a request was received.
        /// </summary>
        /// <param name="requestId">The ID of the request that did not receive a response.</param>
        [Event(ReceivedNoResponseEvent, Task = Tasks.OutboundCall, Opcode = EventOpcode.Stop, Message = "Request {0} received no response.", Tags = Tags.Dropped, Level = EventLevel.Warning)]
        public void ReceivedNoResponse(long requestId)
        {
            this.WriteEvent(ReceivedNoResponseEvent, requestId);
        }

        /// <summary>
        /// Signals that a previously transmitted request is being canceled.
        /// </summary>
        /// <param name="requestId">The ID of the request being canceled.</param>
        [Event(SendingCancellationRequestEvent, Task = Tasks.Cancellation, Opcode = EventOpcode.Send, Level = EventLevel.Informational)]
        public void SendingCancellationRequest(long requestId)
        {
            this.WriteEvent(SendingCancellationRequestEvent, requestId);
        }

        /// <summary>
        /// Signals the receipt of a notification.
        /// </summary>
        /// <param name="method">The name of the method.</param>
        /// <param name="args">A snippet representing the arguments.</param>
        [Event(ReceivedNotificationEvent, Task = Tasks.Notification, Opcode = EventOpcode.Receive, Level = EventLevel.Verbose)]
        public void ReceivedNotification(string method, string args)
        {
            this.WriteEvent(ReceivedNotificationEvent, method, args);
        }

        /// <summary>
        /// Signals the receipt of a request.
        /// </summary>
        /// <param name="requestId">The id of the request, if any.</param>
        /// <param name="method">The name of the method.</param>
        /// <param name="args">A snippet representing the arguments.</param>
        [Event(ReceivedRequestEvent, Task = Tasks.InboundCall, Opcode = EventOpcode.Start, Level = EventLevel.Verbose)]
        public void ReceivedRequest(long requestId, string method, string args)
        {
            this.WriteEvent(ReceivedRequestEvent, requestId, method, args);
        }

        /// <summary>
        /// Signals the transmission of a successful response.
        /// </summary>
        /// <param name="requestId">The ID of the request being responded to.</param>
        [Event(SendingResultEvent, Task = Tasks.InboundCall, Opcode = EventOpcode.Stop, Tags = Tags.Success, Level = EventLevel.Informational)]
        public void SendingResult(long requestId)
        {
            this.WriteEvent(SendingResultEvent, requestId);
        }

        /// <summary>
        /// Signals the receipt of a response.
        /// </summary>
        /// <param name="requestId">The ID of the request being responded to.</param>
        /// <param name="errorCode">The <see cref="Protocol.JsonRpcErrorCode"/> on the error response.</param>
        [Event(SendingErrorEvent, Task = Tasks.InboundCall, Opcode = EventOpcode.Stop, Message = "Request {0} failed with: {1}", Tags = Tags.Error, Level = EventLevel.Warning)]
        public void SendingError(long requestId, JsonRpcErrorCode errorCode)
        {
            this.WriteEvent(SendingErrorEvent, requestId, (long)errorCode);
        }

        /// <summary>
        /// Signals that a previously transmitted request is being canceled.
        /// </summary>
        /// <param name="requestId">The ID of the request being canceled.</param>
        [Event(ReceivedCancellationRequestEvent, Task = Tasks.Cancellation, Opcode = EventOpcode.Receive, Level = EventLevel.Informational)]
        public void ReceivedCancellationRequest(long requestId)
        {
            this.WriteEvent(ReceivedCancellationRequestEvent, requestId);
        }

        /// <summary>
        /// Signals that a message has been queued for transmission.
        /// </summary>
        [Event(TransmissionQueuedEvent, Task = Tasks.MessageTransmission, Opcode = EventOpcode.Start, Level = EventLevel.Informational)]
        public void TransmissionQueued()
        {
            this.WriteEvent(TransmissionQueuedEvent);
        }

        /// <summary>
        /// Signals that a message has been transmitted.
        /// </summary>
        [Event(TransmissionCompletedEvent, Task = Tasks.MessageTransmission, Opcode = EventOpcode.Stop, Level = EventLevel.Informational)]
        public void TransmissionCompleted()
        {
            this.WriteEvent(TransmissionCompletedEvent);
        }

        /// <summary>
        /// Signal that a message has been transmitted with the size.
        /// </summary>
        /// <param name="size">Size of the payload.</param>.
        [Event(TransmisionCompletedSizeEvent, Task = Tasks.MessageTransmission, Opcode = EventOpcode.Stop, Level = EventLevel.Informational)]
        public void TransmissionCompletedSize(long size)
        {
            this.WriteEvent(TransmisionCompletedSizeEvent, size);
        }

        /// <summary>
        /// Creates a string representation of arguments of max 200 characters for logging.
        /// </summary>
        /// <param name="arguments">A single argument or an array of arguments.</param>
        /// <returns>String representation of first argument only.</returns>
        internal static string GetArgumentsString(object? arguments)
        {
            if (arguments == null)
            {
                return string.Empty;
            }

            const int maxLength = 128;

            if (arguments is object?[] args)
            {
                var stringBuilder = new StringBuilder();
                if (args != null && args.Length > 0)
                {
                    for (int i = 0; i < args.Length; ++i)
                    {
                        stringBuilder.Append($"arg{i}: {args[i]}, ");
                        if (stringBuilder.Length > maxLength)
                        {
                            return $"{stringBuilder.ToString(0, maxLength)}...(truncated)";
                        }
                    }
                }

                return stringBuilder.ToString();
            }
            else
            {
                string? retVal = arguments.ToString();
                return (retVal?.Length > maxLength) ? $"{retVal.Substring(0, maxLength)}...(truncated)" : (retVal ?? "(null)");
            }
        }

        /// <summary>
        /// Names of constants in this class make up the middle term in the event name
        /// E.g.: StreamJsonRpc/InvokeMethod/Start.
        /// </summary>
        /// <remarks>Name of this class is important for EventSource.</remarks>
        public static class Tasks
        {
            public const EventTask OutboundCall = (EventTask)1;
            public const EventTask InboundCall = (EventTask)2;
            public const EventTask MessageTransmission = (EventTask)3;
            public const EventTask Cancellation = (EventTask)4;
            public const EventTask Notification = (EventTask)5;
        }

        /// <summary>
        /// Names of constants in this class make up the middle term in the event name
        /// E.g.: StreamJsonRpc/InvokeMethod/Start.
        /// </summary>
        /// <remarks>Name of this class is important for EventSource.</remarks>
        public static class Tags
        {
            public const EventTags Success = (EventTags)0x1;
            public const EventTags Error = (EventTags)0x2;
            public const EventTags Dropped = (EventTags)0x4;
        }
    }
}
