param([ValidateSet('Enforce','Verify','Disable','Restore')][string]$Mode='Verify',[switch]$ForceFull)
$ErrorActionPreference='Stop'
$policyRoot=$PSScriptRoot
$configPath=Join-Path $policyRoot 'policy.json'
$config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$taskName='CrimsonDesert-NvidiaProfile-Policy'
$statusPath=Join-Path $policyRoot 'status.json'
if($Mode -in @('Disable','Restore')){Disable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null}
if($Mode -eq 'Disable'){Write-Output 'Automatic enforcement disabled; saved driver values retained.';exit 0}
$lock=$null
try {
 $lock=[IO.File]::Open((Join-Path $policyRoot 'policy.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 foreach($asset in $config.EngineFiles){
  $file=Join-Path $policyRoot $asset.RelativePath
  if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.SHA256){throw 'Pinned policy engine integrity mismatch'}
 }
 function Get-DrsStamp {
  $dbDir=Join-Path $env:ProgramData 'NVIDIA Corporation\Drs'
  return (@(Get-ChildItem -LiteralPath $dbDir -File -ErrorAction Stop | Where-Object {$_.Extension -in @('.bin','.bak')} | Sort-Object Name | ForEach-Object {$_.Name+':'+$_.Length+':'+$_.LastWriteTimeUtc.Ticks}) -join '|')
 }
 $stamp=Get-DrsStamp
 if($Mode -eq 'Enforce' -and -not $ForceFull -and (Test-Path -LiteralPath $statusPath)){
  $previous=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
  if($previous.Success -and $previous.DrsStamp -eq $stamp -and $previous.PolicySHA256 -eq (Get-FileHash -LiteralPath $configPath).Hash){
   $previous.LastCheckUtc=[DateTime]::UtcNow.ToString('o')
   $previous | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statusPath -Encoding UTF8
   Write-Output 'Driver database unchanged since last full verification.';exit 0
  }
 }
 $exe=Join-Path $policyRoot 'engine\nvidiaProfileInspector.exe'
 $assembly=[Reflection.Assembly]::LoadFrom($exe)
 $customType=$assembly.GetType('nvidiaProfileInspector.Common.CustomSettings.CustomSettingNames',$true)
 $stream=$assembly.GetManifestResourceStream('nvidiaProfileInspector.CustomSettingNames.xml')
 if(-not $stream){throw 'Embedded metadata missing'}
 $reader=New-Object IO.StreamReader($stream)
 try{$embedded=$reader.ReadToEnd()}finally{$reader.Dispose()}
 $custom=$customType.GetMethod('FactoryLoadFromString').Invoke($null,[object[]]@($embedded))
 $referencePath=[string](Join-Path $policyRoot 'engine\Reference.xml')
 $reference=$customType.GetMethod('FactoryLoadFromFile').Invoke($null,[object[]]@($referencePath))
 $meta=[Activator]::CreateInstance($assembly.GetType('nvidiaProfileInspector.Common.DrsSettingsMetaService',$true),[object[]]@($custom,$reference))
 $decrypt=[Activator]::CreateInstance($assembly.GetType('nvidiaProfileInspector.Common.DrsDecrypterService',$true),[object[]]@($meta))
 $settings=[Activator]::CreateInstance($assembly.GetType('nvidiaProfileInspector.Common.DrsSettingsService',$true),[object[]]@($meta,$decrypt))
 $name=$settings.GetProfileNameByExeName([string]$config.Executable)
 if(-not $name -and $Mode -eq 'Enforce'){
  $baseName='';$names=$settings.GetProfileNames([ref]$baseName)
  $name=[string]$config.ProfileName
  if($name -notin $names){$settings.CreateProfile($name,[string]$config.Executable)}else{$settings.AddApplication($name,[string]$config.Executable)}
 }
 if(-not $name -or $name -eq 'Base Profile'){throw 'Dedicated game driver profile unavailable'}
 $changed=0
 if($Mode -eq 'Restore'){
  foreach($item in $config.OriginalOverrides){
   if($item.Present){$settings.SetDwordValueToProfile($name,[uint32]$item.Id,[uint32]$item.Value)}
   else{$remove=$false;$settings.DeleteValue($name,[uint32]$item.Id,[ref]$remove)}
  }
  Write-Output 'Original per-game overrides restored. Automatic enforcement remains disabled.';exit 0
 }
 $batch=New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[uint32,string]]'
 foreach($item in $config.Settings){
  $actual=$settings.GetDwordValueFromProfile($name,[uint32]$item.Id,$true,$true)
  if($actual -ne [uint32]$item.Value){$batch.Add((New-Object 'System.Collections.Generic.KeyValuePair[uint32,string]'([uint32]$item.Id,('0x{0:X8}' -f [uint32]$item.Value))))}
 }
 if($Mode -eq 'Enforce' -and $batch.Count){$changed=$batch.Count;[void]$settings.StoreSettingsToProfile($name,$batch)}
 $results=@($config.Settings | ForEach-Object {$actual=$settings.GetDwordValueFromProfile($name,[uint32]$_.Id,$true,$true);[pscustomobject]@{Name=$_.Name;Id=$_.Id;Expected=$_.Value;Actual=$actual;Matches=($actual -eq [uint32]$_.Value)}})
 $ok=@($results | Where-Object {-not $_.Matches}).Count -eq 0
 $record=[ordered]@{Success=$ok;LastCheckUtc=[DateTime]::UtcNow.ToString('o');LastFullVerifyUtc=[DateTime]::UtcNow.ToString('o');Mode=$Mode;ChangedCount=$changed;Profile=$name;PolicySHA256=(Get-FileHash -LiteralPath $configPath).Hash;DrsStamp=(Get-DrsStamp);Values=$results}
 $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statusPath -Encoding UTF8
 if(-not $ok){throw 'One or more saved NVIDIA values differ from policy'}
 Write-Output ('Verified '+$results.Count+' game settings; restored '+$changed+' values.')
} catch {
 [ordered]@{Success=$false;LastCheckUtc=[DateTime]::UtcNow.ToString('o');Mode=$Mode;Error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
 throw
} finally {if($lock){$lock.Dispose()}}
