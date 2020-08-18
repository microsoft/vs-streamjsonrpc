# StreamJsonRpc

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com)
with any additional questions or comments.

We welcome 3rd party pull requests.
For significant changes we strongly recommend opening an issue to start a design discussion first.

## Dev workflow

### Dependencies

Get the .NET Core SDK and .NET Core runtimes that are required to build and test this repo by running `.\init.ps1 -InstallLocality Machine` from the root of the repo in a PowerShell window.

### Building

Build using `dotnet build src` or `msbuild /restore src`.

### Running tests

Run tests using `dotnet test src` or in Visual Studio with Test Explorer.
