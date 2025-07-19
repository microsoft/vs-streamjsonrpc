# Past source-generated proxies

This folder contains checked-in copies of source generated proxies of the `JsonRpcProxyGenerationTests.ITimeTestedProxy` interface.

Each should be identical to the original from the source generator except perhaps for a type name change to prevent conflicts with the currently running source generator.

The purpose of these is to verify that past source generators continue to run correctly under the latest library versions, since it is impossible to dynamically update a source generator that is compiled into another assembly based on an older source generator.

Presence in this folder guarantees that they will compile.
The `JsonRpcProxyGenerationTests.CheckedInProxiesFromPastGenerationsStillWork` unit test runs it through functionality tests.

Whenever significant changes to source generator output are made, a sample of the new output should be added to this folder.
