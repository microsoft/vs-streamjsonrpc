﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nerdbank.Streams;

public partial class SystemTextJsonFormatterTests : FormatterTestBase<SystemTextJsonFormatter>
{
    public SystemTextJsonFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    ////[Fact]
    ////public void DataContractAttributesWinOverSTJAttributes()
    ////{
    ////    this.Formatter = new SystemTextJsonFormatter
    ////    {
    ////        JsonSerializerOptions =
    ////        {
    ////            TypeInfoResolver = new SystemTextJsonFormatter.DataContractResolver(),
    ////        },
    ////    };
    ////    IJsonRpcMessageFactory messageFactory = this.Formatter;
    ////    JsonRpcRequest requestMessage = messageFactory.CreateRequestMessage();
    ////    requestMessage.Method = "test";
    ////    requestMessage.Arguments = new[] { new DCSClass { C = 1 } };

    ////    using Sequence<byte> sequence = new();
    ////    this.Formatter.Serialize(sequence, requestMessage);

    ////    using JsonDocument doc = JsonDocument.Parse(sequence);
    ////    this.Logger.WriteLine(doc.RootElement.ToString());
    ////    Assert.Equal(1, doc.RootElement.GetProperty("params")[0].GetProperty("A").GetInt32());
    ////}

    [Fact]
    public void STJAttributesWinOverDataContractAttributesByDefault()
    {
        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest requestMessage = messageFactory.CreateRequestMessage();
        requestMessage.Method = "test";
        requestMessage.Arguments = new[] { new DCSClass { C = 1 } };

        using Sequence<byte> sequence = new();
        this.Formatter.Serialize(sequence, requestMessage);

        using JsonDocument doc = JsonDocument.Parse(sequence);
        this.Logger.WriteLine(doc.RootElement.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("params")[0].GetProperty("B").GetInt32());
    }

    [Fact]
    public void STJAttributesWinOverDataMemberWithoutDataContract()
    {
        IJsonRpcMessageFactory messageFactory = this.Formatter;
        JsonRpcRequest requestMessage = messageFactory.CreateRequestMessage();
        requestMessage.Method = "test";
        requestMessage.Arguments = new[] { new STJClass { C = 1 } };

        using Sequence<byte> sequence = new();
        this.Formatter.Serialize(sequence, requestMessage);

        using JsonDocument doc = JsonDocument.Parse(sequence);
        this.Logger.WriteLine(doc.RootElement.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("params")[0].GetProperty("B").GetInt32());
    }

    protected override SystemTextJsonFormatter CreateFormatter() => new() { JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext2.Default } };

    [DataContract]
    public class DCSClass
    {
        [DataMember(Name = "A")]
        [JsonPropertyName("B")]
        public int C { get; set; }
    }

    public class STJClass
    {
        [DataMember(Name = "A")]
        [JsonPropertyName("B")]
        public int C { get; set; }
    }

    [JsonSerializable(typeof(DCSClass))]
    [JsonSerializable(typeof(STJClass))]
    [JsonSerializable(typeof(CustomType))]
    [JsonSerializable(typeof(string))]
    private partial class SourceGenerationContext2 : JsonSerializerContext;
}
