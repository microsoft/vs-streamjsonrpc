// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Diagnostics.Tracing;
    using System.Text;
    using StreamJsonRpc.Protocol;

    [EventSource(Name = "Microsoft-VisualStudio-StreamJsonRpc")]
    internal sealed class JsonRpcEventSource : EventSource
    {
        /// <summary>
        /// The singleton instance used for logging ETW events.
        /// </summary>
        internal static readonly JsonRpcEventSource Instance = new JsonRpcEventSource();

        /// <summary>
        /// The event ID for the <see cref="SendNotificationStart(string)"/>.
        /// </summary>
        private const int SendNotificationStartEvent = 1;

        /// <summary>
        /// The event ID for the <see cref="SendNotificationStop(string)"/>.
        /// </summary>
        private const int SendNotificationStopEvent = 2;

        /// <summary>
        /// The event ID for the <see cref="SendRequestStart(string)"/>.
        /// </summary>
        private const int SendRequestStartEvent = 3;

        /// <summary>
        /// The event ID for the <see cref="SendRequestStop(string)"/>.
        /// </summary>
        private const int SendRequestStopEvent = 4;

        /// <summary>
        /// The event ID for the <see cref="InvokeMethodStart(string)"/>.
        /// </summary>
        private const int InvokeMethodStartEvent = 5;

        /// <summary>
        /// The event ID for the <see cref="InvokeMethodStop(string)"/>.
        /// </summary>
        private const int InvokeMethodStopEvent = 6;

        /// <summary>
        /// The event ID for the <see cref="CancelRequestStart(string)"/>.
        /// </summary>
        private const int CancelRequestStartEvent = 7;

        /// <summary>
        /// The event ID for the <see cref="CancelRequestStop(string)"/>.
        /// </summary>
        private const int CancelRequestStopEvent = 8;

        /// <summary>
        /// The event ID for the <see cref="InvokeNotificationStart(string)"/>.
        /// </summary>
        private const int InvokeNotificationStartEvent = 9;

        /// <summary>
        /// The event ID for the <see cref="InvokeNotificationStop(string)"/>.
        /// </summary>
        private const int InvokeNotificationStopEvent = 10;

        /// <summary>
        /// Send Notification start.
        /// </summary>
        /// <param name="message">Notification details.</param>
        [Event(SendNotificationStartEvent, Task = Tasks.SendNotification, Opcode = EventOpcode.Start, Level = EventLevel.Verbose)]
        public void SendNotificationStart(string message)
        {
            this.WriteEvent(SendNotificationStartEvent, message);
        }

        /// <summary>
        /// Send Notification end.
        /// </summary>
        /// <param name="message">Notification details.</param>
        [Event(SendNotificationStopEvent, Task = Tasks.SendNotification, Opcode = EventOpcode.Stop, Level = EventLevel.Verbose)]
        public void SendNotificationStop(string message)
        {
            this.WriteEvent(SendNotificationStopEvent, message);
        }

        /// <summary>
        /// Send Request start.
        /// </summary>
        /// <param name="message">Request details.</param>
        [Event(SendRequestStartEvent, Task = Tasks.SendRequest, Opcode = EventOpcode.Start, Level = EventLevel.Verbose)]
        public void SendRequestStart(string message)
        {
            this.WriteEvent(SendRequestStartEvent, message);
        }

        /// <summary>
        /// Send Request end.
        /// </summary>
        /// <param name="message">Request details.</param>
        [Event(SendRequestStopEvent, Task = Tasks.SendRequest, Opcode = EventOpcode.Stop, Level = EventLevel.Verbose)]
        public void SendRequestStop(string message)
        {
            this.WriteEvent(SendRequestStopEvent, message);
        }

        /// <summary>
        /// Invoke Method start.
        /// </summary>
        /// <param name="message">Request and method details.</param>
        [Event(InvokeMethodStartEvent, Task = Tasks.InvokeMethod, Opcode = EventOpcode.Start, Level = EventLevel.Verbose)]
        public void InvokeMethodStart(string message)
        {
            this.WriteEvent(InvokeMethodStartEvent, message);
        }

        /// <summary>
        /// Invoke Method end.
        /// </summary>
        /// <param name="message">Request and method details.</param>
        [Event(InvokeMethodStopEvent, Task = Tasks.InvokeMethod, Opcode = EventOpcode.Stop, Level = EventLevel.Verbose)]
        public void InvokeMethodStop(string message)
        {
            this.WriteEvent(InvokeMethodStopEvent, message);
        }

        /// <summary>
        /// Cancel Request start.
        /// </summary>
        /// <param name="message">Cancellation message.</param>
        [Event(CancelRequestStartEvent, Task = Tasks.CancelRequest, Opcode = EventOpcode.Start, Level = EventLevel.Verbose)]
        public void CancelRequestStart(string message)
        {
            this.WriteEvent(CancelRequestStartEvent, message);
        }

        /// <summary>
        /// Cancel Request end.
        /// </summary>
        /// <param name="message">Cancellation message.</param>
        [Event(CancelRequestStopEvent, Task = Tasks.CancelRequest, Opcode = EventOpcode.Stop, Level = EventLevel.Verbose)]
        public void CancelRequestStop(string message)
        {
            this.WriteEvent(CancelRequestStopEvent, message);
        }

        /// <summary>
        /// Invoke Notification.
        /// </summary>
        /// <param name="message">Details of the Notification.</param>
        [Event(InvokeNotificationStartEvent, Task = Tasks.InvokeNotification, Opcode = EventOpcode.Start, Level = EventLevel.Verbose)]
        public void InvokeNotificationStart(string message)
        {
            this.WriteEvent(InvokeNotificationStartEvent, message);
        }

        /// <summary>
        /// Notification completed.
        /// </summary>
        /// <param name="message">Details of the Notification.</param>
        [Event(InvokeNotificationStopEvent, Task = Tasks.InvokeNotification, Opcode = EventOpcode.Stop, Level = EventLevel.Verbose)]
        public void InvokeNotificationStop(string message)
        {
            this.WriteEvent(InvokeNotificationStopEvent, message);
        }

        /// <summary>
        /// Creates a string representation of arguments of max 200 characters for logging.
        /// </summary>
        /// <param name="arguments">A single argument or an array of arguments.</param>
        /// <returns>String representation of first argument only.</returns>
        internal static string GetArgumentsString(object arguments)
        {
            if (arguments == null)
            {
                return string.Empty;
            }

            const int maxLength = 128;

            if (arguments is object[] args)
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
                string retVal = arguments.ToString();
                return (retVal.Length > maxLength) ? $"{retVal.Substring(0, maxLength)}...(truncated)" : retVal;
            }
        }

        internal static string GetErrorDetails(JsonRpcError error)
        {
            if (error == null)
            {
                return string.Empty;
            }

            string errorDetails = "Error:";
            if (error.Id != null)
            {
                errorDetails += $" Id = \"{error.Id}\", ";
            }

            if (error.Error != null)
            {
                errorDetails += $"Error.Code = \"{error.Error.Code}\", ";
            }

            return errorDetails;
        }

        /// <summary>
        /// Names of constants in this class make up the middle term in the event name
        /// E.g.: Microsoft-VisualStudio-StreamJsonRpc/InvokeMethod/Start.
        /// </summary>
        /// <remarks>Name of this class is important for EventSource.</remarks>
        public static class Tasks
        {
            public const EventTask SendNotification = (EventTask)1;
            public const EventTask SendRequest = (EventTask)2;
            public const EventTask InvokeMethod = (EventTask)3;
            public const EventTask CancelRequest = (EventTask)4;
            public const EventTask InvokeNotification = (EventTask)5;
        }
    }
}
