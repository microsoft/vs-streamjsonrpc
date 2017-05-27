namespace StreamJsonRpc
{
    /// <summary>
    /// Identifies a reason for a stream disconnection.
    /// </summary>
    public enum DisconnectedReason
    {
        /// <summary>
        /// Unknown reason.
        /// </summary>
        Unknown,

        /// <summary>
        /// An error occurred while accessing the stream.
        /// </summary>
        StreamError,

        /// <summary>
        /// A syntax or schema error while reading a JSON-RPC packet occurred.
        /// </summary>
        ParseError,

        /// <summary>
        /// The stream was disposed.
        /// </summary>
        Disposed
    }
}
