param([string]$OutputDirectory, [string]$PackageSource)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project 'artifacts\portable\ZapretTester-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'ZapretTester.Helper.exe')) {
    throw 'This folder contains an older flat package. Choose a new OutputDirectory for the compact package.'
}
$appDirectory = Join-Path $OutputDirectory 'app'
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
$restore = @('-p:NuGetAudit=false')
if ($PackageSource) { $restore += "-p:RestoreSources=$PackageSource" }
foreach ($name in @('ZapretTester.Helper','ZapretTester.App')) {
    $layout = @()
    if ($name -eq 'ZapretTester.App') { $layout += "-p:PortableRoot=$OutputDirectory" }
    & dotnet publish (Join-Path $project "src\$name\$name.csproj") -c Release -r win-x64 --self-contained true -o $appDirectory @restore @layout -p:DebugType=None -p:DebugSymbols=false --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $name." }
}
# Only the root entry point is offered to users; the helper remains internal.
Remove-Item -LiteralPath (Join-Path $appDirectory 'ZapretTester.exe')
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination $appDirectory
Copy-Item -LiteralPath (Join-Path $project 'IMPLEMENTATION_STATUS.md') -Destination $appDirectory
$guideSource = Join-Path $project 'docs'
$guideDestination = Join-Path $appDirectory 'docs'
New-Item -ItemType Directory -Path $guideDestination -Force | Out-Null
foreach ($file in @('USER_GUIDE.ru.md', 'USER_GUIDE.ru.html', 'zapret-scoring-dashboard.png', 'COMPATIBILITY-1.10.3.md', 'COMPATIBILITY-1.10.4.md', 'PERFORMANCE-1.1.0.md')) {
    Copy-Item -LiteralPath (Join-Path $guideSource $file) -Destination $guideDestination
}
Copy-Item -LiteralPath (Join-Path $guideSource 'USER_GUIDE.ru.html') -Destination (Join-Path $OutputDirectory 'ИНСТРУКЦИЯ.html')
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $appDirectory 'LICENSE-ZapretTester.txt')
Copy-Item -LiteralPath (Join-Path $project 'THIRD_PARTY_NOTICES.md') -Destination $appDirectory
$licenseDestination = Join-Path $appDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $project 'licenses') -File | Copy-Item -Destination $licenseDestination
Write-Output "Portable application: $OutputDirectory\ZapretTester.exe"


