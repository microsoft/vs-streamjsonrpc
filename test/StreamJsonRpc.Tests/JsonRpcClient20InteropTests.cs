// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Verifies the <see cref="JsonRpc"/> class's functionality as a JSON-RPC 2.0 *client* (i.e. the one sending requests, and receiving results)
/// against various server messages.
/// </summary>
public class JsonRpcClient20InteropTests : InteropTestBase
{
    private readonly JsonRpc clientRpc;

    public JsonRpcClient20InteropTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.clientRpc = new JsonRpc(this.messageHandler)
        {
            TraceSource =
            {
                Switch = { Level = SourceLevels.Verbose },
                Listeners = { new XunitTraceListener(logger) },
            },
        };
        this.clientRpc.StartListening();
    }

    [Fact]
    public async Task CancelMessageNotSentAfterResponseIsReceived()
    {
        using (var cts = new CancellationTokenSource())
        {
            Task invokeTask = this.clientRpc.InvokeWithCancellationAsync("test", cancellationToken: cts.Token);
            dynamic request = await this.ReceiveAsync();
            this.Send(new
            {
                jsonrpc = "2.0",
                id = request.id,
                result = new { },
            });
            await invokeTask;

            // Now cancel the request that has already resolved.
            cts.Cancel();

            // Verify that no cancellation message is transmitted.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.messageHandler.WrittenMessages.DequeueAsync(ExpectedTimeoutToken));
        }
    }

    [Fact]
    public async Task NotifyAsync_ParameterObjectSentAsArray()
    {
        Task notifyTask = this.clientRpc.NotifyAsync("test", new { Bar = "value" });
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Equal("value", ((JArray)request["params"])[0]["Bar"].ToString());
    }

    [Fact]
    public async Task NotifyAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.NotifyAsync("test");
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Empty((JArray)request["params"]);
    }

    [Fact]
    public async Task NotifyWithParameterPassedAsObjectAsync_ParameterObjectSentAsObject()
    {
        Task notifyTask = this.clientRpc.NotifyWithParameterObjectAsync("test", new { Bar = "value" });
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Object, request["params"].Type);
        Assert.Equal("value", request["params"]["Bar"].ToString());
    }

    [Fact]
    public async Task NotifyWithParameterPassedAsObjectAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.NotifyWithParameterObjectAsync("value");
        JToken request = await this.ReceiveAsync();
        Assert.Null(request["params"]);
    }

    [Fact]
    public async Task InvokeAsync_ParameterObjectSentAsArray()
    {
        Task notifyTask = this.clientRpc.InvokeAsync<object>("test", new { Bar = "value" });
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Equal("value", ((JArray)request["params"])[0]["Bar"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.InvokeAsync<object>("test");
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Array, request["params"].Type);
        Assert.Empty((JArray)request["params"]);
    }

    [Fact]
    public async Task SerializeWithNoWhitespace()
    {
        Task notifyTask = this.clientRpc.NotifyAsync("test");
        JToken jtoken = await this.messageHandler.WrittenMessages.DequeueAsync(this.TimeoutToken);
        string json = jtoken.ToString(Formatting.None);
        this.Logger.WriteLine(json);
        Assert.Equal(@"{""jsonrpc"":""2.0"",""method"":""test"",""params"":[]}", json);
    }

    [Fact]
    public async Task SerializeWithPrettyFormatting()
    {
        Task notifyTask = this.clientRpc.NotifyAsync("test");
        JToken jtoken = await this.messageHandler.WrittenMessages.DequeueAsync(this.TimeoutToken);
        string json = jtoken.ToString();
        this.Logger.WriteLine(json);
        string expected = "{\n  \"jsonrpc\": \"2.0\",\n  \"method\": \"test\",\n  \"params\": []\n}"
            .Replace("\n", Environment.NewLine);
        Assert.Equal(expected, json);
    }

    [Fact]
    public async Task InvokeWithParameterPassedAsObjectAsync_ParameterObjectSentAsObject()
    {
        Task notifyTask = this.clientRpc.InvokeWithParameterObjectAsync<object>("test", new { Bar = "value" });
        JToken request = await this.ReceiveAsync();
        Assert.Equal(JTokenType.Object, request["params"].Type);
        Assert.Equal("value", request["params"]["Bar"].ToString());
    }

    [Fact]
    public async Task InvokeWithParameterPassedAsObjectAsync_NoParameter()
    {
        Task notifyTask = this.clientRpc.InvokeWithParameterObjectAsync<object>("test");
        JToken request = await this.ReceiveAsync();
        Assert.Null(request["params"]);
    }

    [Fact]
    public async Task InvokeWithProgressParameterAsArray()
    {
        AsyncAutoResetEvent signal = new AsyncAutoResetEvent();

        int sum = 0;
        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(report =>
        {
            sum += report;
            signal.Set();
        });

        int n = 3;
        Task<int> invokeTask = this.clientRpc.InvokeAsync<int>("test", new object[] { n, progress });

        JToken request = await this.ReceiveAsync();

        JToken progressID = request["params"][1];

        // Send responses as $/progress
        int sum2 = 0;
        for (int i = 1; i <= n; i++)
        {
            string content = "{ \"jsonrpc\": \"2.0\", \"method\": \"$/progress\", \"params\": { \"token\": " + progressID + ", \"value\": " + i + " } }";
            JObject json = JObject.Parse(content);

            this.Send(json);

            sum2 += i;

            await signal.WaitAsync().WithCancellation(this.TimeoutToken);
            Assert.Equal(sum2, sum);
        }

        this.Send(new
        {
            jsonrpc = "2.0",
            id = request["id"],
            result = sum,
        });

        int result = await invokeTask;
        Assert.Equal(sum2, result);
    }

    [Fact]
    public async Task InvokeWithProgressParameter()
    {
        AsyncAutoResetEvent signal = new AsyncAutoResetEvent();
        int sum = 0;

        ProgressWithCompletion<int> progress = new ProgressWithCompletion<int>(report =>
        {
            sum += report;
            signal.Set();
        });

        int n = 3;
        Task<int> invokeTask = this.clientRpc.InvokeWithParameterObjectAsync<int>("test", new { Bar = n, Progress = progress });

        JToken request = await this.ReceiveAsync();

        long progressID = request["params"]["Progress"].Value<long>();

        // Send responses as $/progress
        int sum2 = 0;
        for (int i = 0; i < n; i++)
        {
            string content = "{ \"jsonrpc\": \"2.0\", \"method\": \"$/progress\", \"params\": { \"token\": " + progressID + ", \"value\": " + i + "} }";
            JObject json = JObject.Parse(content);

            this.Send(json);

            sum2 += i;

            await signal.WaitAsync().WithCancellation(this.TimeoutToken);
            Assert.Equal(sum2, sum);
        }

        Assert.Equal(sum2, sum);

        this.Send(new
        {
            jsonrpc = "2.0",
            id = request["id"].Value<long>(),
            result = sum,
        });

        int result = await invokeTask;
        Assert.Equal(sum2, result);
    }

    [Fact]
    public async Task NotifyWithParameterPassedAsObjectAsync_ThrowsExceptions()
    {
        var ex = await Assert.ThrowsAsync<JsonSerializationException>(() => this.clientRpc.NotifyWithParameterObjectAsync("test", new int[] { 1, 2 }));
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public async Task CancelMessageSentAndResponseCompletes()
    {
        using (var cts = new CancellationTokenSource())
        {
            Task<string> invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>("test", cancellationToken: cts.Token);

            // Wait for the request to actually be transmitted.
            dynamic request = await this.ReceiveAsync();

            // Now cancel the request.
            cts.Cancel();

            dynamic cancellationRequest = await this.ReceiveAsync();
            Assert.Equal("$/cancelRequest", (string)cancellationRequest.method);
            Assert.Equal(request.id, cancellationRequest.@params.id);
            Assert.Null(cancellationRequest.id);

            // Now send the response for the request (which we emulate not responding to cancellation)
            this.Send(new
            {
                jsonrpc = "2.0",
                id = request.id,
                result = "testSucceeded",
            });
            string stringResult = await invokeTask;
            Assert.Equal("testSucceeded", stringResult);
        }
    }

    [Fact]
    public async Task ErrorResponseIncludesCallstack()
    {
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        var errorObject = new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = -32000,
                message = "Object reference not set to an instance of an object.",
                data = new
                {
                    stack = "   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.GetProjectScope()\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<DetermineAllSymbolsCoreAsync>d__20.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<DetermineAllSymbolsAsync>d__19.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<FindReferencesAsync>d__10.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<FindReferencesAsync>d__10.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.<FindReferencesAsync>d__13.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.DocumentHighlighting.AbstractDocumentHighlightsService.<GetTagsForReferencedSymbolAsync>d__4.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.DocumentHighlighting.AbstractDocumentHighlightsService.<GetDocumentHighlightsInCurrentProcessAsync>d__2.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.DocumentHighlighting.AbstractDocumentHighlightsService.<GetDocumentHighlightsAsync>d__0.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.Remote.CodeAnalysisService.<GetDocumentHighlightsAsync>d__5.MoveNext()",
                    code = "-2147467261",
                },
            },
        };
        this.Send(errorObject);
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
        var commonErrorData = ((JToken)ex.ErrorData!).ToObject<CommonErrorData>();
        Assert.Equal(errorObject.error.data.stack, commonErrorData.StackTrace);
    }

    [Fact]
    public async Task ErrorResponseIncludesCallstackAndNumericErrorCode()
    {
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        var errorObject = new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = -32000,
                message = "Object reference not set to an instance of an object.",
                data = new
                {
                    stack = "   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.GetProjectScope()\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<DetermineAllSymbolsCoreAsync>d__20.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<DetermineAllSymbolsAsync>d__19.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<FindReferencesAsync>d__10.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()\r\n   at Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchEngine.<FindReferencesAsync>d__10.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.<FindReferencesAsync>d__13.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.DocumentHighlighting.AbstractDocumentHighlightsService.<GetTagsForReferencedSymbolAsync>d__4.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.DocumentHighlighting.AbstractDocumentHighlightsService.<GetDocumentHighlightsInCurrentProcessAsync>d__2.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.DocumentHighlighting.AbstractDocumentHighlightsService.<GetDocumentHighlightsAsync>d__0.MoveNext()\r\n--- End of stack trace from previous location where exception was thrown ---\r\n   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\r\n   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)\r\n   at Microsoft.CodeAnalysis.Remote.CodeAnalysisService.<GetDocumentHighlightsAsync>d__5.MoveNext()",
                    code = -2147467261,
                },
            },
        };
        this.Send(errorObject);
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
        var commonErrorData = ((JToken?)ex.ErrorData)!.ToObject<CommonErrorData>();
        Assert.Equal(errorObject.error.data.stack, commonErrorData.StackTrace);
        Assert.Equal(-2147467261, commonErrorData.HResult);
    }

    [Fact]
    public async Task ErrorResponseUsesUnexpectedDataTypes()
    {
        var errorData = new
        {
            stack = new { foo = 3 },
            code = new { bar = "-2147467261" },
        };
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        var errorObject = new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = -32000,
                message = "Object reference not set to an instance of an object.",
                data = errorData,
            },
        };
        this.Send(errorObject);
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
        JToken errorDataToken = (JToken)ex.ErrorData!;
        Assert.Throws<JsonReaderException>(() => errorDataToken.ToObject<CommonErrorData>());
        Assert.Equal(errorData.stack.foo, errorDataToken["stack"].Value<int>("foo"));
    }

    [Fact]
    public async Task ErrorResponseOmitsFieldsInDataObject()
    {
        const int expectedCode = -32000;
        const string expectedMessage = "Object reference not set to an instance of an object.";
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        this.Send(new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = expectedCode,
                message = expectedMessage,
                data = new
                {
                },
            },
        });
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
        Assert.Equal(expectedMessage, ex.Message);
        var errorData = Assert.IsType<JObject>(ex.ErrorData);
        Assert.Empty(errorData.Properties());
    }

    [Fact]
    public async Task ErrorResponseDataFieldIsPrimitive()
    {
        string expectedMessage = "Object reference not set to an instance of an object.";
        string expectedData = "An error occurred.";
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        this.Send(new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = -32000,
                message = expectedMessage,
                data = expectedData,
            },
        });
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
        Assert.Equal(expectedMessage, ex.Message);
        Assert.Equal(expectedData, ((JToken?)ex.ErrorData)?.Value<string>());
    }

    [Fact]
    public async Task ErrorResponseOmitsDataObject()
    {
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        this.Send(new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = -32000,
                message = "Object reference not set to an instance of an object.",
            },
        });
        await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
    }

    /// <summary>
    /// Some lesser JSON-RPC servers may convert the request ID from the JSON number that we sent to a string.
    /// Reproduce that to verify that our client functionality is resilient enough to withstand that bad behavior.
    /// </summary>
    [Fact]
    public async Task ServerReturnsOurRequestIdAsString()
    {
        var invokeTask = this.clientRpc.InvokeWithCancellationAsync<string>("test", cancellationToken: this.TimeoutToken);
        dynamic request = await this.ReceiveAsync();
        this.Send(new
        {
            jsonrpc = "2.0",
            id = request.id.ToString(), // deliberately return the request id as a string instead of an integer.
            result = "pass",
        });

        string result = await invokeTask.WithCancellation(this.TimeoutToken);
        Assert.Equal("pass", result);
    }

    [Fact]
    public async Task ServerSendsErrorResponseWithoutRequest()
    {
        // This represents an invalid JSON-RPC communication, because errors are not supposed to be sent except in response to a prior request.
        // But in order to document how JsonRpc responds to it, we test it here.
        var args = new TaskCompletionSource<JsonRpcDisconnectedEventArgs>();
        this.clientRpc.Disconnected += (s, e) => args.SetResult(e);

        this.Send(new
        {
            jsonrpc = "2.0",
            error = new
            {
                code = -1,
                message = "Some message",
            },
            id = (object?)null,
        });

        // Verify that the connection drops.
        await this.clientRpc.Completion.WithCancellation(this.TimeoutToken);
        var eventArgs = await args.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(DisconnectedReason.RemoteProtocolViolation, eventArgs.Reason);
    }

    [Fact]
    public async Task SerializableExceptions_AssemblyVersionMismatch()
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;

        var modifiedAssemblyName = new AssemblyName(typeof(PrivateSerializableException).Assembly.FullName!);
        modifiedAssemblyName.Version = new Version(modifiedAssemblyName.Version!.Major, modifiedAssemblyName.Version.Minor + 1, modifiedAssemblyName.Version.Build, modifiedAssemblyName.Version.Revision);
        string expectedMessage = "Some test exception message.";
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        this.Send(new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = JsonRpcErrorCode.InvocationErrorWithException,
                message = expectedMessage,
                data = new
                {
                    ClassName = typeof(PrivateSerializableException).FullName,
                    Message = expectedMessage,
                    Data = (object?)null,
                    InnerException = (object?)null,
                    HelpURL = (string?)null,
                    StackTraceString = (string?)null,
                    RemoteStackTraceString = (string?)null,
                    RemoteStackIndex = 0,
                    ExceptionMethod = string.Empty,
                    HResult = -2146233088,
                    Source = "StreamJsonRpc.Tests",
                    AssemblyName = modifiedAssemblyName.FullName,
                },
            },
        });
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
        Assert.IsType<PrivateSerializableException>(ex.InnerException);
    }

    [Fact]
    public async Task SerializableExceptions_AssemblyNameMissing()
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;

        // Because the test runner loads the test assembly in the LoadFrom context,
        // when the JSON-RPC receiver doesn't have an assembly name to load,
        // we need to help the CLR find the assembly containing our exception type in the Load context.
        AppDomain.CurrentDomain.TypeResolve += (s, e) =>
        {
            if (e.Name == typeof(PrivateSerializableException).FullName)
            {
                return typeof(PrivateSerializableException).Assembly;
            }

            return null;
        };

        string expectedMessage = "Some test exception message.";
        var requestTask = this.clientRpc.InvokeAsync("SomeMethod");
        var remoteReceivedMessage = await this.ReceiveAsync();
        this.Send(new
        {
            jsonrpc = "2.0",
            id = remoteReceivedMessage["id"],
            error = new
            {
                code = JsonRpcErrorCode.InvocationErrorWithException,
                message = expectedMessage,
                data = new
                {
                    ClassName = typeof(PrivateSerializableException).FullName,
                    Message = expectedMessage,
                    Data = (object?)null,
                    InnerException = (object?)null,
                    HelpURL = (string?)null,
                    StackTraceString = (string?)null,
                    RemoteStackTraceString = (string?)null,
                    RemoteStackIndex = 0,
                    ExceptionMethod = string.Empty,
                    HResult = -2146233088,
                    Source = "StreamJsonRpc.Tests",
                },
            },
        });
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => requestTask);
        Assert.IsType<PrivateSerializableException>(ex.InnerException);
    }

    [Serializable]
    private class PrivateSerializableException : Exception
    {
        public PrivateSerializableException()
        {
        }

        public PrivateSerializableException(string message)
            : base(message)
        {
        }

        public PrivateSerializableException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected PrivateSerializableException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }
}
