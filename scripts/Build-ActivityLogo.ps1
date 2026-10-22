param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Ffmpeg)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/ZapretTester.App/Assets/activity-logo.png'))
# Runtime ActivityLogo reads this 10x12 atlas: 120 RGBA frames, 192 px, 20 fps (six seconds).
& $Ffmpeg -hide_banner -loglevel error -i $Source -vf 'fps=20,scale=192:192:flags=lanczos,format=rgba,tile=10x12' -frames:v 1 -compression_level 9 $output -y
if ($LASTEXITCODE -ne 0) { throw 'Animation conversion failed.' }
Get-Item -LiteralPath $output | Select-Object Name, Length
