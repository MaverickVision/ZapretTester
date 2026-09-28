param([Parameter(Mandatory)][string]$HelperPath,[Parameter(Mandatory)][string]$OutputFile,[switch]$PositiveOnly,[switch]$FailureOnly,[Parameter(Mandatory)][string]$FlowsealFolder,[Parameter(Mandatory)][string]$LeagueFolder,[switch]$AllowServiceRestart)
$ErrorActionPreference='Stop'
if (-not $AllowServiceRestart) { throw 'Pass -AllowServiceRestart to permit network and managed-profile changes.' }
$folder=[IO.Path]::GetFullPath($FlowsealFolder)
function SourceHash { (@(Get-ChildItem -LiteralPath $folder -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash }) + @(Get-FileHash -LiteralPath (Join-Path $LeagueFolder 'system.yaml'),(Join-Path $LeagueFolder 'Config\LeagueClientSettings.yaml')|ForEach-Object Hash)) -join "`n" }
$beforeHash=SourceHash
$pipeName='ZapretTester.league.'+[Guid]::NewGuid().ToString('N')
$nonce=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$result=[ordered]@{Passed=$false;PeakParallel=0;Completed=0;TabsIndependent=$false;SwitchDidNotCancel=$false;MainVariant=$false;Cancelled=$false;Rollback=$false;ForeignRejected=$false;SourceUnchanged=$false;Cleaned=$false;Error=$null}
$process=Start-Process -FilePath $HelperPath -ArgumentList @('"'+$folder+'"',$pipeName,$nonce,$sid) -Verb RunAs -WindowStyle Hidden -PassThru
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
$w=$null;$r=$null;$foreign=$null
function ReadMessage([int]$timeout=60000){
    $line=$r.ReadLineAsync();if(-not $line.Wait($timeout)){throw 'IPC timeout.'}
    if($null -eq $line.Result){throw 'Helper disconnected.'}
    $message=$line.Result|ConvertFrom-Json
    if($message.Kind -eq 'Error'){throw $message.Error}
    if($message.State){$result.PeakParallel=[Math]::Max($result.PeakParallel,@($message.State.Profiles|Where-Object RunState -eq 2).Count)}
    return $message
}
function Send([string]$command,[int]$suite=0,[string]$id=''){
    $request=[Guid]::NewGuid().ToString('N')
    $w.WriteLine((@{Command=$command;Nonce=$nonce;ProfileId=$(if($id){$id}else{$null});RequestId=$request;Suite=$suite;Region=$(if($suite -eq 1){'RU'}else{$null})}|ConvertTo-Json -Compress));$w.Flush()
    do{$message=ReadMessage}while($message.RequestId -ne $request)
    return $message.State
}
function WaitIdle($state){
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($state.IsTesting -and $timer.Elapsed.TotalSeconds -lt 360){$m=ReadMessage 90000;if($m.State){$state=$m.State}}
    if($state.IsTesting){throw 'Series timeout.'}
    return $state
}
try{
    $pipe.Connect(30000);$w=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true);$r=[IO.StreamReader]::new($pipe,[Text.UTF8Encoding]::new($false),$false,4096,$true)
    $state=Send 'State';$expectedParallel=$state.Settings.MaxParallelTests;$general=($state.Profiles|Where-Object Name -eq 'General').Id
    $discordBefore=@($state.Profiles|ForEach-Object LastResult)|ConvertTo-Json -Depth 10 -Compress
    $null=Send 'StartManaged' 0 $general
    if(-not $FailureOnly){
    $state=Send 'SelectSuite' 1
    if($state.Targets.Count -ne 4 -or $state.Region -ne 'RU' -or @($state.Profiles|Where-Object Error).Count -ne 0){throw ('LoL configuration failed: '+$state.Error)}
    $started=[DateTimeOffset]::UtcNow
    $state=Send 'Test' 1
    $state=Send 'SelectSuite' 0
    if(-not $state.IsTesting -or $state.ActiveSuite -ne 1 -or $state.Suite -ne 0){throw 'Tab switch lost active LoL series.'}
    $result.SwitchDidNotCancel=$true
    if(($state.Profiles|Where-Object {$_.RunState -eq 1 -or $_.RunState -eq 2})){throw 'LoL run state leaked into Discord.'}
    $state=Send 'SelectSuite' 1
    $state=WaitIdle $state
    $state=Send 'State'
    $result.Profiles=$state.Profiles
    $result.Completed=@($state.Profiles|Where-Object {$_.LastResult -and $_.LastResult.Suite -eq 1 -and $_.LastResult.Region -eq 'RU' -and $_.LastResult.IsReliable -and $_.LastResult.Endpoints.Count -eq 4 -and [DateTimeOffset]$_.LastResult.TestedAt -ge $started}).Count
    if($result.Completed -ne 22){throw ('Expected 22 fresh reliable measurements. '+$state.Error)}
    $state=Send 'SelectSuite' 0
    $discordAfter=@($state.Profiles|ForEach-Object LastResult)|ConvertTo-Json -Depth 10 -Compress
    if($discordBefore -cne $discordAfter){throw 'Discord results changed during LoL testing.'}
    $result.TabsIndependent=$true
    }
    $state=Send 'StartManaged' 1 $general
    if($state.Settings.MainSuite -ne 1 -or $state.Settings.MainRegion -ne 'RU'){throw 'Main variant mismatch.'}
    $result.MainVariant=$true
    if(-not $PositiveOnly){
    $null=Send 'SelectSuite' 1
    $null=Send 'Test' 1
    $state=Send 'Cancel'
    if($state.IsTesting -or $state.Mode -ne 'Managed'){throw 'Cancel failed or stopped main.'}
    $result.Cancelled=$true
    $null=Send 'FailNextMainForAcceptance'
    $failed=$false
    try{$null=Send 'StartManaged' 1 'GENERAL (ALT5).BAT'}catch{if($_.Exception.Message -notlike '*Injected main startup failure*'){throw};$failed=$true}
    $state=Send 'State'
    if(-not $failed -or $state.MainProfileId -ne $general -or $state.Settings.MainSuite -ne 1 -or $state.Mode -ne 'Managed'){throw 'LoL rollback failed.'}
    $result.Rollback=$true
    $null=Send 'Test' 1
    $state=Send 'State'
    while($state.IsTesting -and @($state.Profiles|Where-Object RunState -eq 2).Count -eq 0){Start-Sleep -Milliseconds 100;$state=Send 'State'}
    $probe=Join-Path (Split-Path -Parent $PSScriptRoot) 'src\TestIsolation\bin\Debug\net10.0-windows\TestIsolation.exe'
    $foreign=Start-Process -FilePath $probe -ArgumentList @('--foreign-probe',('"'+$folder+'"'),'41998') -Verb RunAs -WindowStyle Hidden -PassThru
    $state=WaitIdle (Send 'State')
    if($state.Error -notlike '*WinDivert handle*'){throw ('Expected foreign-handle rejection: '+$state.Error)}
    $result.ForeignRejected=$true
    $null=$foreign.WaitForExit(20000)
    }
    if($result.PeakParallel -gt $expectedParallel){throw "Displayed concurrency exceeded $expectedParallel."}
    $result.Passed=$true
}catch{$result.Error=$_.Exception.ToString()}
finally{
    if($pipe.IsConnected -and $w){try{$null=Send 'Cancel';$null=Send 'StopMain';$null=Send 'Shutdown';$result.Cleaned=$true}catch{$result.Error+=" Cleanup: "+$_.Exception.Message;$result.Passed=$false}}
    if($w){try{$w.Dispose()}catch{}};if($r){$r.Dispose()};$pipe.Dispose();$null=$process.WaitForExit(15000)
    $result.SourceUnchanged=$beforeHash -ceq (SourceHash)
    if(-not $result.SourceUnchanged){$result.Passed=$false}
    $result|ConvertTo-Json -Depth 14|Set-Content -LiteralPath $OutputFile -Encoding utf8
}
[pscustomobject]$result|Select-Object Passed,Completed,PeakParallel,TabsIndependent,SwitchDidNotCancel,MainVariant,Cancelled,Rollback,ForeignRejected,SourceUnchanged,Cleaned,Error|Format-List
if(-not $result.Passed){exit 1}

