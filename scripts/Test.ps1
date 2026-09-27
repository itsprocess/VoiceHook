$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet run --project tests/VoiceHook.Tests
    if($LASTEXITCODE -ne 0){throw 'Native tests failed'}
    dotnet run --no-build --project tests/VoiceHook.Tests -- --startup
    if($LASTEXITCODE -ne 0){throw 'Tray application startup test failed'}
    Push-Location streamdeck
    try {
        npm ci
        if($LASTEXITCODE -ne 0){throw 'Plugin dependency installation failed'}
        npm run build
        if($LASTEXITCODE -ne 0){throw 'Plugin build failed'}
        npm test
        if($LASTEXITCODE -ne 0){throw 'Plugin tests failed'}
        npm run validate
        if($LASTEXITCODE -ne 0){throw 'Plugin manifest validation failed'}
    } finally {Pop-Location}
} finally {Pop-Location}
