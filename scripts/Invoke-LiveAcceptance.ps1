param(
    [Parameter(Mandatory)][string]$FlowsealFolder,
    [Parameter(Mandatory)][string]$OutputFile,
    [switch]$AllowServiceRestart,
    [string]$HelperPath
)
$ErrorActionPreference = 'Stop'
if (-not $AllowServiceRestart) { throw 'Pass -AllowServiceRestart to explicitly permit the managed transition and restoration.' }
$project = Split-Path -Parent $PSScriptRoot
$helper = if ($HelperPath) { $HelperPath } else { Join-Path $project 'src\ZapretTester.Helper\bin\Debug\net10.0-windows\ZapretTester.Helper.exe' }
function Source-Manifest { (Get-ChildItem -LiteralPath $FlowsealFolder -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n" }
$sourceBefore = Source-Manifest
$pipeName = 'ZapretTester.acceptance.' + [Guid]::NewGuid().ToString('N')
$nonce = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$result = [ordered]@{ StartedAt=[DateTimeOffset]::Now; Passed=$false; ProfileCount=0; Completed=0; PeakParallel=0; Restored=$false; SourceUnchanged=$false; NoTemporaryProcesses=$false; Error=$null }
$process = Start-Process -FilePath $helper -ArgumentList @('"'+$FlowsealFolder+'"',$pipeName,$nonce,$sid) -Verb RunAs -WindowStyle Hidden -PassThru
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
$writer = $null
$reader = $null
function Send-Command([string]$command, [string]$profileId='') {
    $script:testerRequestId=[Guid]::NewGuid().ToString('N')
    $writer.WriteLine((@{Command=$command;Nonce=$nonce;ProfileId=$(if ($profileId) {$profileId} else {$null});RequestId=$script:testerRequestId} | ConvertTo-Json -Compress))
    $writer.Flush()
}
function Wait-State([scriptblock]$predicate, [int]$seconds=30, [bool]$reply=$true) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        $taskLine = $reader.ReadLineAsync()
        if (-not $taskLine.Wait([TimeSpan]::FromSeconds([Math]::Max(1,$seconds-$watch.Elapsed.TotalSeconds)))) { throw 'IPC response timeout.' }
        if ($null -eq $taskLine.Result) { throw 'Helper disconnected.' }
        $message = $taskLine.Result | ConvertFrom-Json
        if ($message.Kind -eq 'Error') { throw $message.Error }
        if ($message.State) {
            $active = @($message.State.Profiles | Where-Object RunState -eq 2).Count
            $result.PeakParallel = [Math]::Max($result.PeakParallel,$active)
            if ((-not $reply -or ($message.Kind -eq 'Reply' -and $message.RequestId -eq $script:testerRequestId)) -and (& $predicate $message.State)) { return $message.State }
        }
    }
    throw 'Expected state did not arrive.'
}
try {
    $pipe.Connect(30000)
    $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
    $reader = [IO.StreamReader]::new($pipe,[Text.UTF8Encoding]::new($false),$false,4096,$true)
    Send-Command 'State'
    $state = Wait-State { param($s) $s.Profiles.Count -gt 0 }
    $result.ProfileCount = $state.Profiles.Count
    if ($state.Profiles.Count -ne 22) { throw 'Expected the 22 profiles from the validated fixture bundle.' }
    $selected = $state.Profiles | Where-Object Name -eq 'ALT9' | Select-Object -First 1
    Send-Command 'StartManaged' $selected.Id
    $state = Wait-State { param($s) $s.Mode -eq 'Managed' }
    Send-Command 'Test'
    $state = Wait-State { param($s) $true }
    if ($state.IsTesting) { $state = Wait-State { param($s) -not $s.IsTesting } 300 $false }
    $result.Completed = @($state.Profiles | Where-Object { $_.RunState -eq 0 -and $_.LastResult -and $_.LastResult.IsReliable -and $_.LastResult.Endpoints.Count -eq 4 -and [DateTimeOffset]$_.LastResult.TestedAt -ge $result.StartedAt }).Count
    $result.Profiles = @($state.Profiles | Select-Object Name,RunState,LastResult)
    if ($result.Completed -ne 22) { throw ('Not all profiles produced reliable endpoint measurements. ' + $state.Error) }
    if ($result.PeakParallel -gt 4) { throw 'Concurrency limit violated.' }
    $result.Passed = $true
}
catch { $result.Error = $_.Exception.ToString() }
finally {
    if ($pipe.IsConnected -and $writer) {
        try {
            Send-Command 'Cancel'
            $null = Wait-State { param($s) -not $s.IsTesting }
            Send-Command 'RestoreExternal'
            $null = Wait-State { param($s) $s.Mode -eq 'Compatibility' }
            # RestoreExternal can emit an intermediate state. Query again after it completes.
            Send-Command 'State'
            $null = Wait-State { param($s) $s.Mode -eq 'Compatibility' }
            Send-Command 'Shutdown'
            $result.Restored = (Get-Service zapret).Status -eq 'Running'
        } catch { $result.Passed=$false; $result.Error += "`nCleanup: " + $_.Exception.Message }
    }
    if ($writer) { $writer.Dispose() }
    if ($reader) { $reader.Dispose() }
    $pipe.Dispose()
    $null = $process.WaitForExit(10000)
    $result.SourceUnchanged = $sourceBefore -ceq (Source-Manifest)
    $temporary = @(Get-CimInstance Win32_Process -Filter "Name='winws.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith((Join-Path $env:ProgramData 'ZapretTester'),[StringComparison]::OrdinalIgnoreCase) })
    $result.NoTemporaryProcesses = $temporary.Count -eq 0
    if (-not $result.SourceUnchanged -or -not $result.NoTemporaryProcesses -or -not $result.Restored) { $result.Passed=$false; $result.Error += ' Final preservation/cleanup check failed.' }
    $result.FinishedAt=[DateTimeOffset]::Now
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputFile))) | Out-Null
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputFile -Encoding utf8
}
[pscustomobject]$result | Select-Object Passed,ProfileCount,Completed,PeakParallel,Restored,SourceUnchanged,NoTemporaryProcesses,Error | Format-List
if (-not $result.Passed) { exit 1 }
