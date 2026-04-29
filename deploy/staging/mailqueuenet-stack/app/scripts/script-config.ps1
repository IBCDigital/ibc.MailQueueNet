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
