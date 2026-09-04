<#
.SYNOPSIS
  Generates the three shutter sounds into src/WinShot/Assets/sounds/.

  Synthesised rather than sampled so nothing with a licence ships in the binary,
  and so the character of each one is readable here instead of in a binary blob:

    default  two-stage mirror-and-shutter click, the familiar camera sound
    classic  one sharp mechanical snap
    subtle   a soft low tick for people who want a cue, not a camera

  16-bit mono PCM at 44.1 kHz — what System.Media.SoundPlayer wants, and small
  enough (a few KB each) to embed.

  Run after changing the recipes:  powershell -File tools\make-sounds.ps1
#>
[CmdletBinding()]
param([string]$OutDir)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo 'src\WinShot\Assets\sounds' }
New-Item -ItemType Directory -Force $OutDir | Out-Null

$rate = 44100

function New-Click {
    param(
        [double]$Start,      # seconds into the clip
        [double]$Decay,      # seconds to fall to silence
        [double]$Tone,       # Hz of the body resonance
        [double]$Level,      # 0..1 peak
        [double]$Noise,      # 0..1 how much of the level is broadband
        [double[]]$Buffer
    )
    $rand = New-Object System.Random(($Tone * 1000))
    $first = [int]($Start * $rate)
    $count = [int]($Decay * $rate)
    for ($i = 0; $i -lt $count; $i++) {
        $n = $first + $i
        if ($n -ge $Buffer.Length) { break }
        $t = $i / $rate
        $env = [Math]::Exp(-$t / ($Decay / 5))
        $body = [Math]::Sin(2 * [Math]::PI * $Tone * $t)
        $hiss = ($rand.NextDouble() * 2 - 1)
        $Buffer[$n] += $Level * $env * ((1 - $Noise) * $body + $Noise * $hiss)
    }
}

function Save-Wav {
    param([double[]]$Buffer, [string]$Path)
    # Normalise to -3 dBFS so the three sounds sit at the same loudness.
    $peak = 0.0
    foreach ($v in $Buffer) { if ([Math]::Abs($v) -gt $peak) { $peak = [Math]::Abs($v) } }
    if ($peak -le 0) { $peak = 1 }
    $gain = 0.71 / $peak

    $samples = $Buffer.Length
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([char[]]'RIFF'); $w.Write([uint32](36 + $samples * 2))
    $w.Write([char[]]'WAVE'); $w.Write([char[]]'fmt ')
    $w.Write([uint32]16); $w.Write([uint16]1); $w.Write([uint16]1)
    $w.Write([uint32]$rate); $w.Write([uint32]($rate * 2)); $w.Write([uint16]2); $w.Write([uint16]16)
    $w.Write([char[]]'data'); $w.Write([uint32]($samples * 2))
    foreach ($v in $Buffer) {
        $s = [int][Math]::Round([Math]::Max(-1.0, [Math]::Min(1.0, $v * $gain)) * 32767)
        $w.Write([int16]$s)
    }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $w.Dispose(); $ms.Dispose()
    Write-Host ("{0}  {1} ms" -f (Split-Path -Leaf $Path), [int](1000 * $samples / $rate))
}

# default — mirror up, shutter closed: two clicks 55 ms apart.
$buf = New-Object 'double[]' ([int](0.16 * $rate))
New-Click -Start 0.000 -Decay 0.045 -Tone 1400 -Level 0.9 -Noise 0.55 -Buffer $buf
New-Click -Start 0.055 -Decay 0.070 -Tone 900  -Level 0.7 -Noise 0.45 -Buffer $buf
Save-Wav $buf (Join-Path $OutDir 'shutter-default.wav')

# classic — one hard snap.
$buf = New-Object 'double[]' ([int](0.10 * $rate))
New-Click -Start 0.000 -Decay 0.090 -Tone 2100 -Level 1.0 -Noise 0.70 -Buffer $buf
Save-Wav $buf (Join-Path $OutDir 'shutter-classic.wav')

# subtle — a low tick, no hiss to speak of.
$buf = New-Object 'double[]' ([int](0.08 * $rate))
New-Click -Start 0.000 -Decay 0.070 -Tone 520 -Level 0.6 -Noise 0.15 -Buffer $buf
Save-Wav $buf (Join-Path $OutDir 'shutter-subtle.wav')
