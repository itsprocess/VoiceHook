# VoiceHook

A small, independent Windows tray utility: **push to talk → transcript → webhook**, and **incoming text → speech**.

VoiceHook owns microphone capture and transcription. The destination receives a normal HTTP event. VoiceHook is a standalone utility. Any webhook receiver can authenticate the hook, consume its transcript payload, and decide what happens next.

## Run

Double-click **Start VoiceHook.bat** in this repository. The built Windows x64 application is in `artifacts/VoiceHook-win-x64/VoiceHook.exe`; that entire folder can be copied elsewhere. Published builds include .NET, so running them does not require the SDK. Windows 10/11, microphone permission for desktop apps, and a microphone are required.

1. Open **Settings**, select your microphone and transcription provider, then save.
2. Hold **F9** and speak; release to transcribe. You can change F1–F24 and require Ctrl/Alt/Shift, or disable keyboard capture. The selected shortcut is consumed while VoiceHook runs. The Capture tab also has a hold-to-talk button.
3. Read the transcript in Capture. To deliver it, set a **Webhook URL** and optional bearer token. Automatic delivery is enabled by default, but no destination is preconfigured. Turn it off to review text before using **Send last transcript**.
4. Closing the window keeps VoiceHook in the tray. **Exit** in the tray ends it and discards an unfinished recording. No automatic Windows startup registration is installed.

Only one recording/transcription can run at a time. The default 60-second recording limit ends capture and starts transcription; it is configurable from 1–300 seconds. Cancel discards capture or cancels transcription. An already queued webhook cannot be recalled.

## Transcription

- **Windows (local):** uses the installed Windows SAPI dictation recognizer; no API key or audio upload. The default language is `en-US`. Install the matching speech-recognition language in Windows if absent. This is classic Windows recognition, not Windows cloud voice typing; accuracy varies and can be noticeably poorer than a modern transcription model.
- **OpenAI-compatible API:** configure the full transcription endpoint, model, language and key. Defaults are `https://api.openai.com/v1/audio/transcriptions` and `gpt-4o-mini-transcribe`. Your own OpenAI key is required for that endpoint; API usage is billed to your account. Other services must support multipart audio transcriptions and a JSON `text` result. Local compatible services may use loopback HTTP; remote endpoints require HTTPS.

The provider receives a 16 kHz, mono, PCM WAV only after release/limit. VoiceHook performs no automatic transcription retries. See the [OpenAI speech-to-text contract](https://developers.openai.com/api/docs/guides/speech-to-text) for supported models and service behavior.

## Stream Deck — direct

Double-click **Install Stream Deck Plugin.bat**, then add **VoiceHook → Push to talk** to a key in Stream Deck. Keep VoiceHook running; hold the key to record and release to transcribe. No simulated keyboard shortcut or UDP configuration is needed. The key displays a warning if VoiceHook is unavailable or busy. Requires Stream Deck 6.6+ on Windows.

The plugin uses a current-user Windows named pipe. Changing profiles while a key is held cancels that recording when Stream Deck sends `willDisappear`. An abrupt plugin or Stream Deck crash may lose key-up; the recording limit still stops capture. See [the plugin protocol](docs/PROTOCOL.md).

## UDP

Enable UDP in Settings. It listens only on `127.0.0.1` (default port `17654`) and requires the random token shown there. It is for local controls, not a network microphone service.

```powershell
$id=[guid]::NewGuid().ToString('N')
.\scripts\Send-Udp.ps1 -Action start -RecordingId $id
# Speak, then:
.\scripts\Send-Udp.ps1 -Action stop -RecordingId $id
```

The helper reads your port/token from local settings. `cancel` discards the matching recording; `status` returns current state. Repeat lost packets with the **same** ID, and use a fresh ID for a new utterance. UDP has no guaranteed delivery. [Payloads and semantics](docs/PROTOCOL.md) describe duplicate/reordered packet handling.

## Data and delivery

Settings live in `%LOCALAPPDATA%\VoiceHook\settings.json`. API keys and webhook bearer tokens use Windows DPAPI for your account. The local UDP control token is in this settings file; it grants capture control, not access to configured API keys. Keep the folder private to your Windows account.

Audio is held in memory and is not written to an audio file. Unsent transcripts are saved in the local `outbox` folder with their destination and encrypted token, so pending delivery survives restart. A successful HTTP 2xx removes the pending transcript and keeps a receipt including the text under `outbox/sent` for Messages history. Earlier receipts without text cannot reconstruct old outgoing messages. No audio archive is created.

Webhook delivery is **at least once**: five bounded attempts, then held for **Retry pending**. Retries retain the payload `id` and `Idempotency-Key` header. Receivers must deduplicate that ID: a connection can fail after the receiver has already accepted a message. Clicking Send again does not requeue a successfully delivered transcript. Queued destinations are fixed at enqueue time; changing settings does not reroute old messages. No HTTP redirects are followed.

## Develop and verify

Build prerequisites: .NET 8 SDK (or compatible newer SDK), Node.js 24+, npm, Windows. The main app targets .NET 8; Stream Deck provides its own Node runtime for the plugin.

```powershell
.\scripts\Test.ps1
.\scripts\Build.ps1
```

Tests use isolated temporary folders and a generated speech fixture; they never capture the live microphone or call a paid service. Close VoiceHook before transport integration tests because they use its current-user pipe. Native tests cover capture ownership, cancellation, recording limits, duplicate/reordered controls, API serialization, durable delivery and real Windows recognition. Plugin tests cover key ordering, disconnect handling, named-pipe framing and the full SDK-event → native-host → Windows-transcript → HTTP path.

See [architecture](docs/ARCHITECTURE.md), [protocol](docs/PROTOCOL.md), and [validation/remaining checks](docs/VALIDATION.md).

## Speech output

VoiceHook also receives text from any authenticated local sender and speaks it. Open **Speech output**, enable incoming text, choose Windows (local) or an OpenAI-compatible provider, then save. Copy the incoming URL and access token to the sender. The default endpoint is `http://127.0.0.1:17655/speech`.

Windows uses an installed voice without a service. OpenAI defaults to `gpt-4o-mini-tts`, voice `coral`, and WAV output. Supply a speech API key or explicitly choose **Use the transcription API key**. Service mode sends the response text to that endpoint and plays AI-generated speech. [OpenAI's TTS guide](https://developers.openai.com/api/docs/guides/text-to-speech) describes voices and usage.

**Stop speech** is available in Capture and the tray. Any PTT source interrupts the current reply; subsequent queued replies wait until capture/transcription finishes. Test saved voice speaks a short test phrase. Closing to tray keeps both directions available; Exit stops listeners and playback.

POST JSON `{ "id": "unique-delivery-id", "text": "Text to speak" }` with `Authorization: Bearer <access token>`. A `202` response confirms durable acceptance, not playback completion. Read `/speech/<id>` with the same token for `queued`, `speaking`, `completed`, `interrupted` or `failed`. `/health` is authenticated and does not play audio. Reuse the same ID/text for retries: it never queues twice. Conflicting text returns 409; a full 32-message queue returns 429. Text is limited to 16,000 characters. Listeners bind only to loopback and require no administrator registration.

Speech receipts and incoming text persist under `%LOCALAPPDATA%\VoiceHook\speech\speech.json`, including finished receipts for deduplication. Keep this folder private. Pending work resumes on restart; an utterance that was in progress becomes interrupted and is not replayed. Receipts remain until you explicitly clear that file with VoiceHook closed; clearing it also clears duplicate protection. Playback failures are visible locally and are not automatically retried. Markdown decoration and code blocks are simplified for speech; the original received text is retained in the receipt.

## Messages

The **Messages** tab lists the latest 200 incoming replies and outgoing messages with delivery/playback status. Select a row to read the full text and any error. Type below and click **Send** or press **Ctrl+Enter** to use your configured webhook without recording audio. Manual sends work even when automatic transcript sending is off. Failed sends remain in the existing outbox; **Retry pending sends** preserves their original IDs.

Typed messages use the same `transcript.completed` envelope with `source: "text"`, `provider: "manual"`, and zero recording duration. A receiver must accept those values. Incoming replies remain readable if speech fails or is interrupted; Messages is a transport history, not a model conversation/session manager.
