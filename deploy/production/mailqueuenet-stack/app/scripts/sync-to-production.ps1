param(
    [string]$Server,
    [string]$User = "ibcdigital",
    [string]$SshPassword,
    [string]$RemotePath = "/wwwroot/wwwdocs/mailqueuenet-stack",
    [string]$UploadPath = "/tmp/mailqueuenet-stack-upload",
    [string]$PrivilegeMode = "su",
    [string]$PrivilegePassword,
    [switch]$IncludeEnv = $true,
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

if ($PSBoundParameters.ContainsKey("UploadPath") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_UPLOAD_PATH"))
{
    $UploadPath = $dotEnvValues["PRODUCTION_UPLOAD_PATH"]
}

if ($PSBoundParameters.ContainsKey("PrivilegeMode") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_PRIVILEGE_MODE"))
{
    $PrivilegeMode = $dotEnvValues["PRODUCTION_PRIVILEGE_MODE"]
}

if ($PSBoundParameters.ContainsKey("PrivilegePassword") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_PRIVILEGE_PASSWORD"))
{
    $PrivilegePassword = $dotEnvValues["PRODUCTION_PRIVILEGE_PASSWORD"]
}

if ($PSBoundParameters.ContainsKey("SshPassword") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_SSH_PASSWORD"))
{
    $SshPassword = $dotEnvValues["PRODUCTION_SSH_PASSWORD"]
}

Assert-RequiredValue -Name "PRODUCTION_SERVER" -Value $Server

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
$uploadPathQuoted = Quote-RemoteShellValue -Value $UploadPath
$remotePathQuoted = Quote-RemoteShellValue -Value $RemotePath
$remoteArchivePath = ($UploadPath.TrimEnd("/") + "/mailqueuenet-stack.tar")
$remoteArchivePathQuoted = Quote-RemoteShellValue -Value $remoteArchivePath

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

    Copy-Item -Path (Join-Path $stackRoot "docker-compose.yml") -Destination (Join-Path $tempRoot "docker-compose.yml") -Force
    Copy-Item -Path (Join-Path $stackRoot "README.md") -Destination (Join-Path $tempRoot "README.md") -Force

    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("mailqueuenet-stack-" + [guid]::NewGuid().ToString("N") + ".tar")
    tar -cf $archivePath -C $tempRoot docker-compose.yml README.md app

    $prepareUploadCommand = "rm -rf $uploadPathQuoted && mkdir -p $uploadPathQuoted"
    if (($null -ne $sshPassCommand) -and -not $DryRun)
    {
        $env:SSHPASS = $SshPassword
    }

    if ($DryRun)
    {
        if ($null -ne $sshPassCommand)
        {
            Write-Host "DRY RUN: sshpass -e ssh $sshTarget $prepareUploadCommand"
        }
        elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
        {
            Write-Host "DRY RUN: plink -ssh -pw ******** $sshTarget $prepareUploadCommand"
        }
        else
        {
            Write-Host "DRY RUN: ssh $sshTarget $prepareUploadCommand"
        }
    }
    else
    {
        if ($null -ne $sshPassCommand)
        {
            & sshpass -e ssh $sshTarget $prepareUploadCommand
        }
        elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
        {
            & $plinkCommand.Path -ssh -pw $SshPassword $sshTarget $prepareUploadCommand
        }
        else
        {
            ssh $sshTarget $prepareUploadCommand
        }
    }

    if (!(Test-Path -Path $archivePath))
    {
        throw "Failed to create local archive: $archivePath"
    }

    if ($DryRun)
    {
        if ($null -ne $sshPassCommand)
        {
            Write-Host "DRY RUN: sshpass -e scp '$archivePath' '${sshTarget}:$remoteArchivePath'"
        }
        elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
        {
            Write-Host "DRY RUN: pscp -pw ******** '$archivePath' '${sshTarget}:$remoteArchivePath'"
        }
        else
        {
            Write-Host "DRY RUN: scp '$archivePath' '${sshTarget}:$remoteArchivePath'"
        }
    }
    elseif ($null -ne $sshPassCommand)
    {
        & sshpass -e scp $archivePath "${sshTarget}:$remoteArchivePath"
    }
    elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
    {
        & $pscpCommand.Path -pw $SshPassword $archivePath "${sshTarget}:$remoteArchivePath"
    }
    else
    {
        scp $archivePath "${sshTarget}:$remoteArchivePath"
    }

    $installCommand = "mkdir -p $remotePathQuoted && tar -xf $remoteArchivePathQuoted -C $remotePathQuoted && rm -rf $uploadPathQuoted"
    $privilegedInstallCommand = Get-PrivilegedCommand -Command $installCommand -Mode $PrivilegeMode -Password $PrivilegePassword
    $verifyCommand = "test -f $remotePathQuoted/docker-compose.yml && test -d $remotePathQuoted/app && ls -la $remotePathQuoted"
    $privilegedVerifyCommand = Get-PrivilegedCommand -Command $verifyCommand -Mode $PrivilegeMode -Password $PrivilegePassword
    $requiresTty = Test-PrivilegeModeRequiresTty -Mode $PrivilegeMode

    if ($DryRun)
    {
        Write-Host "DRY RUN: ssh $sshTarget $privilegedInstallCommand"
    }
    elseif ($null -ne $sshPassCommand)
    {
        if ($requiresTty)
        {
            & sshpass -e ssh -t $sshTarget $privilegedInstallCommand
        }
        else
        {
            & sshpass -e ssh $sshTarget $privilegedInstallCommand
        }
    }
    elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
    {
        if ($requiresTty)
        {
            & $plinkCommand.Path -ssh -t -pw $SshPassword $sshTarget $privilegedInstallCommand
        }
        else
        {
            & $plinkCommand.Path -ssh -pw $SshPassword $sshTarget $privilegedInstallCommand
        }
    }
    else
    {
        if ($requiresTty)
        {
            ssh -t $sshTarget $privilegedInstallCommand
        }
        else
        {
            ssh $sshTarget $privilegedInstallCommand
        }
    }

    if ($LASTEXITCODE -ne 0)
    {
        throw "Remote privileged install failed. Check the root/sudo password and whether '$User' is allowed to use '$PrivilegeMode'."
    }

    Write-Host "Verifying production stack files at $RemotePath..."
    if ($DryRun)
    {
        Write-Host "DRY RUN: ssh $sshTarget $privilegedVerifyCommand"
    }
    elseif ($null -ne $sshPassCommand)
    {
        if ($requiresTty)
        {
            & sshpass -e ssh -t $sshTarget $privilegedVerifyCommand
        }
        else
        {
            & sshpass -e ssh $sshTarget $privilegedVerifyCommand
        }
    }
    elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
    {
        if ($requiresTty)
        {
            & $plinkCommand.Path -ssh -t -pw $SshPassword $sshTarget $privilegedVerifyCommand
        }
        else
        {
            & $plinkCommand.Path -ssh -pw $SshPassword $sshTarget $privilegedVerifyCommand
        }
    }
    else
    {
        if ($requiresTty)
        {
            ssh -t $sshTarget $privilegedVerifyCommand
        }
        else
        {
            ssh $sshTarget $privilegedVerifyCommand
        }
    }

    if ($LASTEXITCODE -ne 0)
    {
        throw "Production stack verification failed. The upload completed, but expected files were not found at '$RemotePath'."
    }
}
finally
{
    if ($null -ne $tempRoot)
    {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (($null -ne $archivePath) -and (Test-Path -Path $archivePath))
    {
        Remove-Item -Path $archivePath -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $sshPassCommand)
    {
        Remove-Item -Path Env:\SSHPASS -ErrorAction SilentlyContinue
    }
}
