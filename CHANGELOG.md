# Changelog

## Unreleased


- Shorten the repository README around features, setup, building and data boundaries; retain protocol/validation references and separate possible future work.

## 0.3.2 — 2026-09-28

- Launching VoiceHook again opens the existing tray application's window through current-user IPC instead of showing an already-running warning.

## 0.3.1 — 2026-09-28

- Replace Messages with Speak: typed text goes directly to TTS, never to the transcript webhook. Capture retains transcript delivery and retry controls.
- Show synthesis/playback phases, queue and first-audio timing, completion timing, and explicit interruption/timeout reasons. Keep the existing speech HTTP states compatible with receiving adaptors.
- Retry temporary webhook outages indefinitely with backoff capped at one minute and stable delivery IDs; hold permanent request/authentication failures for correction and manual retry.

## 0.3.0 — 2026-09-27

- Add Messages: incoming/outgoing text, status/error details, and typed webhook sends with Ctrl+Enter.
- Fix playback of OpenAI streaming WAV headers with unknown RIFF/data lengths. Preserve bounded decoding and validate malformed audio.
- Retain outgoing text in delivery receipts for restart-safe message history.


## 0.2.0 — 2026-09-27

- Add authenticated incoming speech, durable deduplication, serialized Windows/OpenAI playback, voice settings, Stop speech, and interruption from every PTT control.
- Describe the utility exclusively as a standalone transcription-to-webhook tool, independent of any receiving platform.

## 0.1.0 — 2026-09-27

- Independent Windows tray utility with keyboard, button, direct Stream Deck and authenticated loopback UDP push-to-talk.
- Windows local dictation or configurable OpenAI-compatible transcription.
- Webhook transcript delivery with persistent bounded retries, stable IDs and protected credentials.
- Self-contained Windows build, Stream Deck package, launchers, protocol documentation and automated tests.
