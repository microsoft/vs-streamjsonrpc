// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;
using PolyType;
using STJ = System.Text.Json.Serialization;

namespace StreamJsonRpc.Protocol;

/// <summary>
/// A class that describes useful data that may be found in the JSON-RPC error message's error.data property.
/// </summary>
[DataContract]
[GenerateShape]
public partial class CommonErrorData
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CommonErrorData"/> class.
    /// </summary>
    [ConstructorShape]
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
        this.Inner = copyFrom.InnerException is not null ? new CommonErrorData(copyFrom.InnerException) : null;
    }

    /// <summary>
    /// Gets or sets the type of error (e.g. the full type name of the exception thrown).
    /// </summary>
    [DataMember(Order = 0, Name = "type")]
    [STJ.JsonPropertyName("type"), STJ.JsonPropertyOrder(0)]
    [PropertyShape(Name = "type", Order = 0)]
    public string? TypeName { get; set; }

    /// <summary>
    /// Gets or sets the message associated with this error.
    /// </summary>
    [DataMember(Order = 1, Name = "message")]
    [STJ.JsonPropertyName("message"), STJ.JsonPropertyOrder(1)]
    [PropertyShape(Name = "message", Order = 1)]
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the stack trace associated with the error.
    /// </summary>
    [DataMember(Order = 2, Name = "stack")]
    [STJ.JsonPropertyName("stack"), STJ.JsonPropertyOrder(2)]
    [PropertyShape(Name = "stack", Order = 2)]
    public string? StackTrace { get; set; }

    /// <summary>
    /// Gets or sets the application error code or HRESULT of the failure.
    /// </summary>
    [DataMember(Order = 3, Name = "code")]
    [STJ.JsonPropertyName("code"), STJ.JsonPropertyOrder(3)]
    [PropertyShape(Name = "code", Order = 3)]
    public int HResult { get; set; }

    /// <summary>
    /// Gets or sets the inner error information, if any.
    /// </summary>
    [DataMember(Order = 4, Name = "inner")]
    [STJ.JsonPropertyName("inner"), STJ.JsonPropertyOrder(4)]
    [PropertyShape(Name = "inner", Order = 4)]
    public CommonErrorData? Inner { get; set; }
}
