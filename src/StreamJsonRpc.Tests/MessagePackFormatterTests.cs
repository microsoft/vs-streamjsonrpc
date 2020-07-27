using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class MessagePackFormatterTests : TestBase
{
    private readonly MessagePackFormatter formatter = new MessagePackFormatter();

    public MessagePackFormatterTests(ITestOutputHelper logger)
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
        var clientFormatter = new MessagePackFormatter();
        var serverFormatter = new MessagePackFormatter();

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
        this.formatter.SetMessagePackSerializerOptions(MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(new CustomFormatter())));
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
        this.formatter.SetMessagePackSerializerOptions(MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(new IMessagePackFormatter[] { new CustomFormatter() }, new IFormatterResolver[] { BuiltinResolver.Instance })));
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
        this.formatter.SetMessagePackSerializerOptions(MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(new IMessagePackFormatter[] { new CustomFormatter() }, new IFormatterResolver[] { BuiltinResolver.Instance })));
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
        this.formatter.SetMessagePackSerializerOptions(MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(new IMessagePackFormatter[] { new CustomFormatter() }, new IFormatterResolver[] { BuiltinResolver.Instance })));
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
        this.formatter.SetMessagePackSerializerOptions(MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(new CustomFormatter())));
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
        this.formatter.SetMessagePackSerializerOptions(MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(new CustomFormatter())));
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

    /// <summary>
    /// Verifies that the <see cref="MessagePackSerializerOptions"/> passed to an <see cref="IMessagePackFormatter{T}"/>
    /// during serialization of user data is or derives from the value supplied to
    /// <see cref="MessagePackFormatter.SetMessagePackSerializerOptions(MessagePackSerializerOptions)"/>.
    /// </summary>
    /// <remarks>
    /// This is important because some users actually pass extra state to their formatters by way of a derivation of the options class.
    /// Modifying their options is fine so long as it is done using the <see cref="MessagePackSerializerOptions.Clone"/> method
    /// so that the instance is still their type with any custom properties copied.
    /// </remarks>
    [Fact]
    public void ActualOptions_IsOrDerivesFrom_SetMessagePackSerializerOptions()
    {
        var customFormatter = new CustomFormatter();
        var options = (CustomOptions)new CustomOptions(MessagePackFormatter.DefaultUserDataSerializationOptions) { CustomProperty = 3 }
            .WithResolver(CompositeResolver.Create(customFormatter));
        this.formatter.SetMessagePackSerializerOptions(options);
        var value = new JsonRpcRequest
        {
            RequestId = new RequestId(1),
            Method = "Eat",
            ArgumentsList = new object[] { new TypeRequiringCustomFormatter() },
        };

        var sequence = new Sequence<byte>();
        this.formatter.Serialize(sequence, value);

        var observedOptions = Assert.IsType<CustomOptions>(customFormatter.LastObservedOptions);
        Assert.Equal(options.CustomProperty, observedOptions.CustomProperty);
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

    private T Read<T>(object anonymousObject)
        where T : JsonRpcMessage
    {
        var sequence = new Sequence<byte>();
        var writer = new MessagePackWriter(sequence);
        MessagePackSerializer.Serialize(ref writer, anonymousObject, MessagePackSerializerOptions.Standard);
        writer.Flush();
        return (T)this.formatter.Deserialize(sequence);
    }

    private T Roundtrip<T>(T value)
        where T : JsonRpcMessage
    {
        var sequence = new Sequence<byte>();
        this.formatter.Serialize(sequence, value);
        var actual = (T)this.formatter.Deserialize(sequence);
        return actual;
    }

    [DataContract]
    public class CustomType
    {
        [DataMember]
        public int Age { get; set; }
    }

    [DataContract]
    private class DataContractWithSubsetOfMembersIncluded
    {
        public string? ExcludedField;

        [DataMember]
        internal string? IncludedField;

        public string? ExcludedProperty { get; set; }

        [DataMember]
        internal string? IncludedProperty { get; set; }
    }

    private class NonDataContractWithExcludedMembers
    {
        [IgnoreDataMember]
        public string? ExcludedField;

        public string? PublicField;

        internal string? InternalField;

        [IgnoreDataMember]
        public string? ExcludedProperty { get; set; }

        public string? PublicProperty { get; set; }

        internal string? InternalProperty { get; set; }
    }

    private class TypeRequiringCustomFormatter
    {
        internal int Prop1 { get; set; }

        internal int Prop2 { get; set; }
    }

    private class CustomFormatter : IMessagePackFormatter<TypeRequiringCustomFormatter>
    {
        internal MessagePackSerializerOptions? LastObservedOptions { get; private set; }

        public TypeRequiringCustomFormatter Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            this.LastObservedOptions = options;
            Assert.Equal(2, reader.ReadArrayHeader());
            return new TypeRequiringCustomFormatter
            {
                Prop1 = reader.ReadInt32(),
                Prop2 = reader.ReadInt32(),
            };
        }

        public void Serialize(ref MessagePackWriter writer, TypeRequiringCustomFormatter value, MessagePackSerializerOptions options)
        {
            this.LastObservedOptions = options;
            writer.WriteArrayHeader(2);
            writer.Write(value.Prop1);
            writer.Write(value.Prop2);
        }
    }

    private class Server
    {
        public int Add(int a, int b) => a + b;
    }

    private class CustomOptions : MessagePackSerializerOptions
    {
        internal CustomOptions(CustomOptions copyFrom)
            : base(copyFrom)
        {
            this.CustomProperty = copyFrom.CustomProperty;
        }

        internal CustomOptions(MessagePackSerializerOptions copyFrom)
            : base(copyFrom)
        {
        }

        internal int CustomProperty { get; set; }

        protected override MessagePackSerializerOptions Clone() => new CustomOptions(this);
    }
}
