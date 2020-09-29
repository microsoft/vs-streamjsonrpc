using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class LocalRpcExceptionTests : TestBase
{
    public LocalRpcExceptionTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void ErrorCode()
    {
        var ex = new LocalRpcException() { ErrorCode = 1 };
        Assert.Equal(1, ex.ErrorCode);
    }

    [Fact]
    public void ErrorData_AnonymousType()
    {
        var ex = new LocalRpcException() { ErrorData = new { myError = 5 } };
        dynamic error = ex.ErrorData;
        Assert.Equal(5, (int)error.myError);
    }
}
