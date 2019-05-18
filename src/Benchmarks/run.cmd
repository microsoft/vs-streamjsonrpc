@pushd "%~dp0\"
dotnet run -f netcoreapp2.2 -c release -- --runtimes clr core  --invocationCount 992 %*
@popd
