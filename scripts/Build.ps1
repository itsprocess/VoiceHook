param([switch]$SkipStreamDeck)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & "$PSScriptRoot/Make-Icons.ps1"
    dotnet publish src/VoiceHook/VoiceHook.csproj -c Release -r win-x64 --self-contained true -o artifacts/VoiceHook-win-x64
    if($LASTEXITCODE -ne 0){throw 'VoiceHook publish failed'}
    Copy-Item -LiteralPath README.md -Destination artifacts/VoiceHook-win-x64/README.md
    if(-not $SkipStreamDeck){
        Push-Location streamdeck
        try {
            npm ci
            if($LASTEXITCODE -ne 0){throw 'Plugin dependency installation failed'}
            npm run build
            if($LASTEXITCODE -ne 0){throw 'Plugin build failed'}
            npm run validate
            if($LASTEXITCODE -ne 0){throw 'Plugin validation failed'}
            npx streamdeck pack com.voicehook.ptt.sdPlugin --output ../artifacts --force --no-file-list
            if($LASTEXITCODE -ne 0){throw 'Plugin packaging failed'}
        } finally {Pop-Location}
    }
    Write-Host "Ready: $repo\artifacts\VoiceHook-win-x64\VoiceHook.exe"
} finally {Pop-Location}
