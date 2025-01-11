// Copyright (c) Microsoft Corporation. All rights reserved.

#if NETFRAMEWORK

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using Nerdbank.Streams;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class AssemblyLoadTests : TestBase
{
    public AssemblyLoadTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void JsonDoesNotLoadMessagePackDoesUnnecessarily()
    {
        AppDomain testDomain = CreateTestAppDomain();
        try
        {
            var driver = (AppDomainTestDriver)testDomain.CreateInstanceAndUnwrap(typeof(AppDomainTestDriver).Assembly.FullName, typeof(AppDomainTestDriver).FullName);

            this.PrintLoadedAssemblies(driver);

            driver.CreateNewtonsoftJsonConnection();

            this.PrintLoadedAssemblies(driver);
            driver.ThrowIfAssembliesLoaded("MessagePack");
        }
        finally
        {
            AppDomain.Unload(testDomain);
        }
    }

    [Fact]
    public void MessagePackDoesNotLoadNewtonsoftJsonUnnecessarily()
    {
        AppDomain testDomain = CreateTestAppDomain();
        try
        {
            var driver = (AppDomainTestDriver)testDomain.CreateInstanceAndUnwrap(typeof(AppDomainTestDriver).Assembly.FullName, typeof(AppDomainTestDriver).FullName);

            this.PrintLoadedAssemblies(driver);

            driver.CreateMessagePackConnection();

            this.PrintLoadedAssemblies(driver);
            driver.ThrowIfAssembliesLoaded("Newtonsoft.Json");
        }
        finally
        {
            AppDomain.Unload(testDomain);
        }
    }

    [Fact]
    public void NerdbankMessagePackDoesNotLoadNewtonsoftJsonUnnecessarily()
    {
        AppDomain testDomain = CreateTestAppDomain();
        try
        {
            var driver = (AppDomainTestDriver)testDomain.CreateInstanceAndUnwrap(typeof(AppDomainTestDriver).Assembly.FullName, typeof(AppDomainTestDriver).FullName);

            this.PrintLoadedAssemblies(driver);

            driver.CreateNerdbankMessagePackConnection();

            this.PrintLoadedAssemblies(driver);
            driver.ThrowIfAssembliesLoaded("Newtonsoft.Json");
        }
        finally
        {
            AppDomain.Unload(testDomain);
        }
    }

    [Fact]
    public void MockFormatterDoesNotLoadJsonOrMessagePackUnnecessarily()
    {
        AppDomain testDomain = CreateTestAppDomain();
        try
        {
            var driver = (AppDomainTestDriver)testDomain.CreateInstanceAndUnwrap(typeof(AppDomainTestDriver).Assembly.FullName, typeof(AppDomainTestDriver).FullName);

            this.PrintLoadedAssemblies(driver);

            driver.CreateMockFormatterConnection();

            this.PrintLoadedAssemblies(driver);
            driver.ThrowIfAssembliesLoaded("MessagePack");
            driver.ThrowIfAssembliesLoaded("Newtonsoft.Json");
        }
        finally
        {
            AppDomain.Unload(testDomain);
        }
    }

    private static AppDomain CreateTestAppDomain([CallerMemberName] string testMethodName = "") => AppDomain.CreateDomain($"Test: {testMethodName}", null, AppDomain.CurrentDomain.SetupInformation);

    private IEnumerable<string> PrintLoadedAssemblies(AppDomainTestDriver driver)
    {
        var assembliesLoaded = driver.GetLoadedAssemblyList();
        this.Logger.WriteLine($"Loaded assemblies: {Environment.NewLine}{string.Join(Environment.NewLine, assembliesLoaded.OrderBy(s => s).Select(s => "   " + s))}");
        return assembliesLoaded;
    }

#pragma warning disable CA1812 // Avoid uninstantiated internal classes
    private class AppDomainTestDriver : MarshalByRefObject
#pragma warning restore CA1812 // Avoid uninstantiated internal classes
    {
#pragma warning disable CA1822 // Mark members as static -- all members must be instance for marshalability

        private readonly Dictionary<string, StackTrace> loadingStacks = new Dictionary<string, StackTrace>(StringComparer.OrdinalIgnoreCase);

        public AppDomainTestDriver()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) =>
            {
                string simpleName = e.LoadedAssembly.GetName().Name;
                if (!this.loadingStacks.ContainsKey(simpleName))
                {
                    this.loadingStacks.Add(simpleName, new StackTrace(skipFrames: 2, fNeedFileInfo: true));
                }
            };
        }

        internal string[] GetLoadedAssemblyList() => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).ToArray();

        internal void ThrowIfAssembliesLoaded(params string[] assemblyNames)
        {
            foreach (string assemblyName in assemblyNames)
            {
                if (this.loadingStacks.TryGetValue(assemblyName, out StackTrace? loadingStack))
                {
                    throw new Exception($"Assembly {assemblyName} was loaded unexpectedly by the test with this stack trace: {Environment.NewLine}{loadingStack}");
                }
            }
        }

        internal void CreateNewtonsoftJsonConnection()
        {
            var jsonRpc = new JsonRpc(new HeaderDelimitedMessageHandler(FullDuplexStream.CreatePipePair().Item1, new JsonMessageFormatter()));
        }

        internal void CreateMockFormatterConnection()
        {
            var jsonRpc = new JsonRpc(new HeaderDelimitedMessageHandler(FullDuplexStream.CreatePipePair().Item1, new MockFormatter()));
        }

        internal void CreateMessagePackConnection()
        {
            var jsonRpc = new JsonRpc(new LengthHeaderMessageHandler(FullDuplexStream.CreatePipePair().Item1, new MessagePackFormatter()));
        }

        internal void CreateNerdbankMessagePackConnection()
        {
            var jsonRpc = new JsonRpc(new LengthHeaderMessageHandler(FullDuplexStream.CreatePipePair().Item1, new NerdbankMessagePackFormatter()));
        }

#pragma warning restore CA1822 // Mark members as static

        private class MockFormatter : IJsonRpcMessageFormatter
        {
            public JsonRpcMessage Deserialize(System.Buffers.ReadOnlySequence<byte> contentBuffer)
            {
                throw new NotImplementedException();
            }

            public object GetJsonText(JsonRpcMessage message)
            {
                throw new NotImplementedException();
            }

            public void Serialize(System.Buffers.IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
            {
                throw new NotImplementedException();
            }
        }
    }
}

#endif
