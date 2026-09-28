param([Parameter(Mandatory)][string]$FlowsealFolder,[Parameter(Mandatory)][string]$OutputFile,[switch]$AllowServiceRestart)
$ErrorActionPreference='Stop'
if (-not $AllowServiceRestart) { throw 'Explicit -AllowServiceRestart is required.' }
$helper=Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ZapretTester.Helper\bin\Debug\net10.0-windows\ZapretTester.Helper.exe'
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$result=[ordered]@{Passed=$false;Cancelled=$false;Reconnected=$false;Rollback=$false;CrashCleaned=$false;StartupRecovery=$false;Restored=$false;Error=$null}
$pipe=$null; $writer=$null; $reader=$null; $process=$null
function Open-Client {
    $script:pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    $script:pipe.Connect(30000)
    $script:writer=[IO.StreamWriter]::new($script:pipe,[Text.UTF8Encoding]::new($false),4096,$true)
    $script:reader=[IO.StreamReader]::new($script:pipe,[Text.UTF8Encoding]::new($false),$false,4096,$true)
}
function Start-Helper {
    $script:pipeName='ZapretTester.lifecycle.'+[Guid]::NewGuid().ToString('N')
    $script:nonce=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $script:process=Start-Process -FilePath $helper -ArgumentList @('"'+$FlowsealFolder+'"',$script:pipeName,$script:nonce,$sid) -Verb RunAs -WindowStyle Hidden -PassThru
    Open-Client
}
function Send-Request([string]$command,[string]$id='') {
    $request=[Guid]::NewGuid().ToString('N')
    $writer.WriteLine((@{Command=$command;Nonce=$script:nonce;ProfileId=$(if($id){$id}else{$null});RequestId=$request}|ConvertTo-Json -Compress));$writer.Flush()
    if($command -eq 'CrashForAcceptance'){return}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.Elapsed.TotalSeconds -lt 40){
        $line=$reader.ReadLineAsync()
        if(-not $line.Wait([TimeSpan]::FromSeconds(40-$watch.Elapsed.TotalSeconds))){throw 'Reply timeout.'}
        if($null -eq $line.Result){throw 'Disconnected.'}
        $message=$line.Result|ConvertFrom-Json
        if($message.RequestId -eq $request){
            if($message.Kind -eq 'Error'){throw $message.Error}
            return $message.State
        }
    }
    throw 'No matching reply.'
}
function Close-Client {
    if($script:writer){try{$script:writer.Dispose()}catch{}}
    if($script:reader){$script:reader.Dispose()}
    if($script:pipe){$script:pipe.Dispose()}
}
try {
    Start-Helper
    $state=Send-Request 'State'
    $id=($state.Profiles|Where-Object Name -eq 'ALT9'|Select-Object -First 1).Id
    $state=Send-Request 'StartManaged' $id
    $mainPid=[int]([regex]::Match($state.Diagnostics,'Main PID: (\d+)').Groups[1].Value)
    $null=Send-Request 'Test'
    $state=Send-Request 'Cancel'
    if($state.IsTesting -or $state.Mode -ne 'Managed'){throw 'Cancel affected main or failed to stop run.'}
    $result.Cancelled=$true
    Close-Client
    Open-Client
    $state=Send-Request 'State'
    if($state.Mode -ne 'Managed' -or [int]([regex]::Match($state.Diagnostics,'Main PID: (\d+)').Groups[1].Value) -ne $mainPid){throw 'Main did not survive disconnect/reconnect.'}
    $result.Reconnected=$true
    $null=Send-Request 'FailNextMainForAcceptance'
    $failed=$false
    try { $null=Send-Request 'StartManaged' (($state.Profiles|Where-Object Name -eq 'ALT10'|Select-Object -First 1).Id) }
    catch { if($_.Exception.Message -notlike '*Injected main startup failure*'){throw};$failed=$true }
    $state=Send-Request 'State'
    if(-not $failed -or $state.Mode -ne 'Managed' -or $state.MainProfileId -ne $id){throw 'Main rollback failed.'}
    $result.Rollback=$true
    # A real helper-process exit, rather than graceful Dispose, exercises kernel job cleanup.
    $null=Send-Request 'Test'
    Send-Request 'CrashForAcceptance'
    $null=$process.WaitForExit(10000)
    Close-Client
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while((Get-Process -Name winws -ErrorAction SilentlyContinue) -and $watch.Elapsed.TotalSeconds -lt 10){Start-Sleep -Milliseconds 100}
    if(Get-Process -Name winws -ErrorAction SilentlyContinue){throw 'An owned winws survived helper crash.'}
    $result.CrashCleaned=$true
    Start-Helper
    $state=Send-Request 'State'
    if($state.Mode -ne 'Managed'){throw 'Managed configuration did not resume after helper restart.'}
    Send-Request 'CrashForAcceptance'
    $null=$process.WaitForExit(10000)
    Close-Client
    $settingsFile=Join-Path $env:LOCALAPPDATA 'ZapretTester\settings.json'
    $savedSettings=Get-Content -LiteralPath $settingsFile -Raw|ConvertFrom-Json
    $savedSettings.MainProfileId='MISSING-PROFILE-FOR-ACCEPTANCE'
    $savedSettings|ConvertTo-Json|Set-Content -LiteralPath $settingsFile -Encoding utf8
    Start-Helper
    $state=Send-Request 'State'
    if($state.Mode -ne 'Compatibility' -or -not $state.Error){throw 'Startup failure must leave IPC available for recovery.'}
    $result.StartupRecovery=$true
    $null=Send-Request 'RestoreExternal'
    $result.Restored=(Get-Service zapret).Status -eq 'Running'
    if(-not $result.Restored){throw 'Original service was not restored.'}
    $null=Send-Request 'Shutdown'
    $result.Passed=$true
} catch {$result.Error=$_.Exception.ToString()}
finally {
    if(-not $result.Restored -and $pipe -and $pipe.IsConnected){
        try{$null=Send-Request 'RestoreExternal';$result.Restored=$true;$null=Send-Request 'Shutdown'}catch{$result.Error+="`nRecovery: "+$_.Exception.Message}
    }
    Close-Client
    if($process){$null=$process.WaitForExit(10000)}
    if($result.Restored -and $settingsFile){
        $savedSettings=Get-Content -LiteralPath $settingsFile -Raw|ConvertFrom-Json
        $savedSettings.MainProfileId=$id
        $savedSettings|ConvertTo-Json|Set-Content -LiteralPath $settingsFile -Encoding utf8
    }
    $result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputFile -Encoding utf8
}
[pscustomobject]$result|Format-List
if(-not $result.Passed){exit 1}
