// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Globalization;
    using Newtonsoft.Json;

    /// <summary>
    /// Represents the ID of a request, whether it is a number of a string.
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
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RequestId"/> struct.
        /// </summary>
        /// <param name="id">The ID of the request.</param>
        public RequestId(string id)
        {
            this.String = id;
            this.Number = null;
        }

        /// <summary>
        /// Gets an empty (absent) ID.
        /// </summary>
        public static RequestId NotSpecified => default;

        /////// <summary>
        /////// Creates a <see cref="RequestId"/> based on a <see cref="long"/> ID.
        /////// </summary>
        /////// <param name="id">The request ID.</param>
        ////public static implicit operator RequestId(long id) => new RequestId(id);

        /// <summary>
        /// Gets the ID if it is a number.
        /// </summary>
        public long? Number { get; }

        /// <summary>
        /// Gets the ID if it is a string.
        /// </summary>
        public string? String { get; }

        /// <summary>
        /// Gets a value indicating whether both the <see cref="Number"/> and <see cref="String"/> properties are uninitialized.
        /// </summary>
        public bool IsEmpty => this.Number is null && this.String is null;

        /// <summary>
        /// Gets the ID as an object (whether it is a <see cref="long"/>, a <see cref="string"/> or null).
        /// </summary>
        internal object? ObjectValue => (object?)this.Number ?? this.String;

        /// <summary>
        /// Gets the ID if it is a number, or -1.
        /// </summary>
        internal long NumberIfPossibleForEvent => this.Number ?? -1;

        /// <inheritdoc/>
        public bool Equals(RequestId other) => this.Number == other.Number && this.String == other.String;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is RequestId other && this.Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => this.Number?.GetHashCode() ?? this.String?.GetHashCode() ?? 0;

        /// <inheritdoc/>
        public override string ToString() => this.Number?.ToString(CultureInfo.InvariantCulture) ?? this.String ?? "null";

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
