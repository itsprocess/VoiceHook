# Architecture

VoiceHook is an external producer. It has no dependency on a particular receiver or automation platform. It does not decide what a transcript means. Any authenticated webhook destination can consume its event.

```
Keyboard / Stream Deck / authenticated local UDP / capture button
                         ↓
                  one capture owner
                         ↓
                    in-memory WAV
                         ↓
             Windows SAPI or compatible API
                         ↓
                  durable text outbox
                         ↓
                 configured HTTP hook
```

`Engine` owns the bounded idle → recording → processing → idle state machine. A settings snapshot is taken at capture start. Input transports do not own audio or provider credentials. `Microphone` is the audio-device boundary; `Transcriber` is the recognizer/provider boundary. `Outbox` is the delivery boundary and owns retry/idempotency behavior. WinForms owns settings and tray lifetime. A one-second timer services only pending external deliveries; it is not an automation scheduler.

The Stream Deck plugin contains only control transport and key feedback. It lives in this repository because it is a VoiceHook controller. It connects over current-user named pipes; no LAN listener is needed. UDP is an optional loopback control endpoint with its own token.

No administrator rights, service registration, registry startup entry, continuous listening, wake word, intent catalog, or action execution is installed. Press starts recording; release ends it. Both provider and destination are user configuration. Adding another speech service means implementing the provider boundary, not altering downstream programs.

Possible later work: richer provider protocols, quality/latency tuning, configurable webhook headers/envelopes, Windows startup preference, device reconnect feedback, installer/signing. These are possible utility enhancements.

## Incoming speech

`SpeechServer` uses Kestrel bound to 127.0.0.1 with bearer authentication, request-size limits and no request logging. `SpeechQueue` durably accepts a delivery ID/text before returning its receipt. It serializes `ISpeechPlayer` calls, snapshots provider settings for each utterance, holds pending work during PTT, and cancels active playback on interruption/exit. A stored `speaking` receipt becomes `interrupted` after a restart, because audible effects cannot be rolled back. Finished receipts remain available for deduplication and reconciliation.

`SpeechPlayer` owns Windows synthesis or HTTPS speech generation followed by local WAV playback. Requests and audio responses are bounded; longer text is chunked sequentially. It never interprets received text as SSML. The incoming speech service has no dependency on the outbound transcription destination or on any automation platform.
