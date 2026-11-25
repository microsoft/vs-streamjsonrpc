// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using Nerdbank.Streams;

[Obsolete(ObsoleteFormatter)]
public class NewLineDelimitedMessageHandlerJsonMessageFormatterTests : NewLineDelimitedMessageHandlerTests
{
    public NewLineDelimitedMessageHandlerJsonMessageFormatterTests(ITestOutputHelper logger)
        : base(new JsonMessageFormatter(), logger)
    {
    }

    [Fact]
    public void UTF16_Formatter_NotSupported()
    {
        // Because the handler looks for UTF-8 encoded \n characters, it doesn't support anything else.
        Assert.Throws<NotSupportedException>(() => new NewLineDelimitedMessageHandler(FullDuplexStream.CreatePipePair().Item1, new JsonMessageFormatter(Encoding.Unicode)));
    }
}
