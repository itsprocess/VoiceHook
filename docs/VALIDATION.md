# Validation — 0.1.0

Validated on this Windows machine, 2026-09-27:

- Native .NET build and automated checks: settings/DPAPI, capture ownership, cancellation, recording limit, blank speech, API request/response contract, durable webhook retries and stable identity, real local pipe/UDP, installed Windows dictation recognizer.
- Stream Deck controller tests: repeated key-down/short taps, disconnect cancellation, unavailable-host feedback, real pipe framing.
- Integration: actual built plugin receives simulated SDK WebSocket events, controls the native engine over its named pipe, transcribes a synthesized WAV with the installed Windows recognizer, and POSTs to a real loopback HTTP receiver.
- Capture and Settings windows rendered and inspected. Plugin manifest validated by Elgato CLI. Dependencies locked; no known npm vulnerabilities reported during this pass.
- Real tray application startup, keyboard hook registration, local IPC and application exit checked without recording audio. The plugin is linked into this machine's Stream Deck installation; no existing key assignments or profiles were changed.

Windows recognition returned text, but did not accurately transcribe every word of the synthetic sentence. That test verifies integration, not dictation quality.

The owner subsequently confirmed successful hands-on use, including the physical Stream Deck, with the OpenAI-compatible provider configured. This is user-reported acceptance, separate from the automated tests above.

Automated tests do not record the live microphone, send audio to third parties, or post to live webhook receivers. OpenAI-compatible HTTP serialization is tested against a controlled mock; no paid API call was made by the test suite. A real downstream webhook destination still needs configuration and integration testing.

Acceptance: open VoiceHook, choose microphone, hold F9, speak, release, inspect transcript. Repeat with the Stream Deck action and UDP helper. Configure a destination and confirm exactly one accepted message per recording ID; briefly interrupt the receiver and confirm the same ID is retried. Choose a service provider if Windows recognition is insufficient for practical use.

## 0.2.0 speech validation

Deterministic checks cover durable acceptance, duplicate/conflicting IDs, queue limits, cancellation/hold, restart recovery, bearer authentication, invalid and oversized HTTP bodies, receipt lookup, Markdown speech cleanup, and OpenAI WAV request serialization. The Speech output screen was rendered and inspected. Native capture, transport, startup/exit and dictation tests continue to pass. OpenAI speech generation is optional and has not been exercised against a paid account by these tests.
