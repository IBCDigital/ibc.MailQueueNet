[CmdletBinding()]
param (
    [switch]$BuildDocFx,

    [switch]$InstallDependencies
)

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

function Get-DependencyFingerprint
{
    param (
        [string[]]$Paths
    )

    $hashes = foreach ($path in $Paths)
    {
        if (Test-Path $path)
        {
            '{0}:{1}' -f $path, (Get-FileHash -Path $path -Algorithm SHA256).Hash
        }
    }

    return ($hashes -join "`n")
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solutionPath = Join-Path $repoRoot 'MailQueueNet.sln'
$docfxConfigPath = Join-Path $repoRoot 'docfx\docfx.json'
$docsSitePath = Join-Path $repoRoot 'docs-site'
$packageJsonPath = Join-Path $docsSitePath 'package.json'
$packageLockPath = Join-Path $docsSitePath 'package-lock.json'
$nodeModulesPath = Join-Path $docsSitePath 'node_modules'
$docusaurusCommandPath = Join-Path $nodeModulesPath '.bin\docusaurus.cmd'
$installStampPath = Join-Path $nodeModulesPath '.mailqueuenet-docs-install.stamp'

Push-Location $repoRoot

try
{
    if ($BuildDocFx)
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
    else
    {
        Write-Host 'Skipping DocFX API reference build. Use -BuildDocFx to include it.'
    }

    Push-Location $docsSitePath

    try
    {
        $dependencyFingerprint = Get-DependencyFingerprint @($packageJsonPath, $packageLockPath)
        $installedFingerprint = if (Test-Path $installStampPath)
        {
            Get-Content -Path $installStampPath -Raw
        }
        else
        {
            $null
        }

        $requiresInstall = $InstallDependencies -or -not (Test-Path $docusaurusCommandPath)

        if ($requiresInstall -and (Test-Path $packageLockPath))
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
        elseif ($requiresInstall)
        {
            Write-Host 'Installing Docusaurus dependencies with npm install...'
            npm install --legacy-peer-deps
            Assert-LastExitCode 'npm install'
        }
        else
        {
            Write-Host 'Skipping Docusaurus dependency install. Use -InstallDependencies to run npm install or npm ci.'
        }

        if ($requiresInstall)
        {
            $dependencyFingerprint = Get-DependencyFingerprint @($packageJsonPath, $packageLockPath)
            Set-Content -Path $installStampPath -Value $dependencyFingerprint
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
