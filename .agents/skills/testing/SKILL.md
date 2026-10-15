---
name: testing
description: Provides guidance and instructions for writing, running and filtering tests.
user-invocable: false
---

**IMPORTANT**: This repository uses xUnit v3 with Microsoft.Testing.Platform (MTP v2). Traditional `--filter` syntax does NOT work. Use the options below instead.

* There should generally be one test project (under the `test` directory) per shipping project (under the `src` directory). Test projects are named after the project being tested with a `.Tests` suffix.
* Tests use xUnit v3 with Microsoft.Testing.Platform (MTP v2). Traditional VSTest `--filter` syntax does NOT work.
* Skip known unstable tests with `-- --filter-not-trait "TestCategory=FailsInCloudTest"`.
* The main test project targets `net8.0`, `net9.0`, and, on Windows, `net472`.

## Running Tests

**Run all tests**:
```powershell
dotnet test --no-build -c Release
```

**Run tests for a specific test project**:
```powershell
dotnet test --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release
```

**Run a single test method**:
```powershell
dotnet test --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release -- --filter-method JsonRpcJsonHeadersTests.Attach_Null_Throws
```

**Run all tests in a test class**:
```powershell
dotnet test --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release -- --filter-class JsonRpcJsonHeadersTests
```

**Run tests with wildcard matching** (supports wildcards at beginning and/or end):
```powershell
dotnet test --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release -- --filter-method "*Pattern*"
```

**Run tests with a specific trait**:
```powershell
dotnet test --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release -- --filter-trait "TraitName=value"
```

**Exclude known unstable tests**:
```powershell
dotnet test --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release -- --filter-not-trait "TestCategory=FailsInCloudTest"
```

**Run tests for a specific framework only**:
```powershell
dotnet test --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release --framework net8.0
```

**List all available tests without running them**:
```powershell
dotnet run --project test\StreamJsonRpc.Tests\StreamJsonRpc.Tests.csproj --no-build -c Release --framework net8.0 -- --list-tests
```

**Key points about test filtering with xUnit v3 / MTP v2**:
- Options after `--` are passed to the test runner, not to `dotnet test`
- Use `--filter-method`, `--filter-class`, and `--filter-namespace` for simple filtering
- Class and method filters use fully qualified names, including the namespace when present
- Use `--filter-trait` and `--filter-not-trait` for trait-based filtering
- Traditional VSTest `--filter` expressions do NOT work
- Wildcards `*` are supported at the beginning and/or end of filter values
- Multiple inclusion filters of the same type use OR logic; different types combine with AND
- Multiple exclusion filters of the same type use AND logic
- See `--help` for query filter language for advanced scenarios
