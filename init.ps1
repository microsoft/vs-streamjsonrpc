<#
.SYNOPSIS
    Prepares a machine to build and test this project.
.PARAMETER Localization
    Install the MicroBuild localization plugin for loc builds on desktop machines.
.PARAMETER Signing
    Install the MicroBuild signing plugin for test-signed builds on desktop machines.
#>
Param(
    [Parameter()]
    [switch]$Signing
)

Push-Location $PSScriptRoot
try {
    $toolsPath = "$PSScriptRoot\tools"

    & "$toolsPath\Restore-NuGetPackages.ps1" -Path "$PSScriptRoot\src"

    $MicroBuildPackageSource = 'https://devdiv.pkgs.visualstudio.com/DefaultCollection/_packaging/MicroBuildToolset/nuget/v3/index.json'
    if ($Signing) {
        Write-Host "Installing MicroBuild signing plugin"
        & "$toolsPath\Install-NuGetPackage.ps1" MicroBuild.Plugins.Signing -source $MicroBuildPackageSource
        $env:SignType = "Test"
    }

    Write-Host "Successfully restored all dependencies" -ForegroundColor Green
}
catch {
    Write-Error "Aborting script due to error"
    exit $lastexitcode
}
finally {
    Pop-Location
}
