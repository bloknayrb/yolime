# Regenerates the 16 kHz mono 16-bit WAV fixtures in tests/fixtures/audio. Speech comes from System.Speech written
# straight to a file, so nothing plays aloud. Noise is seeded, so reruns produce identical files.
# Run under Windows PowerShell 5.1:  powershell.exe -NoProfile -File tools/make-test-audio.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$out = Join-Path $PSScriptRoot '..\tests\fixtures\audio'
New-Item -ItemType Directory -Force $out | Out-Null
$rate = 16000

function Write-Wav([string]$path, [int16[]]$samples) {
    $data = New-Object byte[] ($samples.Length * 2)
    [Buffer]::BlockCopy($samples, 0, $data, 0, $data.Length)
    $fs = [IO.File]::Create($path)
    $w = New-Object IO.BinaryWriter $fs
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $data.Length))
    $w.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
    $w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]$data.Length); $w.Write($data)
    $w.Close()
}

function Read-Pcm([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    # Find the "data" chunk rather than assuming a 44-byte header; System.Speech may write extra chunks.
    $i = 12
    while ($i -lt $bytes.Length - 8) {
        $id = [Text.Encoding]::ASCII.GetString($bytes, $i, 4)
        $size = [BitConverter]::ToInt32($bytes, $i + 4)
        if ($id -eq 'data') {
            $samples = New-Object int16[] ($size / 2)
            [Buffer]::BlockCopy($bytes, $i + 8, $samples, 0, $size)
            return ,$samples
        }
        $i += 8 + $size
    }
    throw "no data chunk in $path"
}

function Scale-ToPeak([int16[]]$samples, [double]$peakDbfs) {
    $peak = 1
    foreach ($s in $samples) { $a = [math]::Abs([int]$s); if ($a -gt $peak) { $peak = $a } }
    $target = [math]::Pow(10, $peakDbfs / 20) * 32767
    $k = $target / $peak
    $r = New-Object int16[] $samples.Length
    for ($j = 0; $j -lt $samples.Length; $j++) { $r[$j] = [int16][math]::Round($samples[$j] * $k) }
    return ,$r
}

# Uniform noise with RMS = 10^(dbfs/20); a uniform [-a, a] distribution has RMS a/sqrt(3).
function New-Noise([int]$count, [double]$rmsDbfs, [int]$seed) {
    $rng = New-Object Random $seed
    $a = [math]::Pow(10, $rmsDbfs / 20) * [math]::Sqrt(3) * 32767
    $r = New-Object int16[] $count
    for ($j = 0; $j -lt $count; $j++) { $r[$j] = [int16][math]::Round(($rng.NextDouble() * 2 - 1) * $a) }
    return ,$r
}

$tts = Join-Path $env:TEMP "yolime-tts-$([guid]::NewGuid().ToString('N')).wav"
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo($rate, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$synth.SetOutputToWaveFile($tts, $fmt)
$synth.Speak('The quick brown fox jumps over the lazy dog.')
$synth.Dispose()
$speech = Read-Pcm $tts
Remove-Item $tts

Write-Wav (Join-Path $out 'speech-short.wav') $speech
$quiet = Scale-ToPeak $speech -30
Write-Wav (Join-Path $out 'speech-quiet.wav') $quiet
Write-Wav (Join-Path $out 'silence-2s.wav') (New-Object int16[] (2 * $rate))
Write-Wav (Join-Path $out 'noise-floor-5s.wav') (New-Noise (5 * $rate) -60 1)

# 30 s of noise with the quiet phrase mixed into the middle: a short utterance inside a long latched hold.
$long = New-Noise (30 * $rate) -60 2
$start = [int]((15 * $rate) - ($quiet.Length / 2))
for ($j = 0; $j -lt $quiet.Length; $j++) {
    $v = [int]$long[$start + $j] + [int]$quiet[$j]
    $long[$start + $j] = [int16][math]::Max(-32768, [math]::Min(32767, $v))
}
Write-Wav (Join-Path $out 'speech-in-long-noise.wav') $long

Get-ChildItem $out -Filter *.wav | Select-Object Name, Length | Format-Table -AutoSize
