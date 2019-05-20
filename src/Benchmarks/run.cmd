@pushd "%~dp0\"
dotnet run -f netcoreapp2.2 -c release -- --runtimes net472 netcoreapp2.2 %*
@popd
