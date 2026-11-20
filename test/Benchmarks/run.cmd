@pushd "%~dp0\"
dotnet run -f net10.0 -c release -- --runtimes net472 net10.0 %*
@popd
