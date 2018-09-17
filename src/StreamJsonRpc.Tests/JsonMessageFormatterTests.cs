using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class JsonMessageFormatterTests : TestBase
{
    public JsonMessageFormatterTests(ITestOutputHelper logger)
        : base(logger)
    {
    }
}
