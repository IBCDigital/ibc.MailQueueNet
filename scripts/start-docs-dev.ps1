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

function Test-PreviewPortInUse
{
    $listener = Get-NetTCPConnection -LocalPort 3000 -State Listen -ErrorAction SilentlyContinue
    return $null -ne $listener
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$docsSitePath = Join-Path $repoRoot 'docs-site'
$buildDocsScriptPath = Join-Path $PSScriptRoot 'build-docs.ps1'
$buildOutputPath = Join-Path $docsSitePath 'build'
$previewRootPath = Join-Path $docsSitePath '.previewroot'
$previewSitePath = Join-Path $previewRootPath 'MailQueueNet'

Push-Location $repoRoot

try
{
    if (Test-PreviewPortInUse)
    {
        throw 'Port 3000 is already in use. Stop the existing preview process before running start-docs-dev.ps1.'
    }

    Write-Host 'This build can take several minutes. The preview is not available until the serving message appears.'
    Write-Host 'Building Docusaurus and DocFX output...'
    powershell.exe -ExecutionPolicy Bypass -File $buildDocsScriptPath
    Assert-LastExitCode 'build-docs.ps1'

    if (-not (Test-Path $buildOutputPath))
    {
        throw "Build output not found: $buildOutputPath"
    }

    if (Test-Path $previewRootPath)
    {
        Remove-Item $previewRootPath -Recurse -Force
    }

    New-Item -Path $previewSitePath -ItemType Directory -Force | Out-Null

    Copy-Item -Path (Join-Path $buildOutputPath '*') -Destination $previewSitePath -Recurse -Force

    @"
<!DOCTYPE html>
<html>
  <head>
    <meta charset="utf-8" />
    <meta http-equiv="refresh" content="0; url=/MailQueueNet/" />
    <title>Redirecting...</title>
  </head>
  <body>
    <p>Redirecting to <a href="/MailQueueNet/">/MailQueueNet/</a>.</p>
  </body>
</html>
"@ | Set-Content -LiteralPath (Join-Path $previewRootPath 'index.html') -Encoding UTF8

    Write-Host 'Serving the built docs site on http://localhost:3000/MailQueueNet/ ...'
    Push-Location $docsSitePath

    try
    {
        npm run serve -- --dir .previewroot --host 0.0.0.0 --port 3000
        Assert-LastExitCode 'npm run serve'
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
