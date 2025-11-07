// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable PolyTypeJson

using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nerdbank.Streams;

public partial class PolyTypeJsonFormatterTests : FormatterTestBase<PolyTypeJsonFormatter>
{
    public PolyTypeJsonFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

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

    protected override PolyTypeJsonFormatter CreateFormatter() => new()
    {
        JsonSerializerOptions = { TypeInfoResolver = SourceGenerationContext2.Default },
        TypeShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc_Tests.Default,
    };

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

    [GenerateShapeFor<DCSClass>]
    [GenerateShapeFor<STJClass>]
    private partial class Witness;
}
