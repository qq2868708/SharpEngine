$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Project = Join-Path $Root 'Examples\NetworkDedicatedServer\NetworkDedicatedServer.sharpproject'
$ServerExe = Join-Path $Root 'Binaries\Win64\Debug\Server\DedicatedServer.exe'
$OutputDirectory = Join-Path $Root 'Saved\Validation\NetworkDedicatedServer'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$ServerOutput = Join-Path $OutputDirectory 'server.out'
$ServerError = Join-Path $OutputDirectory 'server.err'
$FirstOutput = Join-Path $OutputDirectory 'client-a.out'
$FirstError = Join-Path $OutputDirectory 'client-a.err'
$SecondOutput = Join-Path $OutputDirectory 'client-b.out'
$SecondError = Join-Path $OutputDirectory 'client-b.err'
Remove-Item -LiteralPath @($ServerOutput, $ServerError, $FirstOutput, $FirstError,
    $SecondOutput, $SecondError) -Force -ErrorAction SilentlyContinue

$Server = $null
$First = $null
$Second = $null
try
{
    $Server = Start-Process -FilePath $ServerExe -ArgumentList @(
        '--project', $Project, '--network-port', '28760', '--duration-milliseconds', '8000') `
        -WorkingDirectory $Root -RedirectStandardOutput $ServerOutput `
        -RedirectStandardError $ServerError -WindowStyle Hidden -PassThru
    $Deadline = [DateTime]::UtcNow.AddSeconds(10)
    $ServerText = ''
    do
    {
        Start-Sleep -Milliseconds 100
        if (Test-Path -LiteralPath $ServerOutput -PathType Leaf)
        {
            $ReadText = Get-Content -Raw -LiteralPath $ServerOutput
            $ServerText = if ($null -eq $ReadText) { '' } else { $ReadText }
        }
    }
    while (!$ServerText.Contains('"status":"listening"') -and
        [DateTime]::UtcNow -lt $Deadline -and !$Server.HasExited)
    if (!$ServerText.Contains('"status":"listening"'))
    {
        $ServerErrorText = Get-Content -Raw -LiteralPath $ServerError -ErrorAction SilentlyContinue
        throw 'Dedicated Server did not report listening. ' + $ServerText + ' ' + $ServerErrorText
    }

    $Sample = Join-Path $Root 'Tools\NetworkSample\bin\Debug\net8.0\SharpEngine.NetworkSample.dll'
    $First = Start-Process -FilePath 'dotnet' -ArgumentList @(
        $Sample, 'client', '28760', 'player-a', '7') -WorkingDirectory $Root `
        -RedirectStandardOutput $FirstOutput -RedirectStandardError $FirstError -WindowStyle Hidden -PassThru
    $Second = Start-Process -FilePath 'dotnet' -ArgumentList @(
        $Sample, 'client', '28760', 'player-b', '11') -WorkingDirectory $Root `
        -RedirectStandardOutput $SecondOutput -RedirectStandardError $SecondError -WindowStyle Hidden -PassThru
    if (!$First.WaitForExit(12000) -or !$Second.WaitForExit(12000))
    {
        throw 'Network clients did not finish within 12 seconds'
    }

    $FirstText = Get-Content -Raw -LiteralPath $FirstOutput
    $SecondText = Get-Content -Raw -LiteralPath $SecondOutput
    if (!$FirstText.Contains('"status":"pass"') -or
        !$SecondText.Contains('"status":"pass"'))
    {
        throw 'Network clients did not report pass. A=' + $FirstText + ' B=' + $SecondText
    }
    if (!$Server.WaitForExit(12000))
    {
        throw 'Dedicated Server did not stop within its requested duration'
    }
    $ServerText = Get-Content -Raw -LiteralPath $ServerOutput

    [pscustomobject]@{
        Server = $ServerText.Trim()
        ClientA = $FirstText.Trim()
        ClientB = $SecondText.Trim()
    } | ConvertTo-Json -Compress
}
finally
{
    if ($null -ne $First -and !$First.HasExited)
    {
        $First.Kill()
    }
    if ($null -ne $Second -and !$Second.HasExited)
    {
        $Second.Kill()
    }
    if ($null -ne $Server -and !$Server.HasExited)
    {
        $Server.Kill()
    }
}
