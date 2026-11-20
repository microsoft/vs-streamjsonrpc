// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;
using Newtonsoft.Json.Linq;

public abstract class InteropTestBase : TestBase
{
    protected readonly DirectMessageHandler messageHandler;

    protected InteropTestBase(IJsonRpcMessageTextFormatter formatter, ITestOutputHelper logger)
        : base(logger)
    {
        this.Formatter = formatter;
        this.messageHandler = new DirectMessageHandler(formatter);
    }

    protected IJsonRpcMessageTextFormatter Formatter { get; }

    protected JToken ToJToken(object value)
    {
        if (value is JToken jToken)
        {
            return jToken;
        }

        if (value is JsonElement jsonElement)
        {
            return JToken.Parse(jsonElement.GetRawText());
        }

        return JToken.FromObject(value, new Newtonsoft.Json.JsonSerializer());
    }

    protected ValueTask<JToken> RequestAsync(object request)
    {
        this.Send(request);
        return this.ReceiveAsync();
    }

    protected void Send(dynamic message)
    {
        Requires.NotNull(message, nameof(message));

        var json = JToken.FromObject(message, new Newtonsoft.Json.JsonSerializer());
        this.messageHandler.MessagesToRead.Enqueue(json);
    }

    protected async ValueTask<JToken> ReceiveAsync()
    {
        JToken json = await this.messageHandler.WrittenMessages.DequeueAsync(this.TimeoutToken);
        return json;
    }
}
