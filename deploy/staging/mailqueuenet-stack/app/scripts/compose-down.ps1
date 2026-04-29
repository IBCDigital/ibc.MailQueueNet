param(
    [string]$Server = "docker-dev-internal",
    [string]$User = "root",
    [string]$RemotePath = "/wwwroot/wwwdocs/mailqueuenet-stack",
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "script-config.ps1")

$dotEnvValues = Get-ScriptsDotEnvValues

if ($PSBoundParameters.ContainsKey("Server") -eq $false -and $dotEnvValues.ContainsKey("STAGING_SERVER"))
{
    $Server = $dotEnvValues["STAGING_SERVER"]
}

if ($PSBoundParameters.ContainsKey("User") -eq $false -and $dotEnvValues.ContainsKey("STAGING_USER"))
{
    $User = $dotEnvValues["STAGING_USER"]
}

if ($PSBoundParameters.ContainsKey("RemotePath") -eq $false -and $dotEnvValues.ContainsKey("STAGING_REMOTE_PATH"))
{
    $RemotePath = $dotEnvValues["STAGING_REMOTE_PATH"]
}

$sshTarget = "$User@$Server"
$command = "cd '$RemotePath' && docker compose down"

if ($DryRun)
{
    Write-Host "DRY RUN: ssh $sshTarget $command"
}
else
{
    ssh $sshTarget $command
}
