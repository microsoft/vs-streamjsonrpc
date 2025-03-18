// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class InteropTestBase : TestBase
{
    protected readonly DirectMessageHandler messageHandler;

    public InteropTestBase(ITestOutputHelper logger)
        : base(logger)
    {
        this.messageHandler = new DirectMessageHandler();
    }

    protected void UseTypeHandling()
    {
        this.messageHandler.Formatter.JsonSerializer.TypeNameHandling = TypeNameHandling.Objects;
        this.messageHandler.Formatter.JsonSerializer.TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple;
    }

    protected ValueTask<JToken> RequestAsync(object request)
    {
        this.Send(request);
        return this.ReceiveAsync();
    }

    protected void Send(dynamic message)
    {
        Requires.NotNull(message, nameof(message));

        var json = JToken.FromObject(message, new JsonSerializer());
        this.messageHandler.MessagesToRead.Enqueue(json);
    }

    protected async ValueTask<JToken> ReceiveAsync()
    {
        JToken json = await this.messageHandler.WrittenMessages.DequeueAsync(this.TimeoutToken);
        return json;
    }
}
