$nbgv = & "$PSScriptRoot\..\Get-nbgv.ps1"
[string]::join(',',(@{
    ('StreamJsonRpcVersion') = & { (& $nbgv get-version --project "$PSScriptRoot\..\..\src\StreamJsonRpc" --format json | ConvertFrom-Json).AssemblyVersion };
}.GetEnumerator() |% { "$($_.key)=$($_.value)" }))
