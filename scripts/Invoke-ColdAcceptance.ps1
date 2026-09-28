param([Parameter(Mandatory)][string]$HelperPath,[Parameter(Mandatory)][string]$FlowsealFolder,[Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference='Stop'
$pipeName='ZapretTester.cold.'+[Guid]::NewGuid().ToString('N')
$nonce=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$result=[ordered]@{Passed=$false;DriverBefore=@(Get-Service '*WinDivert*' -ErrorAction SilentlyContinue|ForEach-Object Name);Completed=$false;Error=$null}
$process=Start-Process -FilePath $HelperPath -ArgumentList @('"'+$FlowsealFolder+'"',$pipeName,$nonce,$sid) -Verb RunAs -WindowStyle Hidden -PassThru
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
$writer=$null;$reader=$null
function Send([string]$command,[string]$id=''){
    $script:requestId=[Guid]::NewGuid().ToString('N')
    $writer.WriteLine((@{Command=$command;Nonce=$nonce;ProfileId=$(if($id){$id}else{$null});RequestId=$script:requestId}|ConvertTo-Json -Compress));$writer.Flush()
}
function Wait-Reply([bool]$finished=$false){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.Elapsed.TotalSeconds -lt 90){
        $line=$reader.ReadLineAsync()
        if(-not $line.Wait([TimeSpan]::FromSeconds(90-$watch.Elapsed.TotalSeconds))){throw 'IPC timeout.'}
        if($null -eq $line.Result){throw 'Helper disconnected.'}
        $msg=$line.Result|ConvertFrom-Json
        if($msg.Kind -eq 'Error'){throw $msg.Error}
        if($finished){if($msg.State -and -not $msg.State.IsTesting){return $msg.State}}
        elseif($msg.RequestId -eq $script:requestId){return $msg.State}
    }
    throw 'No expected state.'
}
try{
    $pipe.Connect(30000)
    $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
    $reader=[IO.StreamReader]::new($pipe,[Text.UTF8Encoding]::new($false),$false,4096,$true)
    Send 'State';$state=Wait-Reply
    if($state.Mode -ne 'Compatibility'){throw 'Expected compatibility mode without main.'}
    $id=($state.Profiles|Where-Object Name -eq 'General'|Select-Object -First 1).Id
    Send 'Test' $id;$state=Wait-Reply
    if($state.IsTesting){$state=Wait-Reply $true}
    $row=$state.Profiles|Where-Object Id -eq $id
    $result.Completed=$row.RunState -eq 0 -and $row.LastResult.IsReliable -and $row.LastResult.Endpoints.Count -eq 4
    $result.Measurement=$row.LastResult
    if(-not $result.Completed){throw $state.Error}
    $result.Passed=$true
} catch{$result.Error=$_.Exception.Message}
finally{
    if($pipe.IsConnected -and $writer){try{Send 'Cancel';$null=Wait-Reply;Send 'Shutdown';$null=Wait-Reply}catch{}}
    if($writer){try{$writer.Dispose()}catch{}};if($reader){$reader.Dispose()};$pipe.Dispose()
    $null=$process.WaitForExit(10000)
    $result.DriverAfter=@(Get-Service '*WinDivert*' -ErrorAction SilentlyContinue|ForEach-Object Name)
    $result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $OutputFile -Encoding utf8
}
[pscustomobject]$result|Select-Object Passed,DriverBefore,DriverAfter,Completed,Error|Format-List
if(-not $result.Passed){exit 1}
