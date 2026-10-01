// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public partial class RequestIdTests : TestBase
{
    public RequestIdTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void StringValue()
    {
        Assert.Equal("s", new RequestId("s").String);
        Assert.Null(new RequestId(null).String);
        Assert.Null(new RequestId(3).String);
    }

    [Fact]
    public void NumberValue()
    {
        Assert.Equal(3, new RequestId(3).Number);
        Assert.Null(new RequestId(null).Number);
        Assert.Null(new RequestId("3").Number);
    }

    [Fact]
    public void IsNull()
    {
        Assert.True(RequestId.Null.IsNull);
        Assert.False(RequestId.NotSpecified.IsNull);
        Assert.False(default(RequestId).IsNull);
        Assert.True(new RequestId(null).IsNull);
        Assert.False(new RequestId("string").IsNull);
        Assert.False(new RequestId(1).IsNull);
    }

    [Fact]
    public void IsEmpty()
    {
        Assert.True(RequestId.NotSpecified.IsEmpty);
        Assert.True(default(RequestId).IsEmpty);
        Assert.False(RequestId.Null.IsEmpty);
        Assert.False(new RequestId("string").IsEmpty);
        Assert.False(new RequestId(null).IsEmpty);
        Assert.False(new RequestId(1).IsEmpty);
    }

    [Fact]
    public void Equals_Method()
    {
        Assert.True(RequestId.NotSpecified.Equals(RequestId.NotSpecified));
        Assert.True(RequestId.Null.Equals(RequestId.Null));
        Assert.False(RequestId.NotSpecified.Equals(RequestId.Null));
        Assert.False(new RequestId("string").Equals(RequestId.NotSpecified));
        Assert.False(new RequestId("string").Equals(RequestId.Null));
        Assert.False(new RequestId(1).Equals(RequestId.NotSpecified));
        Assert.False(new RequestId(1).Equals(RequestId.Null));
    }

    [Fact]
    public void ToString_Method()
    {
        Assert.Equal("s", new RequestId("s").ToString());
        Assert.Equal("1", new RequestId(1).ToString());
        Assert.Equal("(null)", RequestId.Null.ToString());
        Assert.Equal("(not specified)", RequestId.NotSpecified.ToString());
    }

    /// <summary>
    /// Verifies numeric request IDs round-trip with source-generated metadata.
    /// </summary>
    /// <param name="value">The numeric request ID.</param>
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(long.MaxValue)]
    public void SourceGeneratedNumber(long value)
    {
        RequestId requestId = new(value);

        string json = JsonSerializer.Serialize(requestId, RequestIdContext.Default.RequestId);

        Assert.Equal(value.ToString(CultureInfo.InvariantCulture), json);
        Assert.Equal(requestId, JsonSerializer.Deserialize(json, RequestIdContext.Default.RequestId));
    }

    /// <summary>
    /// Verifies string request IDs round-trip with source-generated metadata.
    /// </summary>
    /// <param name="value">The string request ID.</param>
    /// <param name="expectedJson">The expected JSON string.</param>
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("request", "\"request\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    public void SourceGeneratedString(string value, string expectedJson)
    {
        RequestId requestId = new(value);

        string json = JsonSerializer.Serialize(requestId, RequestIdContext.Default.RequestId);

        Assert.Equal(expectedJson, json);
        Assert.Equal(requestId, JsonSerializer.Deserialize(json, RequestIdContext.Default.RequestId));
    }

    /// <summary>
    /// Verifies absent request IDs serialize as null with source-generated metadata.
    /// </summary>
    /// <param name="notSpecified">Whether to use an unspecified request ID instead of null.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceGeneratedNull(bool notSpecified)
    {
        RequestId requestId = notSpecified ? RequestId.NotSpecified : RequestId.Null;

        string json = JsonSerializer.Serialize(requestId, RequestIdContext.Default.RequestId);

        Assert.Equal("null", json);
        Assert.Equal(RequestId.Null, JsonSerializer.Deserialize(json, RequestIdContext.Default.RequestId));
    }

    /// <summary>
    /// Verifies source-generated metadata rejects invalid request ID tokens.
    /// </summary>
    /// <param name="json">The invalid request ID JSON.</param>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void SourceGeneratedInvalidToken(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, RequestIdContext.Default.RequestId));
    }

    /// <summary>
    /// Verifies the public converter rejects a null writer.
    /// </summary>
    [Fact]
    public void ConverterWriteRejectsNullWriter()
    {
        SystemTextJsonFormatter.RequestIdSTJsonConverter converter = new();
        Assert.Throws<ArgumentNullException>("writer", () => converter.Write(null!, new RequestId(42), new JsonSerializerOptions()));
    }

    [JsonSerializable(typeof(RequestId))]
    private partial class RequestIdContext : JsonSerializerContext
    {
    }
}
