$filePaths = Get-ChildItem -Path "$PSScriptRoot/../../bin/StreamJsonRpc/*/*/StreamJsonRpc.dll" -ErrorAction Ignore
if ($filePaths) {
    [string]::join(';', $filePaths)
} else {
    ''
}
