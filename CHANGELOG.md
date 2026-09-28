# Changelog

## Unreleased

- Shorten the repository README around features, setup, building and data boundaries; retain protocol/validation references and separate possible future work.

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
