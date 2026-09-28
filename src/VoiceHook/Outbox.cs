using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoiceHook;

public sealed record Transcript(
    string Id,
    string Event,
    string Text,
    string Source,
    string Provider,
    string RecordedAt,
    double DurationSeconds
);

public sealed record Delivery(
    Transcript Payload,
    string Url,
    string Token,
    int Attempts = 0,
    string? LastError = null,
    DateTimeOffset? NextAttempt = null,
    bool Held = false
);

public sealed record TextMessage(string Id, string Text, DateTimeOffset At, string Direction, string Status);
public sealed record SentTranscript(string Id, DateTimeOffset AcceptedAt, Transcript? Payload = null);

public sealed class Outbox(string directory, HttpClient http, Func<DateTimeOffset>? clock = null)
{
    DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public TextMessage[] Recent()
    {
        var messages = new Dictionary<string, TextMessage>();
        if (!Directory.Exists(directory)) return [];
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                if (Path.GetFileName(Path.GetDirectoryName(file)) == "sent")
                {
                    var sent = JsonSerializer.Deserialize<SentTranscript>(File.ReadAllText(file), Storage.Json);
                    if (sent?.Payload is { } p) messages[p.Id] = new(p.Id, p.Text, DateTimeOffset.Parse(p.RecordedAt), "You", "Delivered");
                }
                else
                {
                    var d = Read(file);
                    if (File.Exists(Receipt(d.Payload.Id))) continue;
                    messages[d.Payload.Id] = new(d.Payload.Id, d.Payload.Text, DateTimeOffset.Parse(d.Payload.RecordedAt), "You", d.LastError ?? "Queued");
                }
            }
            catch (IOException) { } // Atomic replacement or delivery may finish during this read.
            catch (JsonException) { }
        }
        return messages.Values.OrderBy(m => m.At).TakeLast(200).ToArray();
    }
    readonly SemaphoreSlim gate = new(1);
    public event Action<string>? Changed;
    public int Count =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0;

    string Receipt(string id) => Path.Combine(directory, "sent", id + ".json");

    public void Enqueue(Transcript payload, Settings s)
    {
        Settings.CheckUrl(s.Webhook);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, payload.Id + ".json");
        if (File.Exists(file) || File.Exists(Receipt(payload.Id)))
        {
            Changed?.Invoke("This transcript is already queued or delivered.");
            return;
        }
        Storage.Write(file, new Delivery(payload, s.Webhook, s.WebhookToken));
        Changed?.Invoke("Transcript queued for webhook delivery.");
    }

    public async Task RetryAll()
    {
        await gate.WaitAsync();
        try
        {
            foreach (var f in Files())
            {
                var d = Read(f);
                Storage.Write(f, d with { Attempts = 0, NextAttempt = null, Held = false });
            }
        }
        finally
        {
            gate.Release();
        }
        await Flush();
    }

    IEnumerable<string> Files() =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];

    static Delivery Read(string f) =>
        JsonSerializer.Deserialize<Delivery>(File.ReadAllText(f), Storage.Json)!;

    public async Task Flush()
    {
        if (!await gate.WaitAsync(0))
            return;
        try
        {
            foreach (var f in Files())
            {
                Delivery d;
                try
                {
                    d = Read(f);
                }
                catch
                {
                    Changed?.Invoke("An outbox file is unreadable; inspect the data folder.");
                    continue;
                }
                if (File.Exists(Receipt(d.Payload.Id)))
                {
                    File.Delete(f);
                    continue;
                }
                if (d.Held || d.NextAttempt > Now)
                    continue;
                // Commit the attempt before sending. Restart never creates a new delivery identity.
                d = d with
                {
                    Attempts = Math.Min(1_000_000, d.Attempts + 1),
                    NextAttempt = Now.AddSeconds(
                        Math.Min(60, Math.Pow(2, Math.Min(6, d.Attempts + 1)))
                    ),
                };
                Storage.Write(f, d);
                try
                {
                    using var req = new HttpRequestMessage(
                        HttpMethod.Post,
                        Settings.CheckUrl(d.Url)
                    )
                    {
                        Content = JsonContent.Create(d.Payload, options: Storage.Json),
                    };
                    req.Headers.Add("Idempotency-Key", d.Payload.Id);
                    var token = Storage.Reveal(d.Token);
                    if (token.Length > 0)
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var res = await http.SendAsync(
                        req,
                        HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token
                    );
                    if (!res.IsSuccessStatusCode)
                    {
                        var status = (int)res.StatusCode;
                        d = d with { Held = status < 500 && status is not (408 or 429) };
                        throw new InvalidOperationException(
                            $"Webhook returned HTTP {status}."
                        );
                    }
                    Storage.Write(
                        Receipt(d.Payload.Id),
                        new SentTranscript(d.Payload.Id, Now, d.Payload)
                    );
                    File.Delete(f);
                    Changed?.Invoke("Webhook accepted the transcript.");
                }
                catch (Exception e)
                {
                    var reason =
                        e is InvalidOperationException
                            ? e.Message
                            : "Webhook connection failed or timed out.";
                    Storage.Write(f, d with { LastError = reason });
                    Changed?.Invoke(
                        reason
                            + (
                                d.Held
                                    ? " Held: check webhook configuration/access, then use Retry pending."
                                    : " Queued for retry."
                            )
                    );
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
