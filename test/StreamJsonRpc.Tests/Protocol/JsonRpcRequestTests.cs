// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;

public class JsonRpcRequestTests
{
    private static readonly IReadOnlyList<object> ArgumentsAsList = new List<object> { 4, 6, 8 };
    private static readonly IReadOnlyList<object> ArgumentsAsArray = new object[] { 4, 6, 8 };
    private static readonly object ArgumentsAsObject = new Dictionary<string, object> { { "Foo", 4 }, { "bar", 6 } };

    [Fact]
    public void ArgumentsCount_WithNamedArguments()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsObject,
        };
        Assert.Equal(2, request.ArgumentCount);
    }

    [Fact]
    public void ArgumentsCount_WithList()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsList,
        };
        Assert.Equal(3, request.ArgumentCount);
    }

    [Fact]
    public void ArgumentsCount_WithArray()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsArray,
        };
        Assert.Equal(3, request.ArgumentCount);
    }

    [Fact]
    public void ArgumentsList()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsList,
        };
        Assert.Same(request.Arguments, request.ArgumentsList);
        Assert.Same(ArgumentsAsList, request.ArgumentsList);
    }

    [Fact]
    public void FlexibleMatchingInvokesLegacyOverride()
    {
        var request = new LegacyJsonRpcRequest
        {
            NamedArguments = new Dictionary<string, object?> { ["unknown"] = true },
        };
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(FlexibleTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, parameterNames: default, arguments, allowFlexibleNamedArgumentMatching: true);

        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.Success, result);
        Assert.True(request.LegacyOverrideInvoked);
        Assert.Equal(0, arguments[0]);
    }

    [Fact]
    public void FlexibleMatchingInvokesLegacyOverrideWithoutParameterNames()
    {
        var request = new LegacyUnnamedJsonRpcRequest
        {
            NamedArguments = new Dictionary<string, object?> { ["unknown"] = true },
        };
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(FlexibleTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, parameterNames: default, arguments, allowFlexibleNamedArgumentMatching: true);

        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.Success, result);
        Assert.True(request.LegacyOverrideInvoked);
        Assert.Equal(0, arguments[0]);
    }

    [Fact]
    public void FlexibleMatchingSupportsCustomNamedArgumentRepresentation()
    {
        var request = new CustomNamedJsonRpcRequest();
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(OptionalTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, parameterNames: default, arguments, allowFlexibleNamedArgumentMatching: true);

        Assert.Null(request.ArgumentNames);
        Assert.True(request.ArgumentsAreNamed);
        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.Success, result);
        Assert.Equal(0, arguments[0]);
    }

    [Fact]
    public void FlexibleMatchingDoesNotRelaxPositionalArgumentCount()
    {
        var request = new JsonRpcRequest
        {
            ArgumentsList = [1, 2],
        };
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(FlexibleTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, parameterNames: default, arguments, allowFlexibleNamedArgumentMatching: true);

        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.ParameterArgumentCountMismatch, result);
    }

    [Fact]
    public void FlexibleMatchingDoesNotSupplyMissingPositionalArguments()
    {
        var request = new JsonRpcRequest
        {
            ArgumentsList = [1],
        };
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(TwoArgumentTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, parameterNames: default, arguments, allowFlexibleNamedArgumentMatching: true);

        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.MissingArgument, result);
    }

    [Fact]
    public void FlexibleMatchingRequiresAttributedReflectedParameter()
    {
        var request = new JsonRpcRequest
        {
            NamedArguments = new Dictionary<string, object?>(),
        };
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(RequiredTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, parameterNames: default, arguments, allowFlexibleNamedArgumentMatching: true);

        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.MissingArgument, result);
    }

    [Fact]
    public void StrictMatchingAppliesDefaultForAttributedReflectedParameter()
    {
        var request = new JsonRpcRequest
        {
            NamedArguments = new Dictionary<string, object?>(),
        };
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(RequiredTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, parameterNames: default, arguments);

        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.Success, result);
        Assert.Equal("fallback", arguments[0]);
    }

    [Fact]
    public void StrictMatchingHonorsCustomNamedLookupSemantics()
    {
        var request = new JsonRpcRequest
        {
            NamedArguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["VALUE"] = 1 },
        };
        ParameterInfo[] parameters = typeof(JsonRpcRequestTests).GetMethod(nameof(OptionalTarget), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        JsonRpcRequest.ArgumentMatchResult result = request.TryGetTypedArguments(parameters, arguments);

        Assert.Equal(JsonRpcRequest.ArgumentMatchResult.Success, result);
        Assert.Equal(1, arguments[0]);
    }

#pragma warning disable CS0618 // Type or member is obsolete
    [Fact]
    public void ArgumentsArray_WithArray()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsArray,
        };
        Assert.Same(request.Arguments, request.ArgumentsArray);
        Assert.Same(ArgumentsAsArray, request.ArgumentsArray);
    }

    [Fact]
    public void ArgumentsArray_WithList()
    {
        var request = new JsonRpcRequest
        {
            Arguments = ArgumentsAsList,
        };
        Assert.Null(request.ArgumentsArray);
    }

    [Fact]
    public void DefaultRequestDoesNotSupportTopLevelProperties()
    {
        var request = new JsonRpcRequest();
        Assert.False(request.TrySetTopLevelProperty("test", "test"));
        Assert.False(request.TryGetTopLevelProperty<string>("test", out string? value));
    }
#pragma warning restore CS0618 // Type or member is obsolete

    [Fact]
    public void JsonRpcRequest_ToString_Works()
    {
        var data = new JsonRpcRequest
        {
            RequestId = new RequestId(10),
            Method = "t",
        };

        Assert.Equal(
            """{"id":10,"method":"t"}""",
            data.ToString());
    }

    [Fact]
    public void JsonRpcError_ToString_Works()
    {
        var data = new JsonRpcError
        {
            RequestId = new RequestId(1),
            Error = new JsonRpcError.ErrorDetail { Code = JsonRpcErrorCode.InternalError, Message = "some error" },
        };

        Assert.Equal(
            """{"id":1,"error":{"code":-32603,"message":"some error"}}""",
            data.ToString());
    }

    [Fact]
    public void JsonRpcResult_ToString_Works()
    {
        var data = new JsonRpcResult
        {
            RequestId = new RequestId("id"),
        };

        Assert.Equal(
            """{"id":"id"}""",
            data.ToString());
    }

    private static void FlexibleTarget(int value)
    {
    }

    private static void TwoArgumentTarget(int first, int second)
    {
    }

    private static void OptionalTarget(int value = 0)
    {
    }

    private static void RequiredTarget([System.ComponentModel.DataAnnotations.Required] string value = "fallback")
    {
    }

    private sealed class LegacyJsonRpcRequest : JsonRpcRequest
    {
        internal bool LegacyOverrideInvoked { get; private set; }

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, ReadOnlySpan<string?> parameterNames, Span<object?> typedArguments)
        {
            this.LegacyOverrideInvoked = true;
            return base.TryGetTypedArguments(parameters, parameterNames, typedArguments);
        }
    }

    private sealed class LegacyUnnamedJsonRpcRequest : JsonRpcRequest
    {
        internal bool LegacyOverrideInvoked { get; private set; }

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
        {
            this.LegacyOverrideInvoked = true;
            return base.TryGetTypedArguments(parameters, typedArguments);
        }
    }

    private sealed class CustomNamedJsonRpcRequest : JsonRpcRequest
    {
        public override int ArgumentCount => 1;

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            value = true;
            return name == "unknown";
        }
    }
}
