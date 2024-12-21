// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using PolyType;
using JsonNET = Newtonsoft.Json.Linq;
using STJ = System.Text.Json.Serialization;

namespace StreamJsonRpc.Protocol;

/// <summary>
/// Describes a method to be invoked on the server.
/// </summary>
[DataContract]
[GenerateShape]
[DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
public partial class JsonRpcRequest : JsonRpcMessage, IJsonRpcMessageWithId
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
    [DataMember(Name = "method", Order = 2, IsRequired = true)]
    [STJ.JsonPropertyName("method"), STJ.JsonPropertyOrder(2), STJ.JsonRequired]
    [PropertyShape(Name = "method", Order = 2)]
    public string? Method { get; set; }

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
    [DataMember(Name = "params", Order = 3, IsRequired = false, EmitDefaultValue = false)]
    [STJ.JsonPropertyName("params"), STJ.JsonPropertyOrder(3), STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    [PropertyShape(Name = "params", Order = 3)]
    public object? Arguments { get; set; }

    /// <summary>
    /// Gets or sets an identifier established by the client if a response to the request is expected.
    /// </summary>
    /// <value>A <see cref="string"/>, an <see cref="int"/>, a <see cref="long"/>, or <see langword="null"/>.</value>
    [Obsolete("Use " + nameof(RequestId) + " instead.")]
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public object? Id
    {
        get => this.RequestId.ObjectValue;
        set => this.RequestId = RequestId.Parse(value);
    }

    /// <summary>
    /// Gets or sets an identifier established by the client if a response to the request is expected.
    /// </summary>
    [DataMember(Name = "id", Order = 1, IsRequired = false, EmitDefaultValue = false)]
    [STJ.JsonPropertyName("id"), STJ.JsonPropertyOrder(1), STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingDefault)]
    [PropertyShape(Name = "id", Order = 1)]
    public RequestId RequestId { get; set; }

    /// <summary>
    /// Gets a value indicating whether a response to this request is expected.
    /// </summary>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public bool IsResponseExpected => !this.RequestId.IsEmpty;

    /// <summary>
    /// Gets a value indicating whether this is a notification, and no response is expected.
    /// </summary>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public bool IsNotification => this.RequestId.IsEmpty;

    /// <summary>
    /// Gets the number of arguments supplied in the request.
    /// </summary>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public virtual int ArgumentCount => this.NamedArguments?.Count ?? this.ArgumentsList?.Count ?? 0;

    /// <summary>
    /// Gets or sets the dictionary of named arguments, if applicable.
    /// </summary>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public IReadOnlyDictionary<string, object?>? NamedArguments
    {
        get => this.Arguments as IReadOnlyDictionary<string, object?>;
        set => this.Arguments = value;
    }

    /// <summary>
    /// Gets or sets a dictionary of <see cref="Type"/> objects indexed by the property name that describe how each element in <see cref="NamedArguments"/> is expected by the server to be typed.
    /// If specified, this must have exactly the same size as <see cref="NamedArguments"/> and contain no <see langword="null"/> values.
    /// </summary>
    /// <remarks>
    /// This property is *not* serialized into the JSON-RPC message.
    /// On the client-side of an RPC call it comes from the declared property types in the parameter object.
    /// On the server-side of the RPC call it comes from the types of each parameter on the invoked RPC method.
    /// This list is used for purposes of aiding the <see cref="IJsonRpcMessageFormatter"/> in serialization.
    /// </remarks>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public IReadOnlyDictionary<string, Type>? NamedArgumentDeclaredTypes { get; set; }

    /// <summary>
    /// Gets or sets an array of arguments, if applicable.
    /// </summary>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    [Obsolete("Use " + nameof(ArgumentsList) + " instead.")]
    public object?[]? ArgumentsArray
    {
        get => this.Arguments as object[];
        set => this.Arguments = value;
    }

    /// <summary>
    /// Gets or sets a read only list of arguments, if applicable.
    /// </summary>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public IReadOnlyList<object?>? ArgumentsList
    {
        get => this.Arguments as IReadOnlyList<object?>;
        set => this.Arguments = value;
    }

    /// <summary>
    /// Gets or sets a list of <see cref="Type"/> objects that describe how each element in <see cref="ArgumentsList"/> is expected by the server to be typed.
    /// If specified, this must have exactly the same length as <see cref="ArgumentsList"/> and contain no <see langword="null"/> elements.
    /// </summary>
    /// <remarks>
    /// This property is *not* serialized into the JSON-RPC message.
    /// On the client-side of an RPC call it comes from the typed arguments supplied to the
    /// <see cref="JsonRpc.InvokeWithCancellationAsync{TResult}(string, IReadOnlyList{object?}, IReadOnlyList{Type}, System.Threading.CancellationToken)"/>
    /// method.
    /// On the server-side of the RPC call it comes from the types of each parameter on the invoked RPC method.
    /// This list is used for purposes of aiding the <see cref="IJsonRpcMessageFormatter"/> in serialization.
    /// </remarks>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public IReadOnlyList<Type>? ArgumentListDeclaredTypes { get; set; }

    /// <summary>
    /// Gets the sequence of argument names, if applicable.
    /// </summary>
    [IgnoreDataMember]
    [STJ.JsonIgnore]
    [PropertyShape(Ignore = true)]
    public virtual IEnumerable<string>? ArgumentNames => this.NamedArguments?.Keys;

    /// <summary>
    /// Gets or sets the data for the <see href="https://www.w3.org/TR/trace-context/">W3C Trace Context</see> <c>traceparent</c> value.
    /// </summary>
    [DataMember(Name = "traceparent", EmitDefaultValue = false)]
    [STJ.JsonPropertyName("traceparent"), STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    [PropertyShape(Name = "traceparent")]
    public string? TraceParent { get; set; }

    /// <summary>
    /// Gets or sets the data for the <see href="https://www.w3.org/TR/trace-context/">W3C Trace Context</see> <c>tracestate</c> value.
    /// </summary>
    [DataMember(Name = "tracestate", EmitDefaultValue = false)]
    [STJ.JsonPropertyName("tracestate"), STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    [PropertyShape(Name = "tracestate")]
    public string? TraceState { get; set; }

    /// <summary>
    /// Gets the string to display in the debugger for this instance.
    /// </summary>
    protected string DebuggerDisplay => (!this.RequestId.IsEmpty ? $"Request {this.RequestId}" : "Notification") + $": {this.Method}({this.Arguments})";

    /// <summary>
    /// Gets the arguments to supply to the method invocation, coerced to types that will satisfy the given list of parameters.
    /// </summary>
    /// <param name="parameters">The list of parameters that the arguments must satisfy.</param>
    /// <param name="typedArguments">
    /// An array to initialize with arguments that can satisfy CLR type requirements for each of the <paramref name="parameters"/>.
    /// The length of this span must equal the length of <paramref name="parameters"/>.
    /// </param>
    /// <returns><see langword="true"/> if all the arguments can conform to the types of the <paramref name="parameters"/> and <paramref name="typedArguments"/> is initialized; <see langword="false"/> otherwise.</returns>
    /// <exception cref="RpcArgumentDeserializationException">Thrown if the argument exists, but cannot be deserialized.</exception>
    public virtual ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
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
            if (this.TryGetArgumentByNameOrIndex(parameter.Name, i, parameter.ParameterType, out object? argument))
            {
                if (argument is null)
                {
                    if (parameter.ParameterType.GetTypeInfo().IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null)
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
    /// <returns><see langword="true"/> if an argument is available for a parameter with the given name or position; <see langword="false"/> otherwise.</returns>
    /// <remarks>
    /// A derived-type may override this method in order to consider the <paramref name="typeHint"/>
    /// and deserialize the required argument on-demand such that it can satisfy the type requirement.
    /// </remarks>
    /// <exception cref="RpcArgumentDeserializationException">Thrown if the argument exists, but cannot be deserialized.</exception>
    public virtual bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
    {
        if (this.NamedArguments is not null && name is not null)
        {
            return this.NamedArguments.TryGetValue(name, out value);
        }
        else if (this.ArgumentsList is not null && position < this.ArgumentsList.Count && position >= 0)
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
        return new JsonNET.JObject
        {
            new JsonNET.JProperty("id", this.RequestId.ObjectValue),
            new JsonNET.JProperty("method", this.Method),
        }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
