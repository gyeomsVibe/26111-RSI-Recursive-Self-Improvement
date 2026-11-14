param([ValidateSet('Apply','Verify')][string]$Mode='Verify')
$ErrorActionPreference='Stop'
. "$PSScriptRoot\Inspect-NvidiaProfile.ps1"
if($name -ne 'Crimson Desert'){throw 'Unexpected driver profile; no settings changed'}
$desired=@(
 @{Name='Power management: prefer maximum performance';Id=[uint32]0x1057EB71;Value=[uint32]1},
 @{Name='Texture filtering quality: High Quality (Zero Chalky Blur)';Id=[uint32]0x00CE2691;Value=[uint32]4294967286},
 @{Name='Texture filtering: Trilinear optimization Off (Full Precision)';Id=[uint32]0x002ECAF2;Value=[uint32]1},
 @{Name='Anisotropic filter: Optimization Off (Preserve Oblique Sharpness)';Id=[uint32]0x0084CD70;Value=[uint32]0},
 @{Name='Anisotropic filter: Sample optimization Off (Preserve Detail)';Id=[uint32]0x00E73211;Value=[uint32]0},
 @{Name='Anisotropic filtering mode: User-defined / Forced';Id=[uint32]0x101E61A9;Value=[uint32]1},
 @{Name='Anisotropic filtering setting: 16x Hardware AF';Id=[uint32]0x10D2BB16;Value=[uint32]16},
 @{Name='Texture filtering Negative LOD bias: Clamp';Id=[uint32]0x0019BB68;Value=[uint32]1},
 @{Name='Driver ambient occlusion: off (game rendering unchanged)';Id=[uint32]0x00667329;Value=[uint32]0},
 @{Name='Antialiasing mode: application controlled';Id=[uint32]0x107EFC5B;Value=[uint32]0},
 @{Name='FXAA: off';Id=[uint32]0x1074C972;Value=[uint32]0},
 @{Name='Driver shader disk cache: on';Id=[uint32]0x00198FFF;Value=[uint32]1},
 @{Name='Shader Cache Size: 10GB (long-session anti-eviction)';Id=[uint32]0x00AC8497;Value=[uint32]0x00002800},
 @{Name='Threaded Optimization: On (i7-6700K 8-thread DX12 dispatch)';Id=[uint32]0x20C1221E;Value=[uint32]1},
 @{Name='Maximum Pre-Rendered Frames: 1 (eliminate 62ms GPU queue latency)';Id=[uint32]0x007BA09E;Value=[uint32]1},
 @{Name='Ultra Low Latency Enabled: On';Id=[uint32]0x10835000;Value=[uint32]1},
 @{Name='Maximum frame rate ceiling: 60';Id=[uint32]0x10835002;Value=[uint32]60}
)
$before=@($desired | ForEach-Object {[pscustomobject]@{Name=$_.Name;Id=$_.Id;Value=$settings.GetDwordValueFromProfile($name,$_.Id,$true,$true)}})
if($Mode -eq 'Apply'){
 $stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
 $before | ConvertTo-Json | Set-Content -LiteralPath "$PSScriptRoot\research\nvidia-inspector\before-$stamp.json" -Encoding UTF8
 $batch=New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[uint32,string]]'
 foreach($item in $desired){$text=('0x{0:X8}' -f $item.Value);$pair=New-Object 'System.Collections.Generic.KeyValuePair[uint32,string]'($item.Id,$text);$batch.Add($pair)}
 [void]$settings.StoreSettingsToProfile($name,$batch)
}
$after=@($desired | ForEach-Object {$read=$settings.GetDwordValueFromProfile($name,$_.Id,$true,$true);[pscustomobject]@{Name=$_.Name;Id=$_.Id;Desired=$_.Value;Actual=$read;Matches=($read -eq $_.Value)}})
$after | ConvertTo-Json | Set-Content -LiteralPath "$PSScriptRoot\research\nvidia-inspector\verification-$Mode.json" -Encoding UTF8
if($Mode -eq 'Apply' -and @($after | Where-Object {-not $_.Matches}).Count){throw 'NVIDIA saved setting verification mismatch'}
if($Mode -eq 'Apply'){
 $selected=New-Object 'System.Collections.Generic.List[string]';$selected.Add($name)
 $imports.ExportProfiles($selected,"$PSScriptRoot\profiles\Crimson-Driver-GTX1070.nip",$false)
}
$after | Format-Table Name,Actual,Matches
