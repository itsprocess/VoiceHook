# VoiceHook boundaries

## Capture controls

UTF-8 JSON:

```json
{"action":"start","recordingId":"unique-per-utterance","token":"local-udp-token"}
```

Actions: `start`, `stop`, `cancel`, `status`. Start/stop/cancel require a 1–100 character recording ID. Status needs no ID. Replies contain `ok`, `state` (`idle`, `recording`, `processing`) and `message`.

- UDP: one object per datagram, at most 4 KiB, to `127.0.0.1:<configured-port>`. Token is mandatory, including for status. Invalid tokens/malformed packets are ignored. Valid requests receive a reply to the sending address/port.
- Stream Deck: current-user-only `\\.\pipe\VoiceHook-<Windows username>`. One request and reply per connection, each JSON object terminated by newline. No token: Windows pipe ACL supplies the user boundary. The plugin receives actual `keyDown`/`keyUp` events from Stream Deck and does not emulate a shortcut.
- Keyboard: a local low-level keyboard hook, physical F1–F24 with optional required modifiers. Key repeat is suppressed; key-up ends the matching recording even if a modifier was released first. Injected keystrokes are ignored. Hook processing posts commands to the UI thread instead of opening an audio device inside the hook callback.

Each transport owns its recording. A UDP stop cannot stop a keyboard capture. Duplicate starts while active do not reopen the microphone. Finished/cancelled IDs are remembered for the last 2,048 controls in this process; late starts with those IDs are rejected. A stop arriving before its start also closes that ID. This cache is not a durable replay ledger; always use fresh IDs across restarts.

Busy starts are rejected. No automatic queue of recordings is created. Stop is idempotent. Cancellation may race delivery; once enqueued, a transcript is not recalled. If a release packet is lost, retry it with the same ID; the duration limit is the final stop mechanism. Stream Deck serializes a key's start/release commands and cancels held keys on orderly disconnect.

## Transcript hook

HTTP POST, `Content-Type: application/json`, optional `Authorization: Bearer <configured-token>`, and `Idempotency-Key: <id>`:

```json
{
  "id": "09a5aa0d20b54e76a9227f6a2bc5c844",
  "event": "transcript.completed",
  "text": "Set the living room lights to blue.",
  "source": "streamdeck",
  "provider": "windows",
  "recordedAt": "2026-09-27T12:00:00.0000000+00:00",
  "durationSeconds": 3.2
}
```

`source`: `keyboard`, `button`, `streamdeck`, or `udp`. `provider`: `windows` or `openai` (the latter names the compatible API protocol, including other configured services). `recordedAt` is capture start in UTC. Duration is elapsed capture time. This event contains text and recording metadata; no audio and no downstream intent/action interpretation.

All 2xx statuses count as accepted. Connection failure, timeout, non-2xx or redirects leave the same item pending. Attempts are persisted before sending, with delays of 2, 4, 8 and 16 seconds between the five attempts. Manual retry resets the budget; it preserves the message ID. Delivery receipts suppress manual re-enqueue after success. A crash after remote acceptance and before receipt persistence can cause a repeat; the receiver must enforce idempotency.

## Transcription service

POST to the exact configured URL. Multipart fields: `file` (PCM WAV, filename `recording.wav`), `model`, `response_format=json`, and optional ISO language derived from the configured locale (`en-US` → `en`). Optional bearer key. Response: `{ "text": "..." }`. Response size is capped at 1 MiB; transcript length at 100,000 characters. Deadline: 120 seconds. No transcription retry. Silence creates no webhook.

The plugin implements the [official Stream Deck WebSocket protocol](https://docs.elgato.com/streamdeck/sdk/references/websocket/plugin/). Its [manifest](https://docs.elgato.com/streamdeck/sdk/references/manifest/) targets SDK 2 / Node 20, bundled with no separately installed Node requirement for end users.

## Incoming speech HTTP (voicehook.speech/1)

All routes require the configured bearer token. Only 127.0.0.1 is bound (default port 17655).

- `GET /health`: `{healthy:true,protocol:"voicehook.speech/1"}`.
- `POST /speech`: `{id,text}`. ID: 1–128 ASCII letters/digits/underscore/hyphen; text: nonblank, at most 16,000 characters. Returns HTTP 202 and `{id,text,state,at,error?}` after durable persistence. Same ID and exact text returns the existing receipt, even if playback already completed or failed. Conflicting content: 409. Queue of 32 waiting/active items full: 429. Invalid input: 400; oversized HTTP body: 413; unavailable storage: 503.
- `GET /speech/{id}`: receipt or 404. States: queued, speaking, completed, interrupted, failed. A receipt is proof of acceptance, never a request to repeat failed playback.

Authentication failures return 401. No CORS policy is enabled. No redirects are followed when generating service speech. No HTTP endpoint reads or changes provider credentials.
