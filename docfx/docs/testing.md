# Testability/mockability

Testing this library or users of this library can be done without any transport
by using @Nerdbank.Streams.FullDuplexStream.CreatePair*?displayProperty=nameWithType from the the [Nerdbank.Streams](https://www.nuget.org/packages/nerdbank.streams) library in your tests
to produce a pair of full-duplex Stream objects.
