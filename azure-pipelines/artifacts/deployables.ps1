$RepoRoot = [System.IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$config = 'Debug'
if ($env:BuildConfiguration) { $config = $env:BuildConfiguration }
$NuGetPackages = "$RepoRoot\bin\Packages\$config\NuGet"

@{
    "$NuGetPackages" = (Get-ChildItem "$NuGetPackages\*.nupkg");
}
