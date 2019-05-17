// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Runtime.Serialization;
    using Microsoft;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Describes a method to be invoked on the server.
    /// </summary>
    [DataContract]
    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    public class JsonRpcRequest : JsonRpcMessage, IJsonRpcMessageWithId
    {
        /// <summary>
        /// The result of an attempt to match request arguments with a candidate method's parameters.
        /// </summary>
        public enum ArgumentMatchResult
        {
            /// <summary>
            /// All arguments matched up with all method parameters.
            /// </summary>
            Success,

            /// <summary>
            /// The number of arguments did not match the number of parameters.
            /// </summary>
            ParameterArgumentCountMismatch,

            /// <summary>
            /// At least one argument could not be made to match its corresponding parameter.
            /// </summary>
            ParameterArgumentTypeMismatch,

            /// <summary>
            /// An argument could not be found for a required parameter.
            /// </summary>
            MissingArgument,
        }

        /// <summary>
        /// Gets or sets the name of the method to be invoked.
        /// </summary>
        [DataMember(Name = "method", Order = 1, IsRequired = true)]
        public string Method { get; set; }

        /// <summary>
        /// Gets or sets the arguments to use when invoking the specified <see cref="Method"/>.
        /// Either an array of arguments or an object whose properties are used in a named arguments object.
        /// </summary>
        /// <value>
        /// An array of arguments OR map of named arguments.
        /// Preferably either an instance of <see cref="IReadOnlyDictionary{TKey, TValue}"/> where the key is a string representing the name of the parameter
        /// and the value is the argument, or an array of <see cref="object"/>.
        /// If neither of these, <see cref="ArgumentCount"/> and <see cref="TryGetArgumentByNameOrIndex(string, int, Type, out object)"/> should be overridden.
        /// </value>
        [DataMember(Name = "params", Order = 2, IsRequired = false, EmitDefaultValue = false)]
        public object Arguments { get; set; }

        /// <summary>
        /// Gets or sets an identifier established by the client if a response to the request is expected.
        /// </summary>
        /// <value>A <see cref="string"/>, an <see cref="int"/>, a <see cref="long"/>, or <c>null</c>.</value>
        [DataMember(Name = "id", Order = 3, IsRequired = false, EmitDefaultValue = false)]
        public object Id { get; set; }

        /// <summary>
        /// Gets a value indicating whether a response to this request is expected.
        /// </summary>
        [IgnoreDataMember]
        public bool IsResponseExpected => this.Id != null;

        /// <summary>
        /// Gets a value indicating whether this is a notification, and no response is expected.
        /// </summary>
        [IgnoreDataMember]
        public bool IsNotification => this.Id == null;

        /// <summary>
        /// Gets the number of arguments supplied in the request.
        /// </summary>
        [IgnoreDataMember]
        public int ArgumentCount => this.NamedArguments?.Count ?? this.ArgumentsList?.Count ?? 0;

        /// <summary>
        /// Gets or sets the dictionary of named arguments, if applicable.
        /// </summary>
        [IgnoreDataMember]
        public IReadOnlyDictionary<string, object> NamedArguments
        {
            get => this.Arguments as IReadOnlyDictionary<string, object>;
            set => this.Arguments = value;
        }

        /// <summary>
        /// Gets or sets an array of arguments, if applicable.
        /// </summary>
        [IgnoreDataMember]
        [Obsolete("Use " + nameof(ArgumentsList) + " instead.")]
        public object[] ArgumentsArray
        {
            get => this.Arguments as object[];
            set => this.Arguments = value;
        }

        /// <summary>
        /// Gets or sets a read only list of arguments, if applicable.
        /// </summary>
        [IgnoreDataMember]
        public IReadOnlyList<object> ArgumentsList
        {
            get => this.Arguments as IReadOnlyList<object>;
            set => this.Arguments = value;
        }

        /// <summary>
        /// Gets the string to display in the debugger for this instance.
        /// </summary>
        protected string DebuggerDisplay => (this.Id != null ? $"Request {this.Id}" : "Notification") + $": {this.Method}({this.Arguments})";

        /// <summary>
        /// Gets the arguments to supply to the method invocation, coerced to types that will satisfy the given list of parameters.
        /// </summary>
        /// <param name="parameters">The list of parameters that the arguments must satisfy.</param>
        /// <param name="typedArguments">
        /// An array to initialize with arguments that can satisfy CLR type requirements for each of the <paramref name="parameters"/>.
        /// The length of this span must equal the length of <paramref name="parameters"/>.
        /// </param>
        /// <returns><c>true</c> if all the arguments can conform to the types of the <paramref name="parameters"/> and <paramref name="typedArguments"/> is initialized; <c>false</c> otherwise.</returns>
        public virtual ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object> typedArguments)
        {
            Requires.Argument(parameters.Length == typedArguments.Length, nameof(typedArguments), "Length of spans do not match.");

            // If we're given more arguments than parameters to hold them, that's a pretty good sign there's a method mismatch.
            if (parameters.Length < this.ArgumentCount)
            {
                return ArgumentMatchResult.ParameterArgumentCountMismatch;
            }

            if (parameters.Length == 0)
            {
                return ArgumentMatchResult.Success;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                if (this.TryGetArgumentByNameOrIndex(parameter.Name, i, parameter.ParameterType, out object argument))
                {
                    if (argument == null)
                    {
                        if (parameter.ParameterType.GetTypeInfo().IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) == null)
                        {
                            // We cannot pass a null value to a value type parameter.
                            return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                        }
                    }
                    else if (!parameter.ParameterType.GetTypeInfo().IsAssignableFrom(argument.GetType()))
                    {
                        return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                    }

                    typedArguments[i] = argument;
                }
                else if (parameter.HasDefaultValue)
                {
                    // The client did not supply an argument, but we have a default value to use, courtesy of the parameter itself.
                    typedArguments[i] = parameter.DefaultValue;
                }
                else
                {
                    return ArgumentMatchResult.MissingArgument;
                }
            }

            return ArgumentMatchResult.Success;
        }

        /// <summary>
        /// Retrieves an argument for the RPC request.
        /// </summary>
        /// <param name="name">The name of the parameter that requires an argument. May be null if the caller knows they want a positional argument.</param>
        /// <param name="position">The index of the parameter that requires an argument. May be -1 for an argument with no position.</param>
        /// <param name="typeHint">The type of the parameter that requires an argument. May be null if the type need not be coerced.</param>
        /// <param name="value">Receives the value of the argument, if it exists. It MAY be returned even if it does not conform to <paramref name="typeHint"/>.</param>
        /// <returns><c>true</c> if an argument is available for a parameter with the given name or position; <c>false</c> otherwise.</returns>
        /// <remarks>
        /// A derived-type may override this method in order to consider the <paramref name="typeHint"/>
        /// and deserialize the required argument on-demand such that it can satisfy the type requirement.
        /// </remarks>
        public virtual bool TryGetArgumentByNameOrIndex(string name, int position, Type typeHint, out object value)
        {
            if (this.NamedArguments != null && name != null)
            {
                return this.NamedArguments.TryGetValue(name, out value);
            }
            else if (this.ArgumentsList != null && position < this.ArgumentsList.Count && position >= 0)
            {
                value = this.ArgumentsList[position];
                return true;
            }

            value = default;
            return false;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return new JObject
            {
                new JProperty("id", this.Id),
                new JProperty("method", this.Method),
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
