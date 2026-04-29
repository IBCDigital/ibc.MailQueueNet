param(
    [string]$Server = "docker-dev-internal",
    [string]$User = "root",
    [string]$SshPassword,
    [string]$RemotePath = "/wwwroot/wwwdocs/mailqueuenet-stack",
    [switch]$IncludeEnv = $true,
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

if ($PSBoundParameters.ContainsKey("SshPassword") -eq $false -and $dotEnvValues.ContainsKey("STAGING_SSH_PASSWORD"))
{
    $SshPassword = $dotEnvValues["STAGING_SSH_PASSWORD"]
}

$useSshPassword = -not [string]::IsNullOrWhiteSpace($SshPassword)

$sshPassCommand = $null
$plinkCommand = $null
$pscpCommand = $null

if ($useSshPassword)
{
    $sshPassCommand = Get-Command -Name "sshpass" -ErrorAction SilentlyContinue
    if ($null -eq $sshPassCommand)
    {
        $plinkCommand = Get-Command -Name "plink" -ErrorAction SilentlyContinue
        $pscpCommand = Get-Command -Name "pscp" -ErrorAction SilentlyContinue
    }

    if (-not $DryRun -and ($null -eq $sshPassCommand) -and (($null -eq $plinkCommand) -or ($null -eq $pscpCommand)))
    {
        throw "SshPassword was provided but no supported helper was found. Install 'sshpass' (recommended on Linux/macOS), or install PuTTY tools ('plink' and 'pscp'), or use SSH key-based authentication (recommended)."
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$stackRoot = Resolve-Path (Join-Path $scriptDir "..\..")

$sshTarget = "$User@$Server"

$tempRoot = $null
$tempAppPath = $null
try
{
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mailqueuenet-stack-" + [guid]::NewGuid().ToString("N"))
    New-Item -Path $tempRoot -ItemType Directory | Out-Null

    $localAppPath = Join-Path $stackRoot "app"
    if (!(Test-Path -Path $localAppPath))
    {
        throw "Missing local path: $localAppPath"
    }

    $tempAppPath = Join-Path $tempRoot "app"
    Copy-Item -Path $localAppPath -Destination $tempAppPath -Recurse -Force

    $scriptsDotEnvPath = Join-Path $tempAppPath "scripts\.env"
    if (Test-Path -Path $scriptsDotEnvPath)
    {
        Remove-Item -Path $scriptsDotEnvPath -Force
    }

    if (-not $IncludeEnv)
    {
        Get-ChildItem -Path $tempAppPath -Filter ".env" -File -Recurse | Remove-Item -Force
    }

    $itemsToCopy = @(
        (Join-Path $stackRoot "docker-compose.yml"),
        (Join-Path $stackRoot "README.md"),
        $tempAppPath
    )

$mkdirCommand = "mkdir -p '$RemotePath/app'"
    if (($null -ne $sshPassCommand) -and -not $DryRun)
    {
        $env:SSHPASS = $SshPassword
    }

    if ($DryRun)
    {
        if ($null -ne $sshPassCommand)
        {
            Write-Host "DRY RUN: sshpass -e ssh $sshTarget $mkdirCommand"
        }
        elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
        {
            Write-Host "DRY RUN: plink -ssh -pw ******** $sshTarget $mkdirCommand"
        }
        else
        {
            Write-Host "DRY RUN: ssh $sshTarget $mkdirCommand"
        }
    }
    else
    {
        if ($null -ne $sshPassCommand)
        {
            & sshpass -e ssh $sshTarget $mkdirCommand
        }
        elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
        {
            & $plinkCommand.Path -ssh -pw $SshPassword $sshTarget $mkdirCommand
        }
        else
        {
            ssh $sshTarget $mkdirCommand
        }
    }

    foreach ($item in $itemsToCopy)
    {
        if (!(Test-Path -Path $item))
        {
            throw "Missing local path: $item"
        }

        if ($DryRun)
        {
            if ($null -ne $sshPassCommand)
            {
                Write-Host "DRY RUN: sshpass -e scp -r '$item' '${sshTarget}:$RemotePath/'"
            }
            elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
            {
                Write-Host "DRY RUN: pscp -pw ******** -r '$item' '${sshTarget}:$RemotePath/'"
            }
            else
            {
                Write-Host "DRY RUN: scp -r '$item' '${sshTarget}:$RemotePath/'"
            }

            continue
        }

        if ($null -ne $sshPassCommand)
        {
            & sshpass -e scp -r $item "${sshTarget}:$RemotePath/"
        }
        elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
        {
            & $pscpCommand.Path -pw $SshPassword -r $item "${sshTarget}:$RemotePath/"
        }
        else
        {
            scp -r $item "${sshTarget}:$RemotePath/"
        }
    }
}
finally
{
    if ($null -ne $tempRoot)
    {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $sshPassCommand)
    {
        Remove-Item -Path Env:\SSHPASS -ErrorAction SilentlyContinue
    }
}
