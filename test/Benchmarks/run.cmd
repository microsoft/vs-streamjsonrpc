@pushd "%~dp0\"
dotnet run -f net8.0 -c release -- --runtimes net472 net8.0 %*
@popd
