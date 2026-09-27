using System.Globalization;
using System.Net.Http.Headers;
using System.Speech.Recognition;
using System.Text.Json;
using NAudio.Utils;
using NAudio.Wave;

namespace VoiceHook;

public interface ICapture : IDisposable
{
    void Start(int device);
    Task<byte[]> Stop();
}

public sealed class Microphone : ICapture
{
    WaveInEvent? input;
    MemoryStream? memory;
    WaveFileWriter? writer;
    TaskCompletionSource<byte[]>? stopped;

    public void Start(int device)
    {
        memory = new MemoryStream();
        input = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
        };
        writer = new WaveFileWriter(new IgnoreDisposeStream(memory), input.WaveFormat);
        stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        input.DataAvailable += (_, e) =>
        {
            if (memory.Length < 10_000_000)
                writer.Write(e.Buffer, 0, e.BytesRecorded);
        };
        input.RecordingStopped += (_, e) =>
        {
            writer.Dispose();
            if (e.Exception != null)
                stopped.TrySetException(e.Exception);
            else
                stopped.TrySetResult(memory.ToArray());
        };
        input.StartRecording();
    }

    public async Task<byte[]> Stop()
    {
        if (input == null || stopped == null)
            throw new InvalidOperationException("Not recording");
        input.StopRecording();
        return await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        input?.Dispose();
        writer?.Dispose();
        memory?.Dispose();
    }
}

public interface ITranscriber
{
    Task<string> Transcribe(byte[] wav, Settings settings, CancellationToken token);
}

public sealed class Transcriber(HttpClient http) : ITranscriber
{
    public Task<string> Transcribe(byte[] wav, Settings s, CancellationToken token) =>
        s.Provider == "windows" ? Windows(wav, s.Language, token) : Remote(wav, s, token);

    static async Task<string> Windows(byte[] wav, string language, CancellationToken token)
    {
        using var engine = string.IsNullOrWhiteSpace(language)
            ? new SpeechRecognitionEngine()
            : new SpeechRecognitionEngine(CultureInfo.GetCultureInfo(language));
        using var stream = new MemoryStream(wav);
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToWaveStream(stream);
        var done = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var parts = new List<string>();
        engine.SpeechRecognized += (_, e) => parts.Add(e.Result.Text);
        engine.RecognizeCompleted += (_, e) =>
        {
            if (e.Error != null)
                done.TrySetException(e.Error);
            else if (e.Cancelled)
                done.TrySetCanceled();
            else
                done.TrySetResult(string.Join(" ", parts));
        };
        engine.RecognizeAsync(RecognizeMode.Multiple);
        using var cancel = token.Register(() => engine.RecognizeAsyncCancel());
        try
        {
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(120), token);
        }
        finally
        {
            engine.RecognizeAsyncCancel();
        }
    }

    async Task<string> Remote(byte[] wav, Settings s, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new("audio/wav");
        form.Add(file, "file", "recording.wav");
        form.Add(new StringContent(s.Model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(s.Language))
            form.Add(new StringContent(s.Language.Split('-')[0]), "language");
        using var req = new HttpRequestMessage(HttpMethod.Post, Settings.CheckUrl(s.Endpoint))
        {
            Content = form,
        };
        var key = Storage.Reveal(s.ApiKey);
        if (key.Length > 0)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var res = await http.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token
        );
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Transcription service returned HTTP {(int)res.StatusCode}. No automatic transcription retry."
            );
        var bytes = await Net.ReadBounded(res.Content, 1_000_000, timeout.Token);
        using var json = JsonDocument.Parse(bytes);
        if (
            !json.RootElement.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
        )
            throw new InvalidOperationException(
                "Transcription service did not return a text field."
            );
        return text.GetString()!;
    }
}

public static class Net
{
    public static HttpClient Client() =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static async Task<byte[]> ReadBounded(
        HttpContent content,
        int limit,
        CancellationToken token
    )
    {
        using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > limit)
                throw new InvalidOperationException("Service response is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}
