param([string]$OutputDirectory, [string]$PackageSource)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project 'artifacts\portable\ZapretTester-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$restore = @('-p:NuGetAudit=false')
if ($PackageSource) { $restore += "-p:RestoreSources=$PackageSource" }
foreach ($name in @('ZapretTester.Helper','ZapretTester.App')) {
    & dotnet publish (Join-Path $project "src\$name\$name.csproj") -c Release -r win-x64 --self-contained true -o $OutputDirectory @restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $name." }
}
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $project 'IMPLEMENTATION_STATUS.md') -Destination $OutputDirectory
$guideSource = Join-Path $project 'docs'
$guideDestination = Join-Path $OutputDirectory 'docs'
New-Item -ItemType Directory -Path $guideDestination -Force | Out-Null
foreach ($file in @('USER_GUIDE.ru.md', 'USER_GUIDE.ru.html', 'zapret-scoring-dashboard.png', 'COMPATIBILITY-1.10.3.md')) {
    Copy-Item -LiteralPath (Join-Path $guideSource $file) -Destination $guideDestination
}
Copy-Item -LiteralPath (Join-Path $guideSource 'USER_GUIDE.ru.html') -Destination (Join-Path $OutputDirectory 'ИНСТРУКЦИЯ.html')
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE-ZapretTester.txt')
Copy-Item -LiteralPath (Join-Path $project 'THIRD_PARTY_NOTICES.md') -Destination $OutputDirectory
$licenseDestination = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $project 'licenses') -File | Copy-Item -Destination $licenseDestination
Write-Output "Portable application: $OutputDirectory\ZapretTester.exe"


