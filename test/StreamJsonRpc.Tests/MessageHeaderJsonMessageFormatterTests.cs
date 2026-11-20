// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;

[Obsolete(ObsoleteFormatter)]
public class MessageHeaderJsonMessageFormatterTests : MessageHeaderTests
{
    public MessageHeaderJsonMessageFormatterTests(ITestOutputHelper logger)
        : base(new JsonMessageFormatter(), logger)
    {
    }

    [Fact]
    public Task ReceiveMessageWithUtf16Encoding() => this.ReceiveMessageWithEncodingAsync(Encoding.Unicode);

    [Fact]
    public Task SendMessageWithUtf16Encoding() => this.SendMessageWithEncodingAsync(Encoding.Unicode);
}
