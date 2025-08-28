// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;

internal record GeneratorConfiguration
{
    internal static GeneratorConfiguration Default { get; } = new();

    internal bool InterceptorsEnabled { get; init; } = true;

    internal bool PublicRpcMarshalableInterfaceExtensions { get; init; } = true;

    internal string ToGlobalConfigString()
    {
        StringBuilder globalConfigBuilder = new();
        globalConfigBuilder.AppendLine("is_global = true");
        globalConfigBuilder.AppendLine();
        AddProperty("EnableStreamJsonRpcInterceptors", this.InterceptorsEnabled ? "true" : "false");
        AddProperty("PublicRpcMarshalableInterfaceExtensions", this.PublicRpcMarshalableInterfaceExtensions ? "true" : "false");

        return globalConfigBuilder.ToString();

        void AddProperty(string name, string value)
        {
            globalConfigBuilder.AppendLine($"build_property.{name} = {value}");
        }
    }
}
