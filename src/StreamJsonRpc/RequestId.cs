// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using Microsoft;
    using Newtonsoft.Json;

    /// <summary>
    /// Represents the ID of a request, whether it is a number or a string.
    /// </summary>
    [JsonConverter(typeof(RequestIdJsonConverter))]
    public struct RequestId : IEquatable<RequestId>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RequestId"/> struct.
        /// </summary>
        /// <param name="id">The ID of the request.</param>
        public RequestId(long id)
        {
            this.Number = id;
            this.String = null;
            this.IsNull = false;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RequestId"/> struct.
        /// </summary>
        /// <param name="id">The ID of the request.</param>
        public RequestId(string? id)
        {
            this.String = id;
            this.Number = null;
            this.IsNull = id is null;
        }

        /// <summary>
        /// Gets an empty (absent) ID.
        /// </summary>
        public static RequestId NotSpecified => default;

        /// <summary>
        /// Gets the special value for an explicitly specified <c>null</c> request ID.
        /// </summary>
        public static RequestId Null => new RequestId(null);

        /// <summary>
        /// Gets the ID if it is a number.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public long? Number { get; }

        /// <summary>
        /// Gets the ID if it is a string.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public string? String { get; }

        /// <summary>
        /// Gets a value indicating whether this request ID is explicitly specified as the special "null" value.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsNull { get; }

        /// <summary>
        /// Gets a value indicating whether the request ID was not specified (i.e. no string, number or null was given).
        /// </summary>
        public bool IsEmpty => this.Number is null && this.String is null && !this.IsNull;

        /// <summary>
        /// Gets the ID as an object (whether it is a <see cref="long"/>, a <see cref="string"/> or null).
        /// </summary>
        internal object? ObjectValue => (object?)this.Number ?? this.String;

        /// <summary>
        /// Gets the ID if it is a number, or -1.
        /// </summary>
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal long NumberIfPossibleForEvent => this.Number ?? -1;

        /// <inheritdoc/>
        public bool Equals(RequestId other) => this.Number == other.Number && this.String == other.String && this.IsNull == other.IsNull;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is RequestId other && this.Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => this.Number?.GetHashCode() ?? this.String?.GetHashCode() ?? 0;

        /// <inheritdoc/>
        public override string ToString() => this.Number?.ToString(CultureInfo.InvariantCulture) ?? this.String ?? (this.IsNull ? "(null)" : "(not specified)");

        internal static RequestId Parse(object? value)
        {
            return
                value is null ? default :
                value is long l ? new RequestId(l) :
                value is string s ? new RequestId(s) :
                value is int i ? new RequestId(i) :
                throw new JsonSerializationException("Unexpected type for id property: " + value.GetType().Name);
        }
    }
}
