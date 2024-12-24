using System.Diagnostics;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;
using PolyType.ReflectionProvider;
using PolyType.SourceGenerator;

public partial class NerdbankMessagePackFormatterTests : FormatterTestBase<NerdbankMessagePackFormatter>
{
    public NerdbankMessagePackFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void JsonRpcRequest_PositionalArgs()
    {
        var original = new JsonRpcRequest
        {
            RequestId = new RequestId(5),
            Method = "test",
            ArgumentsList = new object[] { 5, "hi", new CustomType { Age = 8 } },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(original.Method, actual.Method);

        Assert.True(actual.TryGetArgumentByNameOrIndex(null, 0, typeof(int), out object? actualArg0));
        Assert.Equal(original.ArgumentsList[0], actualArg0);

        Assert.True(actual.TryGetArgumentByNameOrIndex(null, 1, typeof(string), out object? actualArg1));
        Assert.Equal(original.ArgumentsList[1], actualArg1);

        Assert.True(actual.TryGetArgumentByNameOrIndex(null, 2, typeof(CustomType), out object? actualArg2));
        Assert.Equal(((CustomType?)original.ArgumentsList[2])!.Age, ((CustomType)actualArg2!).Age);
    }

    [Fact]
    public void JsonRpcRequest_NamedArgs()
    {
        var original = new JsonRpcRequest
        {
            RequestId = new RequestId(5),
            Method = "test",
            NamedArguments = new Dictionary<string, object?>
            {
                { "Number", 5 },
                { "Message", "hi" },
                { "Custom", new CustomType { Age = 8 } },
            },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(original.Method, actual.Method);

        Assert.True(actual.TryGetArgumentByNameOrIndex("Number", -1, typeof(int), out object? actualArg0));
        Assert.Equal(original.NamedArguments["Number"], actualArg0);

        Assert.True(actual.TryGetArgumentByNameOrIndex("Message", -1, typeof(string), out object? actualArg1));
        Assert.Equal(original.NamedArguments["Message"], actualArg1);

        Assert.True(actual.TryGetArgumentByNameOrIndex("Custom", -1, typeof(CustomType), out object? actualArg2));
        Assert.Equal(((CustomType?)original.NamedArguments["Custom"])!.Age, ((CustomType)actualArg2!).Age);
    }

    [Fact]
    public void JsonRpcResult()
    {
        var original = new JsonRpcResult
        {
            RequestId = new RequestId(5),
            Result = new CustomType { Age = 7 },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(((CustomType?)original.Result)!.Age, actual.GetResult<CustomType>().Age);
    }

    [Fact]
    public void JsonRpcError()
    {
        var original = new JsonRpcError
        {
            RequestId = new RequestId(5),
            Error = new JsonRpcError.ErrorDetail
            {
                Code = JsonRpcErrorCode.InvocationError,
                Message = "Oops",
                Data = new CustomType { Age = 15 },
            },
        };

        var actual = this.Roundtrip(original);
        Assert.Equal(original.RequestId, actual.RequestId);
        Assert.Equal(original.Error.Code, actual.Error!.Code);
        Assert.Equal(original.Error.Message, actual.Error.Message);
        Assert.Equal(((CustomType)original.Error.Data).Age, actual.Error.GetData<CustomType>().Age);
    }

    [Fact]
    public async Task BasicJsonRpc()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var clientFormatter = new NerdbankMessagePackFormatter();
        var serverFormatter = new NerdbankMessagePackFormatter();

        var clientHandler = new LengthHeaderMessageHandler(clientStream.UsePipe(), clientFormatter);
        var serverHandler = new LengthHeaderMessageHandler(serverStream.UsePipe(), serverFormatter);

        var clientRpc = new JsonRpc(clientHandler);
        var serverRpc = new JsonRpc(serverHandler, new Server());

        serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        clientRpc.StartListening();
        serverRpc.StartListening();

        int result = await clientRpc.InvokeAsync<int>(nameof(Server.Add), 3, 5).WithCancellation(this.TimeoutToken);
        Assert.Equal(8, result);
    }

    [Fact]
    public void Resolver_RequestArgInArray()
    {
        this.Formatter.SetFormatterProfile(b =>
        {
            b.RegisterConverter(new CustomConverter());
            b.AddTypeShapeProvider(ShapeProvider_StreamJsonRpc_Tests.Default);
            return b.Build();
        });

        var originalArg = new TypeRequiringCustomFormatter { Prop1 = 3, Prop2 = 5 };
        var originalRequest = new JsonRpcRequest
        {
            RequestId = new RequestId(1),
            Method = "Eat",
            ArgumentsList = new object[] { originalArg },
        };
        var roundtripRequest = this.Roundtrip(originalRequest);
        Assert.True(roundtripRequest.TryGetArgumentByNameOrIndex(null, 0, typeof(TypeRequiringCustomFormatter), out object? roundtripArgObj));
        var roundtripArg = (TypeRequiringCustomFormatter)roundtripArgObj!;
        Assert.Equal(originalArg.Prop1, roundtripArg.Prop1);
        Assert.Equal(originalArg.Prop2, roundtripArg.Prop2);
    }

    [Fact]
    public void Resolver_RequestArgInNamedArgs_AnonymousType()
    {
        this.Formatter.SetFormatterProfile(b =>
        {
            b.RegisterConverter(new CustomConverter());
            b.AddTypeShapeProvider(ShapeProvider_StreamJsonRpc_Tests.Default);
            return b.Build();
        });

        var originalArg = new { Prop1 = 3, Prop2 = 5 };
        var originalRequest = new JsonRpcRequest
        {
            RequestId = new RequestId(1),
            Method = "Eat",
            Arguments = originalArg,
        };
        var roundtripRequest = this.Roundtrip(originalRequest);
        Assert.True(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.Prop1), -1, typeof(int), out object? prop1));
        Assert.True(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.Prop2), -1, typeof(int), out object? prop2));
        Assert.Equal(originalArg.Prop1, prop1);
        Assert.Equal(originalArg.Prop2, prop2);
    }

    [Fact]
    public void Resolver_RequestArgInNamedArgs_DataContractObject()
    {
        this.Formatter.SetFormatterProfile(b =>
        {
            b.RegisterConverter(new CustomConverter());
            b.AddTypeShapeProvider(ShapeProvider_StreamJsonRpc_Tests.Default);
            return b.Build();
        });

        var originalArg = new DataContractWithSubsetOfMembersIncluded { ExcludedField = "A", ExcludedProperty = "B", IncludedField = "C", IncludedProperty = "D" };
        var originalRequest = new JsonRpcRequest
        {
            RequestId = new RequestId(1),
            Method = "Eat",
            Arguments = originalArg,
        };
        var roundtripRequest = this.Roundtrip(originalRequest);
        Assert.False(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.ExcludedField), -1, typeof(string), out object? _));
        Assert.False(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.ExcludedProperty), -1, typeof(string), out object? _));
        Assert.True(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.IncludedField), -1, typeof(string), out object? includedField));
        Assert.True(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.IncludedProperty), -1, typeof(string), out object? includedProperty));
        Assert.Equal(originalArg.IncludedProperty, includedProperty);
        Assert.Equal(originalArg.IncludedField, includedField);
    }

    [Fact]
    public void Resolver_RequestArgInNamedArgs_NonDataContractObject()
    {
        this.Formatter.SetFormatterProfile(b =>
        {
            b.RegisterConverter(new CustomConverter());
            b.AddTypeShapeProvider(ShapeProvider_StreamJsonRpc_Tests.Default);
            return b.Build();
        });

        var originalArg = new NonDataContractWithExcludedMembers { ExcludedField = "A", ExcludedProperty = "B", InternalField = "C", InternalProperty = "D", PublicField = "E", PublicProperty = "F" };
        var originalRequest = new JsonRpcRequest
        {
            RequestId = new RequestId(1),
            Method = "Eat",
            Arguments = originalArg,
        };
        var roundtripRequest = this.Roundtrip(originalRequest);
        Assert.False(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.ExcludedField), -1, typeof(string), out object? _));
        Assert.False(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.ExcludedProperty), -1, typeof(string), out object? _));
        Assert.False(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.InternalField), -1, typeof(string), out object? _));
        Assert.False(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.InternalProperty), -1, typeof(string), out object? _));
        Assert.True(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.PublicField), -1, typeof(string), out object? publicField));
        Assert.True(roundtripRequest.TryGetArgumentByNameOrIndex(nameof(originalArg.PublicProperty), -1, typeof(string), out object? publicProperty));
        Assert.Equal(originalArg.PublicProperty, publicProperty);
        Assert.Equal(originalArg.PublicField, publicField);
    }

    [Fact]
    public void Resolver_RequestArgInNamedArgs_NullObject()
    {
        var originalRequest = new JsonRpcRequest
        {
            RequestId = new RequestId(1),
            Method = "Eat",
            Arguments = null,
        };
        var roundtripRequest = this.Roundtrip(originalRequest);
        Assert.Null(roundtripRequest.Arguments);
        Assert.False(roundtripRequest.TryGetArgumentByNameOrIndex("AnythingReally", -1, typeof(string), out object? _));
    }

    [Fact]
    public void Resolver_Result()
    {
        this.Formatter.SetFormatterProfile(b =>
        {
            b.RegisterConverter(new CustomConverter());
            b.AddTypeShapeProvider(ShapeProvider_StreamJsonRpc_Tests.Default);
            return b.Build();
        });

        var originalResultValue = new TypeRequiringCustomFormatter { Prop1 = 3, Prop2 = 5 };
        var originalResult = new JsonRpcResult
        {
            RequestId = new RequestId(1),
            Result = originalResultValue,
        };
        var roundtripResult = this.Roundtrip(originalResult);
        var roundtripResultValue = roundtripResult.GetResult<TypeRequiringCustomFormatter>();
        Assert.Equal(originalResultValue.Prop1, roundtripResultValue.Prop1);
        Assert.Equal(originalResultValue.Prop2, roundtripResultValue.Prop2);
    }

    [Fact]
    public void Resolver_ErrorData()
    {
        this.Formatter.SetFormatterProfile(b =>
        {
            b.RegisterConverter(new CustomConverter());
            b.AddTypeShapeProvider(ShapeProvider_StreamJsonRpc_Tests.Default);
            return b.Build();
        });

        var originalErrorData = new TypeRequiringCustomFormatter { Prop1 = 3, Prop2 = 5 };
        var originalError = new JsonRpcError
        {
            RequestId = new RequestId(1),
            Error = new JsonRpcError.ErrorDetail
            {
                Data = originalErrorData,
            },
        };
        var roundtripError = this.Roundtrip(originalError);
        var roundtripErrorData = roundtripError.Error!.GetData<TypeRequiringCustomFormatter>();
        Assert.Equal(originalErrorData.Prop1, roundtripErrorData.Prop1);
        Assert.Equal(originalErrorData.Prop2, roundtripErrorData.Prop2);
    }

    [Fact]
    public void CanDeserializeWithExtraProperty_JsonRpcRequest()
    {
        var dynamic = new
        {
            jsonrpc = "2.0",
            method = "something",
            extra = (object?)null,
            @params = new object[] { "hi" },
        };
        var request = this.Read<JsonRpcRequest>(dynamic);
        Assert.Equal(dynamic.jsonrpc, request.Version);
        Assert.Equal(dynamic.method, request.Method);
        Assert.Equal(dynamic.@params.Length, request.ArgumentCount);
        Assert.True(request.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out object? arg));
        Assert.Equal(dynamic.@params[0], arg);
    }

    [Fact]
    public void CanDeserializeWithExtraProperty_JsonRpcResult()
    {
        var dynamic = new
        {
            jsonrpc = "2.0",
            id = 2,
            extra = (object?)null,
            result = "hi",
        };
        var request = this.Read<JsonRpcResult>(dynamic);
        Assert.Equal(dynamic.jsonrpc, request.Version);
        Assert.Equal(dynamic.id, request.RequestId.Number);
        Assert.Equal(dynamic.result, request.GetResult<string>());
    }

    [Fact]
    public void CanDeserializeWithExtraProperty_JsonRpcError()
    {
        var dynamic = new
        {
            jsonrpc = "2.0",
            id = 2,
            extra = (object?)null,
            error = new { extra = 2, code = 5 },
        };
        var request = this.Read<JsonRpcError>(dynamic);
        Assert.Equal(dynamic.jsonrpc, request.Version);
        Assert.Equal(dynamic.id, request.RequestId.Number);
        Assert.Equal(dynamic.error.code, (int?)request.Error?.Code);
    }

    [Fact]
    public void StringsInUserDataAreInterned()
    {
        var dynamic = new
        {
            jsonrpc = "2.0",
            method = "something",
            extra = (object?)null,
            @params = new object[] { "hi" },
        };
        var request1 = this.Read<JsonRpcRequest>(dynamic);
        var request2 = this.Read<JsonRpcRequest>(dynamic);
        Assert.True(request1.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out object? arg1));
        Assert.True(request2.TryGetArgumentByNameOrIndex(null, 0, typeof(string), out object? arg2));
        Assert.Same(arg2, arg1); // reference equality to ensure it was interned.
    }

    [Fact]
    public void StringValuesOfStandardPropertiesAreInterned()
    {
        var dynamic = new
        {
            jsonrpc = "2.0",
            method = "something",
            extra = (object?)null,
            @params = Array.Empty<object?>(),
        };
        var request1 = this.Read<JsonRpcRequest>(dynamic);
        var request2 = this.Read<JsonRpcRequest>(dynamic);
        Assert.Same(request1.Method, request2.Method); // reference equality to ensure it was interned.
    }

    protected override NerdbankMessagePackFormatter CreateFormatter() => new();

    private T Read<T>(object anonymousObject)
        where T : JsonRpcMessage
    {
        var sequence = new Sequence<byte>();
        var writer = new MessagePackWriter(sequence);
        new MessagePackSerializer().Serialize(ref writer, anonymousObject, ReflectionTypeShapeProvider.Default);
        writer.Flush();
        return (T)this.Formatter.Deserialize(sequence);
    }

    [DataContract]
    [GenerateShape]
    public partial class DataContractWithSubsetOfMembersIncluded
    {
        [PropertyShape(Ignore = true)]
        public string? ExcludedField;

        [DataMember]
        internal string? IncludedField;

        [PropertyShape(Ignore = true)]
        public string? ExcludedProperty { get; set; }

        [DataMember]
        internal string? IncludedProperty { get; set; }
    }

    [GenerateShape]
    public partial class NonDataContractWithExcludedMembers
    {
        [IgnoreDataMember]
        [PropertyShape(Ignore = true)]
        public string? ExcludedField;

        public string? PublicField;

        internal string? InternalField;

        [IgnoreDataMember]
        [PropertyShape(Ignore = true)]
        public string? ExcludedProperty { get; set; }

        public string? PublicProperty { get; set; }

        internal string? InternalProperty { get; set; }
    }

    [GenerateShape]
    public partial class TypeRequiringCustomFormatter
    {
        internal int Prop1 { get; set; }

        internal int Prop2 { get; set; }
    }

    private class CustomConverter : MessagePackConverter<TypeRequiringCustomFormatter>
    {
        public override TypeRequiringCustomFormatter Read(ref MessagePackReader reader, SerializationContext context)
        {
            Assert.Equal(2, reader.ReadArrayHeader());
            return new TypeRequiringCustomFormatter
            {
                Prop1 = reader.ReadInt32(),
                Prop2 = reader.ReadInt32(),
            };
        }

        public override void Write(ref MessagePackWriter writer, in TypeRequiringCustomFormatter? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            writer.WriteArrayHeader(2);
            writer.Write(value.Prop1);
            writer.Write(value.Prop2);
        }
    }

    private class Server
    {
        public int Add(int a, int b) => a + b;
    }
}
