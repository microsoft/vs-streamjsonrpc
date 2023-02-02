@pushd "%~dp0\"
dotnet run -f net6.0 -c release -- --runtimes net472 net6.0 %*
@popd
