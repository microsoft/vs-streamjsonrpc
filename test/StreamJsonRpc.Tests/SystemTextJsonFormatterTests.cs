// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nerdbank.Streams;

public class SystemTextJsonFormatterTests : FormatterTestBase<SystemTextJsonFormatter>
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

    [Fact]
    public void STJPositionalArgsInJsonRpcEventSource()
    {
        // Create a mock request.
        string jsonRequest = """
            {
                "jsonrpc":"2.0",
                "id":3,
                "method":"someMethod",
                "params":
                {
                    "someValue": 5
                }
            }
            """;
        ReadOnlySequence<byte> jsonSequence = new ReadOnlySequence<byte>(this.Formatter.Encoding.GetBytes(jsonRequest));
        var jsonMessage = (JsonRpcRequest)this.Formatter.Deserialize(jsonSequence);

        // JsonRpcEventSource calls TryGetArgumentByNameOrIndex to get a string representation of the message.
        // It is possible to get a positional parameters message (name is null) with a single arguments represented as a JsonValueKind.Object.
        Assert.True(jsonMessage.TryGetArgumentByNameOrIndex(null, 0, null, out object? value));
        Assert.IsType<JsonElement>(value);

        Assert.Equal(5, ((JsonElement)value).GetProperty("someValue").GetInt32());
    }

    protected override SystemTextJsonFormatter CreateFormatter() => new();

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
}
