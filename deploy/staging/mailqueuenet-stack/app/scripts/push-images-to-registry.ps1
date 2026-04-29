param(
    [string]$Registry = "docker-hub.internal.ibc.com.au",
    [string]$Tag = "latest",
    [string]$Username,
    [string]$Password,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "script-config.ps1")

$docker = Get-Command -Name "docker" -ErrorAction SilentlyContinue
if ($null -eq $docker)
{
    throw "Docker CLI was not found on PATH. Install Docker Desktop / Docker Engine and ensure 'docker' is available."
}

if ($PSBoundParameters.ContainsKey("Username") -xor $PSBoundParameters.ContainsKey("Password"))
{
    throw "Username and Password must be provided together."
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..\..\..\..")

$dotEnvValues = Get-ScriptsDotEnvValues

$effectiveRegistry = $Registry
$effectiveTag = $Tag
$effectiveUsername = $Username
$effectivePassword = $Password

if (($PSBoundParameters.ContainsKey("Registry") -eq $false) -and $dotEnvValues.ContainsKey("DOCKER_REGISTRY"))
{
    $effectiveRegistry = $dotEnvValues["DOCKER_REGISTRY"]
}

if (($PSBoundParameters.ContainsKey("Tag") -eq $false) -and $dotEnvValues.ContainsKey("DOCKER_TAG"))
{
    $effectiveTag = $dotEnvValues["DOCKER_TAG"]
}

if (($PSBoundParameters.ContainsKey("Username") -eq $false) -and $dotEnvValues.ContainsKey("DOCKER_USERNAME"))
{
    $effectiveUsername = $dotEnvValues["DOCKER_USERNAME"]
}

if (($PSBoundParameters.ContainsKey("Password") -eq $false) -and $dotEnvValues.ContainsKey("DOCKER_PASSWORD"))
{
    $effectivePassword = $dotEnvValues["DOCKER_PASSWORD"]
}

$images = @(
    @{ Name = "mailqueuenet-service"; Dockerfile = "MailQueueNet.Service/Dockerfile" },
    @{ Name = "mailforge"; Dockerfile = "MailForge/Dockerfile" },
    @{ Name = "mailfunk"; Dockerfile = "MailFunk/Dockerfile" }
)

if (-not [string]::IsNullOrWhiteSpace($effectiveUsername))
{
    if ($DryRun)
    {
        Write-Host "DRY RUN: docker login $effectiveRegistry --username $effectiveUsername --password-stdin"
    }
    else
    {
        $effectivePassword | docker login $effectiveRegistry --username $effectiveUsername --password-stdin
    }
}

foreach ($image in $images)
{
    $dockerfilePath = Join-Path $repoRoot $image.Dockerfile
    if (!(Test-Path -Path $dockerfilePath))
    {
        throw "Missing Dockerfile: $dockerfilePath"
    }

    $fullImageTag = "$effectiveRegistry/$($image.Name):$effectiveTag"

    if ($DryRun)
    {
        Write-Host "DRY RUN: docker build --file '$dockerfilePath' --tag '$fullImageTag' '$repoRoot'"
        Write-Host "DRY RUN: docker push '$fullImageTag'"
        continue
    }

    Write-Host "Building image '$($image.Name)' from '$($image.Dockerfile)'..."
    docker build --progress=plain --file $dockerfilePath --tag $fullImageTag $repoRoot
    if ($LASTEXITCODE -ne 0)
    {
        throw "Docker build failed for '$($image.Name)'. See output above for the first error. Dockerfile='$dockerfilePath' ImageTag='$fullImageTag'"
    }

    Write-Host "Pushing image '$fullImageTag'..."
    docker push $fullImageTag
    if ($LASTEXITCODE -ne 0)
    {
        throw "Docker push failed for '$($image.Name)'. ImageTag='$fullImageTag'"
    }
}
