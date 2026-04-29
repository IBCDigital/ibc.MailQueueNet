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

    foreach ($item in $remoteCopyPlan)
    {
        $remoteSource = "${sshTarget}:$RemotePath/$($item.RemoteItem)"
        $localDestination = $item.LocalDestination

        if (!(Test-Path -Path $localDestination))
        {
            New-Item -Path $localDestination -ItemType Directory -Force | Out-Null
        }

        if ($DryRun)
        {
            if ($null -ne $sshPassCommand)
            {
                Write-Host "DRY RUN: sshpass -e scp -r '$remoteSource' '$localDestination'"
            }
            elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
            {
                Write-Host "DRY RUN: pscp -pw ******** -r '$remoteSource' '$localDestination'"
            }
            else
            {
                Write-Host "DRY RUN: scp -r '$remoteSource' '$localDestination'"
            }

            continue
        }

        if ($null -ne $sshPassCommand)
        {
            $env:SSHPASS = $SshPassword
            try
            {
                & sshpass -e scp -r $remoteSource $localDestination
            }
            finally
            {
                Remove-Item -Path Env:\SSHPASS -ErrorAction SilentlyContinue
            }
        }
        elseif (($null -ne $plinkCommand) -and ($null -ne $pscpCommand))
        {
            & $pscpCommand.Path -pw $SshPassword -r $remoteSource $localDestination
        }
        else
        {
            scp -r $remoteSource $localDestination
        }
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
