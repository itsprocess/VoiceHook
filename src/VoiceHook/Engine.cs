namespace VoiceHook;

public sealed record Control(string Action, string RecordingId = "", string Token = "");

public sealed record Reply(bool Ok, string State, string Message);

public sealed class Engine(
    Func<Settings> settings,
    Func<ICapture> capture,
    ITranscriber transcriber,
    Outbox outbox
) : IDisposable
{
    readonly object gate = new();
    readonly Queue<string> recent = new();
    readonly HashSet<string> seen = new();
    ICapture? mic;
    CancellationTokenSource? cancellation;
    string? owner;
    Settings? snapshot;
    DateTimeOffset began;
    string eventId = "";
    public string State { get; private set; } = "idle";
    public Transcript? Last { get; private set; }
    public Task Work { get; private set; } = Task.CompletedTask;
    public event Action? Changed;
    public string Message { get; private set; } = "Ready. Hold your PTT control to record.";

    void Notify(string message)
    {
        Message = message;
        Changed?.Invoke();
    }

    void Remember(string key)
    {
        if (seen.Add(key))
            recent.Enqueue(key);
        while (recent.Count > 2048)
            seen.Remove(recent.Dequeue());
    }

    public Reply Command(Control c, string source)
    {
        lock (gate)
        {
            if (c.Action == "status")
                return new(true, State, Message);
            if (c.Action is not ("start" or "stop" or "cancel"))
                return new(false, State, "Unknown action.");
            if (
                string.IsNullOrEmpty(c.RecordingId)
                || c.RecordingId.Length > 100
                || source.Length > 150
            )
                return new(false, State, "A recordingId of 1–100 characters is required.");
            var key = source + ":" + c.RecordingId;
            if (c.Action == "start")
            {
                if (owner == key && State == "recording")
                    return new(true, State, "Already recording.");
                if (seen.Contains(key))
                    return new(false, State, "Recording ID already finished or cancelled.");
                if (State != "idle")
                    return new(false, State, "Another recording or transcription is active.");
                snapshot = settings().Validate();
                cancellation = new();
                owner = key;
                eventId = Guid.NewGuid().ToString("N");
                began = DateTimeOffset.UtcNow;
                try
                {
                    mic = capture();
                    mic.Start(snapshot.Device);
                    State = "recording";
                    Notify("Recording — release to transcribe.");
                    var token = cancellation.Token;
                    var limit = snapshot.MaxSeconds;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(limit), token);
                            Command(new("stop", c.RecordingId), source);
                        }
                        catch (OperationCanceledException) { }
                    });
                    return new(true, State, "Recording.");
                }
                catch
                {
                    mic?.Dispose();
                    mic = null;
                    cancellation.Dispose();
                    cancellation = null;
                    owner = null;
                    Notify(
                        "Cannot open the selected microphone. Check device and Windows microphone permissions."
                    );
                    return new(false, State, Message);
                }
            }
            if (owner != key)
            {
                Remember(key);
                return new(
                    true,
                    State,
                    "No matching active recording; late start with this ID is ignored."
                );
            }
            if (c.Action == "cancel")
            {
                cancellation?.Cancel();
                Remember(key);
                if (State == "recording")
                {
                    State = "processing";
                    Work = Finish(true);
                }
                else
                    Notify("Cancelling transcription.");
                return new(true, State, "Cancelled. An already queued webhook cannot be recalled.");
            }
            if (State != "recording")
                return new(true, State, "Already stopped.");
            State = "processing";
            Remember(key);
            Notify("Transcribing…");
            Work = Finish(false);
            return new(true, State, "Transcribing.");
        }
    }

    async Task Finish(bool discard)
    {
        var device = mic!;
        var s = snapshot!;
        var cts = cancellation!;
        var duration = (DateTimeOffset.UtcNow - began).TotalSeconds;
        try
        {
            var wav = await device.Stop();
            device.Dispose();
            mic = null;
            if (discard)
            {
                Notify("Recording discarded.");
                return;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(125));
            var text = await transcriber.Transcribe(wav, s, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text))
            {
                Notify("No speech recognized. Nothing sent.");
                return;
            }
            if (text.Length > 100_000)
                throw new InvalidOperationException("Transcript exceeds 100,000 characters.");
            lock (gate)
            {
                cts.Token.ThrowIfCancellationRequested();
                Last = new(
                    eventId,
                    "transcript.completed",
                    text,
                    owner!.Split(':')[0],
                    s.Provider,
                    began.ToString("O"),
                    duration
                );
                if (s.AutoSend && s.Webhook.Length > 0)
                {
                    outbox.Enqueue(Last, s);
                    Notify("Transcript ready; webhook queued.");
                }
                else
                    Notify("Transcript ready. Configure a webhook or send it manually.");
            }
        }
        catch (OperationCanceledException)
        {
            Notify("Recording cancelled or transcription timed out. Nothing sent.");
        }
        catch (Exception e)
        {
            Notify(
                e is InvalidOperationException
                    ? e.Message
                    : "Transcription failed. Check the provider and language settings."
            );
        }
        finally
        {
            lock (gate)
            {
                device.Dispose();
                mic = null;
                cts.Cancel();
                cts.Dispose();
                cancellation = null;
                if (owner != null)
                    Remember(owner);
                owner = null;
                State = "idle";
                Changed?.Invoke();
            }
        }
    }

    public void SendLast()
    {
        lock (gate)
        {
            if (State != "idle" || Last == null)
                throw new InvalidOperationException("No finished transcript.");
            outbox.Enqueue(Last, settings());
        }
    }

    public void CancelActive()
    {
        lock (gate)
        {
            if (owner == null)
                return;
            var split = owner.IndexOf(':');
            Command(new("cancel", owner[(split + 1)..]), owner[..split]);
        }
    }

    public void Dispose()
    {
        CancelActive();
    }
}
