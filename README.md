# VoiceHook

VoiceHook is a standalone Windows tray utility for **push-to-talk → transcript → webhook** and **incoming text → speech**. It connects a microphone, keyboard or Stream Deck to any compatible HTTP receiver.

## Features

- Keyboard push-to-talk, an on-screen capture button, direct Stream Deck control and authenticated loopback UDP.
- Windows dictation or a configurable OpenAI-compatible transcription service.
- Incoming text playback through Windows voices or OpenAI-compatible speech generation.
- A Messages tab showing incoming replies and outgoing text, delivery/playback status, and a typed-message composer.
- Durable outgoing delivery and incoming speech receipts with stable IDs and duplicate protection.

## Run

Use **Start VoiceHook.bat** after building, or run **VoiceHook.exe** from a published Windows x64 folder. Published builds include .NET; copy the entire folder when moving the application. Windows 10/11, desktop microphone permission and a microphone are needed for capture.

Open Settings, choose the microphone and transcription provider, and configure your webhook. Hold **F9** to record and release to transcribe; the shortcut is configurable. Closing the window keeps the tray running; **Exit** stops it. No destination, API account or Windows startup registration is preconfigured.

Use **Messages** to read replies or type and send with Ctrl+Enter. **Speech output** configures the incoming listener, voice and provider. Windows providers stay local; configured external providers receive the audio or text they process. Service credentials and usage belong to your account.

For Stream Deck, run **Install Stream Deck Plugin.bat** and add **VoiceHook → Push to talk**. The direct plugin requires Stream Deck 6.6+ and uses a current-user named pipe. UDP is an optional loopback control transport.

## Build and test

Requires Windows, the .NET 8 SDK, Node.js and npm for the Stream Deck plugin.

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
```

Build output is `artifacts/VoiceHook-win-x64/`; `-SkipStreamDeck` builds only the native app. Exit VoiceHook before rebuilding into its running application folder. Tests use deterministic HTTP fixtures and local Windows speech facilities, not live microphone capture or paid provider calls. Optional live-speech diagnostics are explicitly separate.

## Local data

Settings, outbox and speech receipts live under `%LOCALAPPDATA%\VoiceHook`. API keys and webhook bearer tokens use Windows account encryption. Audio is held in memory; transcripts and incoming text persist for delivery/history. Keep this folder private.

Incoming speech is accepted durably, played sequentially and deduplicated by ID. PTT interrupts playback. Failed or interrupted speech is not automatically replayed. Pending outgoing webhook deliveries retain their original IDs. Editing or deleting receipts changes duplicate protection; see the protocol before manipulating runtime files.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Control, webhook and speech protocols](docs/PROTOCOL.md)
- [Validation evidence](docs/VALIDATION.md)
- [Changelog](CHANGELOG.md) and [future work](FUTURE.md)

VoiceHook is a transport and speech utility, not an AI conversation or workflow engine.
