[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9._-]+$')]
    [string]$SitePath = 'MailQueueNet',

    [string]$SshHost = 'docker-internal.ibc.local',

    [string]$SshUser = 'docsdeploy',

    [int]$SshPort = 2233,

    [string]$SshKeyPath = (Join-Path $HOME '.ssh\docsdeploy'),

    [string]$RemoteRoot = '/wwwroot/wwwdocs/docs.internal.ibc.com.au/app/doc-sites',

    [string]$PublicBaseUrl = 'https://docs.internal.ibc.com.au',

    [int]$KeepReleases = 5,

    [switch]$SkipBuild
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

function Require-Command
{
    param (
        [string]$Name
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command '$Name' was not found in PATH."
    }
}

function Invoke-Native
{
    param (
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    Assert-LastExitCode $FilePath
}

function Invoke-RemoteBash
{
    param (
        [Parameter(Mandatory = $true)]
        [string]$Script
    )

    $normalisedScript = $Script -replace "`r`n", "`n" -replace "`r", "`n"
    $normalisedScript | & ssh @sshArguments $remote 'bash -se'
    Assert-LastExitCode 'ssh'
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$docsSitePath = Join-Path $repoRoot 'docs-site'
$buildDocsScriptPath = Join-Path $PSScriptRoot 'build-docs.ps1'
$buildDir = Join-Path $docsSitePath 'build'
$resolvedSshKeyPath = (Resolve-Path $SshKeyPath).Path
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
$incomingParent = "$RemoteRoot/.incoming/$SitePath"
$incomingStamp = "$incomingParent/$stamp"
$releaseParent = "$RemoteRoot/.releases/$SitePath"
$livePath = "$RemoteRoot/$SitePath"
$remote = "$SshUser@$SshHost"
$sshArguments = @('-i', $resolvedSshKeyPath, '-o', 'IdentitiesOnly=yes', '-p', $SshPort.ToString())
$scpArguments = @('-O', '-i', $resolvedSshKeyPath, '-o', 'IdentitiesOnly=yes', '-P', $SshPort.ToString())
$publicUrl = "$PublicBaseUrl/$SitePath/"
$releasePruneStart = [int]($KeepReleases + 1)

Require-Command ssh
Require-Command scp
Require-Command powershell.exe

if (-not (Test-Path $buildDocsScriptPath))
{
    throw "Build script not found: $buildDocsScriptPath"
}

Push-Location $repoRoot

try
{
    if (-not $SkipBuild)
    {
        Write-Host 'Building DocFX and Docusaurus output...' -ForegroundColor Cyan
        powershell.exe -ExecutionPolicy Bypass -File $buildDocsScriptPath
        Assert-LastExitCode 'build-docs.ps1'
    }

    if (-not (Test-Path $buildDir -PathType Container))
    {
        throw "Build output folder was not found: $buildDir"
    }

    Write-Host "Preparing remote folders on $SshHost..." -ForegroundColor Cyan
    $prepareScript = @"
set -eu
mkdir -p '$incomingParent' '$releaseParent'
rm -rf '$incomingStamp'
mkdir -p '$incomingStamp'
"@
    Invoke-RemoteBash $prepareScript

    Write-Host 'Uploading built site to the remote staging area...' -ForegroundColor Cyan
    Invoke-Native scp @scpArguments -r $buildDir "${remote}:$incomingStamp/"

    Write-Host 'Activating the new release and pruning old releases...' -ForegroundColor Cyan
    $finalizeScript = @'
set -eu
REMOTE_ROOT='__REMOTE_ROOT__'
SITE='__SITE__'
STAMP='__STAMP__'
INCOMING_PARENT="$REMOTE_ROOT/.incoming/$SITE/$STAMP"
INCOMING="$INCOMING_PARENT/build"
RELEASE_PARENT="$REMOTE_ROOT/.releases/$SITE"
RELEASE_DIR="$RELEASE_PARENT/$STAMP"
LIVE_PATH="$REMOTE_ROOT/$SITE"
TEMP_LINK="$REMOTE_ROOT/.$SITE.current"

if [ ! -f "$INCOMING/index.html" ] && [ -f "$INCOMING_PARENT/index.html" ]; then
  INCOMING="$INCOMING_PARENT"
fi

if [ ! -f "$INCOMING/index.html" ]; then
  echo "Incoming Docusaurus output not found under: $INCOMING_PARENT" >&2
  exit 1
fi

chmod -R a+rX "$INCOMING"
mv "$INCOMING" "$RELEASE_DIR"
rm -rf "$REMOTE_ROOT/.incoming/$SITE/$STAMP"

if [ -e "$LIVE_PATH" ] && [ ! -L "$LIVE_PATH" ]; then
  mv "$LIVE_PATH" "$REMOTE_ROOT/.legacy-$SITE-$STAMP"
fi

ln -sfn ".releases/$SITE/$STAMP" "$TEMP_LINK"
mv -Tf "$TEMP_LINK" "$LIVE_PATH"

ls -1dt "$RELEASE_PARENT"/* 2>/dev/null | tail -n +__PRUNE_START__ | xargs -r rm -rf --
'@
    $finalizeScript = $finalizeScript.Replace('__REMOTE_ROOT__', $RemoteRoot)
    $finalizeScript = $finalizeScript.Replace('__SITE__', $SitePath)
    $finalizeScript = $finalizeScript.Replace('__STAMP__', $stamp)
    $finalizeScript = $finalizeScript.Replace('__PRUNE_START__', $releasePruneStart.ToString())
    Invoke-RemoteBash $finalizeScript

    Write-Host "Published successfully: $publicUrl" -ForegroundColor Green
    Write-Host "Live path on server: $livePath" -ForegroundColor Green
}
finally
{
    Pop-Location
}
