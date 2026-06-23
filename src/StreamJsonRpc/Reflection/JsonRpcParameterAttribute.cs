// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Attribute which changes the name by which this parameter is identified in JSON-RPC named arguments.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class JsonRpcParameterAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsonRpcParameterAttribute"/> class.
    /// </summary>
    /// <param name="name">Replacement name of a parameter.</param>
    public JsonRpcParameterAttribute(string name)
    {
        Requires.NotNullOrEmpty(name, nameof(name));
        this.Name = name;
    }

    /// <summary>
    /// Gets the RPC name by which this parameter will be identified in named arguments.
    /// </summary>
    public string Name { get; }
}
