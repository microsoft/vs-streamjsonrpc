// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.Serialization;
    using System.Text;
    using Microsoft;

    /// <summary>
    /// A class that describes useful data that may be found in the JSON-RPC error message's error.data property.
    /// </summary>
    [DataContract]
    public class CommonErrorData
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommonErrorData"/> class.
        /// </summary>
        public CommonErrorData()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CommonErrorData"/> class.
        /// </summary>
        /// <param name="copyFrom">The exception to copy error details from.</param>
        public CommonErrorData(Exception copyFrom)
        {
            Requires.NotNull(copyFrom, nameof(copyFrom));

            this.Message = copyFrom.Message;
            this.StackTrace = copyFrom.StackTrace;
            this.HResult = copyFrom.HResult;
            this.TypeName = copyFrom.GetType().FullName;
            this.Inner = copyFrom.InnerException != null ? new CommonErrorData(copyFrom.InnerException) : null;
        }

        /// <summary>
        /// Gets or sets the type of error (e.g. the full type name of the exception thrown).
        /// </summary>
        [DataMember(Order = 0, Name = "type")]
        public string? TypeName { get; set; }

        /// <summary>
        /// Gets or sets the message associated with this error.
        /// </summary>
        [DataMember(Order = 1, Name = "message")]
        public string? Message { get; set; }

        /// <summary>
        /// Gets or sets the stack trace associated with the error.
        /// </summary>
        [DataMember(Order = 2, Name = "stack")]
        public string? StackTrace { get; set; }

        /// <summary>
        /// Gets or sets the application error code or HRESULT of the failure.
        /// </summary>
        [DataMember(Order = 3, Name = "code")]
        public int HResult { get; set; }

        /// <summary>
        /// Gets or sets the inner error information, if any.
        /// </summary>
        [DataMember(Order = 4, Name = "inner")]
        public CommonErrorData? Inner { get; set; }
    }
}
