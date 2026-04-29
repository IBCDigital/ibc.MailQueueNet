param(
    [string]$TargetHost = "mailqueuenet.dev.ibc.com.au:443",
    [string]$ClientId = "devTest",
    [string]$ClientPass = "HtSYDs0GoKNvxBsZ4uSmaGEiRwzUvjGmj1v3zA6EWhM=",
    [string]$FromAddress = "staging-test@ibc.com.au",
    [string]$FromDisplayName = "Staging Test",
    [Parameter(Mandatory = $true)]
    [string]$ToAddress,
    [string]$ToDisplayName = "Test Recipient",
    [string]$Subject = "gRPC test via NPM",
    [string]$Body = "Queued through NPM gRPC endpoint"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$grpcurl = Get-Command -Name "grpcurl" -ErrorAction SilentlyContinue
if ($null -eq $grpcurl)
{
    throw "grpcurl was not found on PATH. Install grpcurl or run this script from an environment where grpcurl is available."
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$protoPath = Join-Path $repoRoot "MailQueueNet.Common\Protos\mail_queue.proto"
$importPath = Join-Path $repoRoot "MailQueueNet.Common\Protos"

if (!(Test-Path -Path $protoPath))
{
    throw "Proto file not found: $protoPath"
}

$payloadObject = @{
    from = @{
        address = $FromAddress
        displayName = $FromDisplayName
    }
    to = @(
        @{
            address = $ToAddress
            displayName = $ToDisplayName
        }
    )
    subject = $Subject
    body = $Body
    isBodyHtml = $false
    priority = "Normal"
    deliveryNotificationOptions = "None"
}

$payloadJson = $payloadObject | ConvertTo-Json -Depth 10 -Compress

$tempPayloadPath = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tempPayloadPath -Value $payloadJson -NoNewline -Encoding UTF8

try
{
    Write-Host "Sending gRPC QueueMail request to $TargetHost ..."
    Write-Host "To: $ToAddress"

    Get-Content -Path $tempPayloadPath -Raw | & $grpcurl.Source `
        -v `
        -import-path $importPath `
        -proto $protoPath `
        -H "x-client-id: $ClientId" `
        -H "x-client-pass: $ClientPass" `
        -d "@" `
        $TargetHost `
        MailQueue.MailGrpcService/QueueMail
}
finally
{
    Remove-Item -Path $tempPayloadPath -ErrorAction SilentlyContinue
}
