$ErrorActionPreference = 'Stop'

function Assert-LastExitCode
{
    param (
        [string]$StepName
    )

    if ($LASTEXITCODE -ne 0)
    {
        throw "$StepName failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solutionPath = Join-Path $repoRoot 'IBC.AddressLookup.sln'
$docfxConfigPath = Join-Path $repoRoot 'docfx\docfx.json'

Push-Location $repoRoot

try
{
    Write-Host 'Restoring solution packages...'
    dotnet restore $solutionPath
    Assert-LastExitCode 'dotnet restore'

    Write-Host 'Restoring local tools...'
    dotnet tool restore
    Assert-LastExitCode 'dotnet tool restore'

    Write-Host 'Building API reference with DocFX...'
    dotnet tool run docfx $docfxConfigPath
    Assert-LastExitCode 'docfx'
}
finally
{
    Pop-Location
}
