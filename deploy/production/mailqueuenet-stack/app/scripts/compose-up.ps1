param(
    [string]$Server,
    [string]$User = "ibcdigital",
    [string]$RemotePath = "/wwwroot/wwwdocs/mailqueuenet-stack",
    [string]$PrivilegeMode = "su",
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "script-config.ps1")

$dotEnvValues = Get-ScriptsDotEnvValues

if ($PSBoundParameters.ContainsKey("Server") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_SERVER"))
{
    $Server = $dotEnvValues["PRODUCTION_SERVER"]
}

if ($PSBoundParameters.ContainsKey("User") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_USER"))
{
    $User = $dotEnvValues["PRODUCTION_USER"]
}

if ($PSBoundParameters.ContainsKey("RemotePath") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_REMOTE_PATH"))
{
    $RemotePath = $dotEnvValues["PRODUCTION_REMOTE_PATH"]
}

if ($PSBoundParameters.ContainsKey("PrivilegeMode") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_PRIVILEGE_MODE"))
{
    $PrivilegeMode = $dotEnvValues["PRODUCTION_PRIVILEGE_MODE"]
}

Assert-RequiredValue -Name "PRODUCTION_SERVER" -Value $Server

$sshTarget = "$User@$Server"
$remotePathQuoted = Quote-RemoteShellValue -Value $RemotePath
$command = Get-PrivilegedCommand -Command "cd $remotePathQuoted && docker compose up -d --remove-orphans" -Mode $PrivilegeMode
$requiresTty = Test-PrivilegeModeRequiresTty -Mode $PrivilegeMode

if ($DryRun)
{
    Write-Host "DRY RUN: ssh $sshTarget $command"
}
else
{
    if ($requiresTty)
    {
        ssh -t $sshTarget $command
    }
    else
    {
        ssh $sshTarget $command
    }
}
