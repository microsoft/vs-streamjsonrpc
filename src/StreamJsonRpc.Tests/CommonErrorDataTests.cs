using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class CommonErrorDataTests : TestBase
{
    public CommonErrorDataTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void Ctor_CopyFrom_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new CommonErrorData(null!));
    }

    [Fact]
    public void Ctor_CopyFrom_NeverThrown()
    {
        var template = new CustomException(5, "msg");
        var errorData = new CommonErrorData(template);
        Assert.Equal("msg", template.Message);
        Assert.Equal(template.HResult, errorData.HResult);
        Assert.Null(errorData.Inner);
        Assert.Equal(typeof(CustomException).FullName, errorData.TypeName);
        Assert.Null(errorData.StackTrace);
    }

    [Fact]
    public void Ctor_CopyFrom_ThrownStackTrace()
    {
        try
        {
            throw new CustomException(5, "msg");
        }
        catch (Exception template)
        {
            var errorData = new CommonErrorData(template);
            Assert.Equal("msg", template.Message);
            Assert.Equal(template.HResult, errorData.HResult);
            Assert.Null(errorData.Inner);
            Assert.Equal(typeof(CustomException).FullName, errorData.TypeName);
            Assert.NotNull(errorData.StackTrace);
            this.Logger.WriteLine(errorData.StackTrace);
        }
    }

    [Fact]
    public void Ctor_CopyFrom_InnerExceptions()
    {
        var template = new InvalidOperationException("outer", new InvalidCastException("inner"));
        var errorData = new CommonErrorData(template);

        Assert.Equal(template.GetType().FullName, errorData.TypeName);
        Assert.Equal(template.Message, errorData.Message);
        Assert.Equal(template.InnerException.GetType().FullName, errorData.Inner?.TypeName);
        Assert.Equal(template.InnerException.Message, errorData.Inner?.Message);
    }

    private class CustomException : Exception
    {
        internal CustomException(int hresult, string message)
            : base(message)
        {
            this.HResult = hresult;
        }
    }
}
