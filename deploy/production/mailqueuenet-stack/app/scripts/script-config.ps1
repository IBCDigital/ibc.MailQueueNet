function Get-DotEnvValues
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $values = @{}
    foreach ($line in Get-Content -Path $Path)
    {
        $trimmedLine = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmedLine))
        {
            continue
        }

        if ($trimmedLine.StartsWith("#"))
        {
            continue
        }

        $match = [regex]::Match($trimmedLine, "^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$")
        if (-not $match.Success)
        {
            continue
        }

        $key = $match.Groups[1].Value
        $value = $match.Groups[2].Value.Trim()

        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))
        {
            if ($value.Length -ge 2)
            {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        $values[$key] = $value
    }

    return $values
}

function Get-ScriptsDotEnvValues
{
    $dotEnvPath = Join-Path $PSScriptRoot ".env"
    if (Test-Path -Path $dotEnvPath)
    {
        return Get-DotEnvValues -Path $dotEnvPath
    }

    return @{}
}

function Assert-RequiredValue
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $false)]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value))
    {
        throw "Required setting '$Name' was not provided. Set it in scripts/.env or pass it as a parameter."
    }
}

function Quote-RemoteShellValue
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return "'" + $Value.Replace("'", "'`"'`"'") + "'"
}

function Get-PrivilegedCommand
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $false)]
        [string]$Mode = "sudo",

        [Parameter(Mandatory = $false)]
        [string]$Password
    )

    switch ($Mode.ToLowerInvariant())
    {
        "none" { return $Command }
        "sudo" { return "sudo sh -lc " + (Quote-RemoteShellValue -Value $Command) }
        "sudo-password" {
            Assert-RequiredValue -Name "PRODUCTION_PRIVILEGE_PASSWORD" -Value $Password
            return "printf '%s\n' " + (Quote-RemoteShellValue -Value $Password) + " | sudo -S -p '' sh -lc " + (Quote-RemoteShellValue -Value $Command)
        }
        "su" { return "su - root -c " + (Quote-RemoteShellValue -Value $Command) }
        default { throw "Unsupported privilege mode '$Mode'. Use 'sudo', 'sudo-password', 'su', or 'none'." }
    }
}

function Test-PrivilegeModeRequiresTty
{
    param(
        [Parameter(Mandatory = $false)]
        [string]$Mode = "sudo"
    )

    return $Mode.ToLowerInvariant() -eq "su"
}
