param(
    [string]$Server,
    [string]$User = "ibcdigital",
    [string]$SshPassword,
    [string]$RemotePath = "/wwwroot/wwwdocs/mailqueuenet-stack",
    [string]$PrivilegeMode = "su",
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

if ($PSBoundParameters.ContainsKey("PrivilegeMode") -eq $false -and $dotEnvValues.ContainsKey("PRODUCTION_PRIVILEGE_MODE"))
{
    $PrivilegeMode = $dotEnvValues["PRODUCTION_PRIVILEGE_MODE"]
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
$remotePathQuoted = Quote-RemoteShellValue -Value $RemotePath
$remoteArchivePath = "/tmp/mailqueuenet-stack-download.tar"
$remoteArchivePathQuoted = Quote-RemoteShellValue -Value $remoteArchivePath

$tempRoot = $null
try
{
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mailqueuenet-stack-" + [guid]::NewGuid().ToString("N"))
    New-Item -Path $tempRoot -ItemType Directory | Out-Null

    $remoteCopyPlan = @(
        @{ RemoteItem = "docker-compose.yml"; LocalDestination = $tempRoot },
        @{ RemoteItem = "README.md"; LocalDestination = $tempRoot },
        @{ RemoteItem = "app/scripts"; LocalDestination = (Join-Path $tempRoot "app") }
    )

    if ($IncludeEnv)
    {
        $remoteCopyPlan += @{ RemoteItem = "app/mailqueuenet-service/.env"; LocalDestination = (Join-Path $tempRoot "app/mailqueuenet-service") }
        $remoteCopyPlan += @{ RemoteItem = "app/mailforge/.env"; LocalDestination = (Join-Path $tempRoot "app/mailforge") }
        $remoteCopyPlan += @{ RemoteItem = "app/mailfunk/.env"; LocalDestination = (Join-Path $tempRoot "app/mailfunk") }
    }

    $remoteItems = ($remoteCopyPlan | ForEach-Object { Quote-RemoteShellValue -Value $_.RemoteItem }) -join " "
    $tarCommand = Get-PrivilegedCommand -Command "cd $remotePathQuoted && tar -cf $remoteArchivePathQuoted $remoteItems && chown $User $remoteArchivePathQuoted" -Mode $PrivilegeMode
    $archivePath = Join-Path $tempRoot "production-stack.tar"

    if ($DryRun)
    {
        Write-Host "DRY RUN: ssh $sshTarget $tarCommand"
        Write-Host "DRY RUN: scp '${sshTarget}:$remoteArchivePath' '$archivePath'"
    }
    elseif ($null -ne $sshPassCommand)
    {
        $env:SSHPASS = $SshPassword
        & sshpass -e ssh $sshTarget $tarCommand
        & sshpass -e scp "${sshTarget}:$remoteArchivePath" $archivePath
    }
    elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
    {
        & $plinkCommand.Path -ssh -pw $SshPassword $sshTarget $tarCommand
        & $pscpCommand.Path -pw $SshPassword "${sshTarget}:$remoteArchivePath" $archivePath
    }
    else
    {
        ssh $sshTarget $tarCommand
        scp "${sshTarget}:$remoteArchivePath" $archivePath
    }

    if (-not $DryRun)
    {
        tar -xf $archivePath -C $tempRoot
    }

    if (-not $DryRun)
    {
        $downloadedScriptsDotEnvPath = Join-Path $tempRoot "app/scripts/.env"
        if (Test-Path -Path $downloadedScriptsDotEnvPath)
        {
            Remove-Item -Path $downloadedScriptsDotEnvPath -Force
        }

        foreach ($fileName in @("docker-compose.yml", "README.md"))
        {
            $sourcePath = Join-Path $tempRoot $fileName
            if (Test-Path -Path $sourcePath)
            {
                Copy-Item -Path $sourcePath -Destination (Join-Path $stackRoot $fileName) -Force
            }
        }

        $downloadedAppScriptsPath = Join-Path $tempRoot "app/scripts"
        if (Test-Path -Path $downloadedAppScriptsPath)
        {
            Copy-Item -Path $downloadedAppScriptsPath -Destination (Join-Path $stackRoot "app") -Recurse -Force
        }

        if ($IncludeEnv)
        {
            foreach ($envRelativePath in @("app/mailqueuenet-service/.env", "app/mailforge/.env", "app/mailfunk/.env"))
            {
                $sourceEnv = Join-Path $tempRoot $envRelativePath
                $destEnv = Join-Path $stackRoot $envRelativePath
                if (Test-Path -Path $sourceEnv)
                {
                    $destDir = Split-Path -Parent $destEnv
                    if (!(Test-Path -Path $destDir))
                    {
                        New-Item -Path $destDir -ItemType Directory -Force | Out-Null
                    }

                    Copy-Item -Path $sourceEnv -Destination $destEnv -Force
                }
            }
        }
    }
}
finally
{
    if ($null -ne $tempRoot)
    {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
