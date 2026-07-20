[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Program Files\CDisplayEx',
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$exePath = Join-Path $InstallDir 'CDisplayEx.exe'
$backupPath = Join-Path $InstallDir 'CDisplayEx.exe.before-slider-patch.bak'

if ($Restore) {
    if (-not (Test-Path -LiteralPath $backupPath)) { throw "Backup not found: $backupPath" }
    Copy-Item -LiteralPath $backupPath -Destination $exePath -Force
    Write-Host "Restored: $exePath"
    exit 0
}

$bytes = [IO.File]::ReadAllBytes($exePath)
$s=[Text.Encoding]::ASCII.GetString($bytes)
$panel='pnSlider'+[char]0x04+'Left'
$panelAt=$s.IndexOf($panel,[StringComparison]::Ordinal)
if($panelAt -lt 0 -or $s.IndexOf($panel,$panelAt+1,[StringComparison]::Ordinal) -ge 0){throw 'pnSlider panel marker is not unique'}
$falseProp=[char]0x07+'Visible'+[char]0x08+[char]0x0A+'DesignSize'
$trueProp=[char]0x07+'Visible'+[char]0x09+[char]0x0A+'DesignSize'
$falseAt=$s.IndexOf($falseProp,$panelAt,[StringComparison]::Ordinal)
$trueAt=$s.IndexOf($trueProp,$panelAt,[StringComparison]::Ordinal)
if($falseAt -lt 0 -and $trueAt -ge 0 -and $trueAt-$panelAt -le 256){Write-Host 'Slider is already enabled by default.'; exit 0}
if($falseAt -lt 0 -or $falseAt-$panelAt -gt 256){throw 'Visible=False marker was not found inside pnSlider'}
if(-not (Test-Path -LiteralPath $backupPath)){[IO.File]::WriteAllBytes($backupPath,$bytes)}
$patchOffset=$falseAt+8
if($bytes[$patchOffset] -ne 0x08){throw 'Target byte is not False'}
$bytes[$patchOffset]=0x09
[IO.File]::WriteAllBytes($exePath,$bytes)
$verified=[IO.File]::ReadAllBytes($exePath)
$original=[IO.File]::ReadAllBytes($backupPath)
$diff=0; $diffAt=-1
for($i=0;$i -lt $verified.Length;$i++){if($verified[$i] -ne $original[$i]){$diff++;$diffAt=$i}}
if($diff -ne 1 -or $diffAt -ne $patchOffset -or $verified[$patchOffset] -ne 0x09){[IO.File]::WriteAllBytes($exePath,$original); throw 'Verification failed; original restored'}
Write-Host ('Patched offset 0x{0:X8}: 08 -> 09; differing bytes: {1}' -f $patchOffset,$diff)
Write-Host "Backup: $backupPath"
exit 0
$falsePattern = [Text.Encoding]::ASCII.GetBytes('pnSlider') +
    [byte[]](0x04) + [Text.Encoding]::ASCII.GetBytes('Left') +
    [byte[]](0x02,0x00,0x03) + [Text.Encoding]::ASCII.GetBytes('Top') +
    [byte[]](0x03,0x22,0x02) + [Text.Encoding]::ASCII.GetBytes('Width') +
    [byte[]](0x03,0xFB,0x02) + [Text.Encoding]::ASCII.GetBytes('Height') +
    [byte[]](0x02,0x18,0x05) + [Text.Encoding]::ASCII.GetBytes('Align') +
    [byte[]](0x07,0x08) + [Text.Encoding]::ASCII.GetBytes('alBottom') +
    [byte[]](0x0A) + [Text.Encoding]::ASCII.GetBytes('BevelOuter') +
    [byte[]](0x07,0x06) + [Text.Encoding]::ASCII.GetBytes('bvNone') +
    [byte[]](0x08) + [Text.Encoding]::ASCII.GetBytes('TabOrder') +
    [byte[]](0x02,0x01,0x07) + [Text.Encoding]::ASCII.GetBytes('Visible') +
    [byte[]](0x08,0x0A) + [Text.Encoding]::ASCII.GetBytes('DesignSize')
$truePattern = $falsePattern.Clone()
$visibleValueIndex = $falsePattern.Length - ([Text.Encoding]::ASCII.GetByteCount('DesignSize') + 2)
$truePattern[$visibleValueIndex] = 0x09

function Find-Pattern([byte[]]$Haystack, [byte[]]$Needle) {
    $matches = [Collections.Generic.List[int]]::new()
    for ($i = 0; $i -le $Haystack.Length - $Needle.Length; $i++) {
        $found = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $found = $false; break }
        }
        if ($found) { $matches.Add($i) }
    }
    return $matches.ToArray()
}

$falseMatches = @(Find-Pattern $bytes $falsePattern)
$trueMatches = @(Find-Pattern $bytes $truePattern)
if ($falseMatches.Count -eq 0 -and $trueMatches.Count -eq 1) {
    Write-Host 'Slider is already enabled by default.'
    exit 0
}
if ($falseMatches.Count -ne 1 -or $trueMatches.Count -ne 0) {
    throw "Safety check failed (False matches: $($falseMatches.Count), True matches: $($trueMatches.Count)). No changes made."
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $exePath -Destination $backupPath
}

$patchOffset = $falseMatches[0] + $visibleValueIndex
if ($bytes[$patchOffset] -ne 0x08) { throw ('Unexpected byte at 0x{0:X}' -f $patchOffset) }
$bytes[$patchOffset] = 0x09
[IO.File]::WriteAllBytes($exePath, $bytes)

$verified = [IO.File]::ReadAllBytes($exePath)
if (@(Find-Pattern $verified $falsePattern).Count -ne 0 -or @(Find-Pattern $verified $truePattern).Count -ne 1) {
    Copy-Item -LiteralPath $backupPath -Destination $exePath -Force
    throw 'Verification failed; the original executable was restored.'
}

Write-Host ('Patched byte at file offset 0x{0:X8}: Visible=False -> Visible=True' -f $patchOffset)
Write-Host "Backup: $backupPath"
