@pushd "%~dp0\"
dotnet run -f netcoreapp3.1 -c release -- --runtimes net472 netcoreapp3.1 %*
@popd
