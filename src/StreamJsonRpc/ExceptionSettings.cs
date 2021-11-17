// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Runtime.Serialization;
    using Microsoft;

    /// <summary>
    /// Contains security-related settings that influence how errors are serialized and deserialized.
    /// </summary>
    public abstract record ExceptionSettings
    {
        /// <summary>
        /// The recommended settings for use when communicating with a trusted party.
        /// </summary>
        public static readonly ExceptionSettings TrustedData = new DefaultExceptionSettings(int.MaxValue, trusted: true);

        /// <summary>
        /// The recommended settings for use when communicating with an untrusted party.
        /// </summary>
        public static readonly ExceptionSettings UntrustedData = new DefaultExceptionSettings(50, trusted: false);

        /// <summary>
        /// Initializes a new instance of the <see cref="ExceptionSettings"/> class.
        /// </summary>
        /// <param name="recursionLimit">The maximum number of nested errors to serialize or deserialize.</param>
        protected ExceptionSettings(int recursionLimit)
        {
            Requires.Range(recursionLimit > 0, nameof(recursionLimit));
            this.RecursionLimit = recursionLimit;
        }

        /// <summary>
        /// Gets the maximum number of nested errors to serialize or deserialize.
        /// </summary>
        /// <value>The default value is 50.</value>
        /// <remarks>
        /// This can help mitigate DoS attacks from unbounded recursion that otherwise error deserialization
        /// becomes perhaps uniquely vulnerable to since the data structure allows recursion.
        /// </remarks>
        public int RecursionLimit { get; init; }

        /// <summary>
        /// Tests whether a type can be deserialized as part of deserializing an exception.
        /// </summary>
        /// <param name="type">The type that may be deserialized.</param>
        /// <returns><see langword="true" /> if the type is safe to deserialize; <see langword="false" /> otherwise.</returns>
        /// <remarks>
        /// <para>
        /// The default implementation returns <see langword="true" /> for all types in <see cref="TrustedData"/>-based instances;
        /// or for <see cref="UntrustedData"/>-based instances will return <see langword="true" /> for
        /// <see cref="Exception"/>-derived types that are expected to be safe to deserialize.
        /// </para>
        /// <para>
        /// <see cref="Exception"/>-derived types that may deserialize data that would be unsafe coming from an untrusted party <em>should</em>
        /// consider the <see cref="StreamingContext"/> passed to their deserializing constructor and skip deserializing of potentitally
        /// dangerous data when <see cref="StreamingContext.State"/> includes the <see cref="StreamingContextStates.Remoting"/> flag.
        /// </para>
        /// </remarks>
        public abstract bool CanDeserialize(Type type);

        private record DefaultExceptionSettings : ExceptionSettings
        {
            private readonly bool trusted;

            public DefaultExceptionSettings(int recursionLimit, bool trusted)
                : base(recursionLimit)
            {
                this.trusted = trusted;
            }

            public override bool CanDeserialize(Type type) => this.trusted || typeof(Exception).IsAssignableFrom(type);
        }
    }
}
