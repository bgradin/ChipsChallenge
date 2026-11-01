#Requires -Version 5.1
<#
.SYNOPSIS
Restores dependencies and runs the solution tests with isolated temporary files.
.EXAMPLE
.\test.ps1
.EXAMPLE
# Run once from an elevated PowerShell to configure Defender, then exit.
.\test.ps1 -ConfigureDefenderExclusion
.EXAMPLE
.\test.ps1 -TestArguments @('--filter', 'FullyQualifiedName~GlobalMemory')
#>
[CmdletBinding()]
param(
    [switch] $ConfigureDefenderExclusion,
    [string[]] $TestArguments = @()
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'ChipShift64.sln'
$testTemp = Join-Path $PSScriptRoot 'bin/test-temp'
$onWindows = $env:OS -eq 'Windows_NT'

if ($ConfigureDefenderExclusion) {
    if (-not $onWindows) {
        throw 'Defender configuration requires Windows PowerShell running as Administrator.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run .\test.ps1 -ConfigureDefenderExclusion once in PowerShell as Administrator, then run .\test.ps1 in your regular shell.'
    }
    New-Item -ItemType Directory -Force -Path $testTemp | Out-Null
    $exclusions = (Get-MpPreference).ExclusionPath
    if ($exclusions -notcontains $testTemp) {
        Add-MpPreference -ExclusionPath $testTemp
    }
    if ((Get-MpPreference).ExclusionPath -notcontains $testTemp) {
        throw "Defender did not retain the exclusion for '$testTemp'. Check your device security policy."
    }
    Write-Host "Defender exclusion configured for $testTemp. Run .\test.ps1 in your regular shell."
    exit 0
}

Get-Command dotnet -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Force -Path $testTemp | Out-Null
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$result = 1
Push-Location $PSScriptRoot
try {
    $env:TEMP = $testTemp
    $env:TMP = $testTemp
    Write-Host "Test temporary files: $testTemp"

    # Re-evaluate dependencies and bypass the HTTP cache to recover missing packages.
    & dotnet restore $solution --force --no-cache
    $result = $LASTEXITCODE
    if ($result -eq 0) {
        & dotnet test $solution --no-restore -m:1 @TestArguments
        $result = $LASTEXITCODE
    }
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    Pop-Location
}
exit $result
