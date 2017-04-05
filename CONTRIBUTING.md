# StreamJsonRpc

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com)
with any additional questions or comments.

## Pull requests

We are not yet accepting pull requests for this repository.

We hope to soon.

## Building

Visual Studio automatically downloads all the dependencies required and you can build using `msbuild` in `src/` or directly in Visual Studio. 

If there are any issues, save all untracked changes, close Visual Studio, and run the following commands from the VS 2017 Dev Console:
```
git clean -fdx :/
```
```
msbuild /t:restore
```


### Running tests

Most test runners will shadow copy assemblies, which the desktop CLR won't do for "public signed"
assemblies. To run tests, disable shadow copying in your test runner.

Alternatively you can disable public signing in favor of delay signing by setting
the `SignType` environment variable to `mock`.
This may cause the CLR to reject the assembly because it is delay signed, so you can
tell the CLR to skip delay sign verification of this assembly using this command
from an elevated Visual Studio Developer Command Prompt:

```
sn -Vr Microsoft.VisualStudio.Validation,b03f5f7f11d50a3a
```

Then restart your test runner process and rebuild the project
(with the SignType env var set as described above).
The tests should run.
