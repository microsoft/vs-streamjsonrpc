using System;
using Microsoft;

namespace StreamJsonRpc
{
    public class JsonRpcDisconnectedEventArgs : EventArgs
    {
        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason)
            : this(description, reason, lastMessage: null, exception: null)
        {   
        }

        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason, Exception exception)
            : this(description, reason, lastMessage: null, exception: exception)
        {
        }

        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason, string lastMessage)
            : this(description, reason, lastMessage: lastMessage, exception: null)
        {
        }

        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason, string lastMessage, Exception exception)
        {
            Requires.NotNullOrWhiteSpace(description, nameof(description));

            this.Description = description;
            this.Reason = reason;
            this.LastMessage = lastMessage;
            this.Exception = exception;
        }

        public string Description { get; }

        public DisconnectedReason Reason { get; }

        public string LastMessage { get; }
        
        public Exception Exception { get; }
    }
}
