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
$buildApiDocsScriptPath = Join-Path $PSScriptRoot 'build-api-docs.ps1'
$docfxOutputPath = Join-Path $repoRoot 'docs-site\static\api'
$previewRootPath = Join-Path $repoRoot 'docs-site\.previewroot\MailQueueNet'
$previewApiPath = Join-Path $previewRootPath 'api'

Push-Location $repoRoot

try
{
    if (-not (Test-Path $previewRootPath))
    {
        throw 'The staged docs preview was not found. Run .\scripts\start-docs-dev.ps1 once before refreshing the DocFX preview.'
    }

    Write-Host 'Rebuilding DocFX output...'
    powershell.exe -ExecutionPolicy Bypass -File $buildApiDocsScriptPath
    Assert-LastExitCode 'build-api-docs.ps1'

    if (-not (Test-Path $docfxOutputPath))
    {
        throw "DocFX output not found: $docfxOutputPath"
    }

    if (Test-Path $previewApiPath)
    {
        Remove-Item $previewApiPath -Recurse -Force
    }

    New-Item -Path $previewApiPath -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $docfxOutputPath '*') -Destination $previewApiPath -Recurse -Force

    Write-Host 'Refreshed the staged DocFX preview.'
    Write-Host 'Open http://localhost:3000/MailQueueNet/api/index.html to review the updated API reference.'
}
finally
{
    Pop-Location
}
