// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Changes the name by which an event is raised over JSON-RPC.
/// </summary>
/// <remarks>
/// This attribute is useful when an RPC event name differs from its CLR event name or contains characters that are not valid in CLR identifiers.
/// The configured event name is passed to any applicable event name transform.
/// </remarks>
[AttributeUsage(AttributeTargets.Event, AllowMultiple = false, Inherited = true)]
public class JsonRpcEventAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsonRpcEventAttribute"/> class.
    /// </summary>
    /// <param name="name">The replacement name of the event.</param>
    public JsonRpcEventAttribute(string name)
    {
        Requires.NotNullOrEmpty(name);
        this.Name = name;
    }

    /// <summary>
    /// Gets the public RPC name by which this event will be raised.
    /// </summary>
    public string Name { get; }
}
