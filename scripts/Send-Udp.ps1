param(
    [Parameter(Mandatory)][ValidateSet('start','stop','cancel','status')][string]$Action,
    [string]$RecordingId,
    [int]$Port=0,
    [string]$Token
)
$ErrorActionPreference='Stop'
if($Action -ne 'status' -and -not $RecordingId){throw 'Supply the same -RecordingId for start and stop; use a new ID for each utterance.'}
if(-not $Token -or -not $Port){
    $config=Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'VoiceHook/settings.json') -Raw | ConvertFrom-Json
    if(-not $Token){$Token=$config.udpToken}
    if(-not $Port){$Port=$config.udpPort}
}
$udp=[Net.Sockets.UdpClient]::new()
try {
    $udp.Client.ReceiveTimeout=3000
    $udp.Connect('127.0.0.1',$Port)
    $json=@{action=$Action;recordingId=$RecordingId;token=$Token} | ConvertTo-Json -Compress
    $bytes=[Text.Encoding]::UTF8.GetBytes($json)
    [void]$udp.Send($bytes,$bytes.Length)
    $remote=[Net.IPEndPoint]::new([Net.IPAddress]::Loopback,0)
    [Text.Encoding]::UTF8.GetString($udp.Receive([ref]$remote))
} finally {$udp.Dispose()}
