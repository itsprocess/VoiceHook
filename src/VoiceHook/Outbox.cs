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
    DateTimeOffset? NextAttempt = null
);

public sealed class Outbox(string directory, HttpClient http)
{
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
                Storage.Write(f, d with { Attempts = 0, NextAttempt = null });
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
                if (d.Attempts >= 5 || d.NextAttempt > DateTimeOffset.UtcNow)
                    continue;
                // Commit the attempt before sending. Restart never creates a new delivery identity.
                d = d with
                {
                    Attempts = d.Attempts + 1,
                    NextAttempt = DateTimeOffset.UtcNow.AddSeconds(
                        Math.Min(60, Math.Pow(2, d.Attempts + 1))
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
                        throw new InvalidOperationException(
                            $"Webhook returned HTTP {(int)res.StatusCode}."
                        );
                    Storage.Write(
                        Receipt(d.Payload.Id),
                        new { id = d.Payload.Id, acceptedAt = DateTimeOffset.UtcNow }
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
                                d.Attempts >= 5
                                    ? " Held after five attempts; use Retry pending."
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
