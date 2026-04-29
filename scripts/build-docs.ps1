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
$solutionPath = Join-Path $repoRoot 'MailQueueNet.sln'
$docfxConfigPath = Join-Path $repoRoot 'docfx\docfx.json'
$docsSitePath = Join-Path $repoRoot 'docs-site'
$packageLockPath = Join-Path $docsSitePath 'package-lock.json'

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

    Push-Location $docsSitePath

    try
    {
        if (Test-Path $packageLockPath)
        {
            Write-Host 'Installing Docusaurus dependencies with npm ci...'
            npm ci --legacy-peer-deps

            if ($LASTEXITCODE -ne 0)
            {
                Write-Warning 'npm ci failed. Retrying with npm install...'
                npm install --legacy-peer-deps
                Assert-LastExitCode 'npm install'
            }
        }
        else
        {
            Write-Host 'Installing Docusaurus dependencies with npm install...'
            npm install --legacy-peer-deps
            Assert-LastExitCode 'npm install'
        }

        Write-Host 'Building Docusaurus site...'
        npm run build
        Assert-LastExitCode 'npm run build'
    }
    finally
    {
        Pop-Location
    }
}
finally
{
    Pop-Location
}
