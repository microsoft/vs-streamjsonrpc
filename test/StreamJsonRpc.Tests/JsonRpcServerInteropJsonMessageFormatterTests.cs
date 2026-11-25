// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

[Obsolete(ObsoleteFormatter)]
public class JsonRpcServerInteropJsonMessageFormatterTests : JsonRpcServerInteropTests
{
    public JsonRpcServerInteropJsonMessageFormatterTests(ITestOutputHelper logger)
        : base(new JsonMessageFormatter(), logger)
    {
    }

    [Fact]
    public Task ServerEmitsProgressNotificationsWithNamedArguments_WithNamedArgumentsInput_WithSingleObjectParameterDeserialization() => this.VerifyServerEmitsProgressNotificationsWithNamedArguments_WithNamedArgumentsInput(true);
}
