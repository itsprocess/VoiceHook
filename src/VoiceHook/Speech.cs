using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace VoiceHook;

public interface ISpeechPlayer
{
    Task Speak(string text, Settings settings, CancellationToken token);
}

public static class SpokenText
{
    // Render common Markdown as plain speech, never as SSML or executable content.
    public static string FromMarkdown(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?```|~~~[\s\S]*?~~~", " Code block omitted. ");
        text = Regex.Replace(text, @"!?\[([^\]]*)\]\([^\r\n)]*\)", "$1");
        text = Regex.Replace(text, @"(?m)^\s*(?:#{1,6}\s+|>\s*|[-*+]\s+)", "");
        text = Regex.Replace(text, @"(?m)^\s*\|?[ :|-]+\|[ :|-]*$", "");
        text = text.Replace("**", "").Replace("__", "").Replace("`", "").Replace("|", ", ");
        text = Regex.Replace(text, @"(?<!\w)[*_](.+?)[*_](?!\w)", "$1");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static IEnumerable<string> Chunks(string text, int limit = 2500)
    {
        while (text.Length > limit)
        {
            var end = text.LastIndexOf(' ', limit, limit);
            if (end < limit / 2) end = limit;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            yield return text[..end];
            text = text[end..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}

public sealed class SpeechPlayer(HttpClient http) : ISpeechPlayer
{
    public async Task Speak(string text, Settings settings, CancellationToken token)
    {
        foreach (var chunk in SpokenText.Chunks(SpokenText.FromMarkdown(text)))
        {
            token.ThrowIfCancellationRequested();
            if (settings.SpeechProvider == "windows") await SpeakWindows(chunk, settings, token);
            else
            {
                var audio = await Synthesize(chunk, settings, token);
                using var stream = new MemoryStream(audio);
                using var reader = new WaveFileReader(stream);
                using var player = new WaveOutEvent();
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.PlaybackStopped += (_, e) => { if (e.Exception != null) done.TrySetException(e.Exception); else done.TrySetResult(); };
                player.Init(reader);
                token.ThrowIfCancellationRequested();
                player.Play();
                using var cancel = token.Register(player.Stop);
                await done.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested();
            }
        }
    }

    public async Task<byte[]> Synthesize(string text, Settings settings, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var key = Storage.Reveal(settings.SpeechUseTranscriptionKey ? settings.ApiKey : settings.SpeechApiKey);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Configure a speech API key in Speech output settings.");
        using var request = new HttpRequestMessage(HttpMethod.Post, Settings.CheckUrl(settings.SpeechEndpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new { model = settings.SpeechModel, input = text, voice = settings.SpeechVoice, response_format = "wav" });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Speech service returned HTTP {(int)response.StatusCode}. Check speech settings.");
        return await Net.ReadBounded(response.Content, 20_000_000, deadline.Token);
    }

    static async Task SpeakWindows(string text, Settings settings, CancellationToken token)
    {
        using var synth = new SpeechSynthesizer();
        if (settings.WindowsVoice.Length > 0) synth.SelectVoice(settings.WindowsVoice);
        synth.SetOutputToDefaultAudioDevice();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        synth.SpeakCompleted += (_, e) => { if (e.Error != null) done.TrySetException(e.Error); else done.TrySetResult(); };
        token.ThrowIfCancellationRequested();
        synth.SpeakAsync(text);
        using var cancel = token.Register(synth.SpeakAsyncCancelAll);
        await done.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
    }
}

public sealed record SpeechReceipt(string Id, string Text, string State, DateTimeOffset At, string? Error = null);
public sealed class SpeechConflictException : Exception;
public sealed class SpeechQueueFullException : Exception;

public sealed class SpeechQueue : IAsyncDisposable
{
    readonly object gate = new();
    readonly string file;
    readonly Func<Settings> settings;
    readonly ISpeechPlayer player;
    readonly List<SpeechReceipt> receipts;
    readonly SemaphoreSlim wake = new(0, 1);
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? active;
    bool paused, disposed;
    readonly Task loop;
    public event Action? Changed;
    public string Status { get; private set; } = "Speech ready.";
    public int Pending { get { lock (gate) return receipts.Count(r => r.State == "queued"); } }

    public SpeechQueue(string directory, Func<Settings> settings, ISpeechPlayer player)
    {
        this.settings = settings;
        this.player = player;
        file = Path.Combine(directory, "speech.json");
        receipts = File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<List<SpeechReceipt>>(File.ReadAllText(file), Storage.Json)! : [];
        // Playback cannot be rolled back. Never replay an uncertain, interrupted utterance on restart.
        for (var i = 0; i < receipts.Count; i++)
            if (receipts[i].State == "speaking") receipts[i] = receipts[i] with { State = "interrupted", Error = "Application stopped during playback." };
        if (File.Exists(file)) Save();
        loop = Task.Run(Run);
        Signal();
    }

    void Signal() { if (wake.CurrentCount == 0) wake.Release(); }
    void Save() => Storage.Write(file, receipts);
    public SpeechReceipt? Find(string id) { lock (gate) return receipts.Find(r => r.Id == id); }

    public SpeechReceipt Accept(string id, string text)
    {
        if (!Regex.IsMatch(id ?? "", @"^[A-Za-z0-9_-]{1,128}$") || string.IsNullOrWhiteSpace(text) || text.Length > 16_000)
            throw new ArgumentException("Use an ID of 1–128 letters, digits, underscores or hyphens, and text of 1–16,000 characters.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var existing = receipts.Find(r => r.Id == id);
            if (existing != null)
            {
                if (existing.Text != text) throw new SpeechConflictException();
                return existing;
            }
            if (receipts.Count(r => r.State is "queued" or "speaking") >= 32) throw new SpeechQueueFullException();
            var receipt = new SpeechReceipt(id!, text, "queued", DateTimeOffset.UtcNow);
            receipts.Add(receipt);
            try { Save(); } catch { receipts.Remove(receipt); throw; }
            Signal();
            Changed?.Invoke();
            return receipt;
        }
    }

    public void Pause(bool value)
    {
        lock (gate)
        {
            if (disposed) return;
            paused = value;
            if (value) active?.Cancel();
            else Signal();
        }
    }

    public void Stop() { lock (gate) active?.Cancel(); }
    public void SettingsChanged() { lock (gate) { if (!settings().SpeechEnabled) active?.Cancel(); Signal(); } }

    async Task Run()
    {
        try
        {
            while (true)
            {
                await wake.WaitAsync(lifetime.Token);
                while (true)
                {
                    SpeechReceipt? item;
                    Settings snapshot;
                    CancellationTokenSource cts;
                    lock (gate)
                    {
                        if (disposed || paused || !settings().SpeechEnabled) break;
                        item = receipts.Find(r => r.State == "queued");
                        if (item == null) break;
                        snapshot = settings();
                        item = item with { State = "speaking" };
                        receipts[receipts.FindIndex(r => r.Id == item.Id)] = item;
                        Save(); // Durable before any audible side effect.
                        active = cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        cts.CancelAfter(TimeSpan.FromMinutes(10));
                        Status = "Speaking: " + SpokenText.FromMarkdown(item.Text)[..Math.Min(120, SpokenText.FromMarkdown(item.Text).Length)];
                        Changed?.Invoke();
                    }
                    string state = "completed", error = "";
                    try { await player.Speak(item.Text, snapshot, cts.Token); cts.Token.ThrowIfCancellationRequested(); }
                    catch (OperationCanceledException) { state = "interrupted"; }
                    catch (Exception e) { state = "failed"; error = e is InvalidOperationException ? e.Message : "Speech failed. Check the voice, audio device and connection settings."; }
                    lock (gate)
                    {
                        active = null;
                        cts.Dispose();
                        var index = receipts.FindIndex(r => r.Id == item.Id);
                        receipts[index] = item with { State = state, Error = error.Length > 0 ? error : null };
                        Save();
                        Status = error.Length > 0 ? error : "Speech " + state + ".";
                        Changed?.Invoke();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { lock (gate) { disposed = true; Status = "Speech queue stopped: cannot persist receipts. Check the data folder and restart VoiceHook."; Changed?.Invoke(); } }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { disposed = true; lifetime.Cancel(); active?.Cancel(); }
        await loop;
        lifetime.Dispose();
        wake.Dispose();
    }
}
