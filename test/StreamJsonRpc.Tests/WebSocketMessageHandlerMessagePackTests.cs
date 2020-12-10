using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class WebSocketMessageHandlerMessagePackTests : WebSocketMessageHandlerTests
{
    public WebSocketMessageHandlerMessagePackTests(ITestOutputHelper logger)
        : base(new MessagePackFormatter(), logger)
    {
    }
}
