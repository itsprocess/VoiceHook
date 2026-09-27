using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VoiceHook;

static class SpeechTests
{
    static void Check(bool condition) { if (!condition) throw new Exception("Speech assertion failed"); }
    static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(5000);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    sealed class Player : ISpeechPlayer
    {
        public int Calls;
        public Task Speak(string text, Settings settings, CancellationToken token) { Interlocked.Increment(ref Calls); return Task.Delay(Timeout.Infinite, token); }
    }
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    public static async Task Run(string root)
    {
        var directory = Path.Combine(root, "speech");
        var settings = new Settings { SpeechEnabled = true };
        var player = new Player();
        await using (var queue = new SpeechQueue(directory, () => settings, player))
        {
            queue.Pause(true);
            Check(queue.Accept("one", "hello").State == "queued");
            Check(queue.Accept("one", "hello").Id == "one");
            try { queue.Accept("one", "changed"); throw new Exception("Expected conflict"); } catch (SpeechConflictException) { }
            for (int i = 0; i < 31; i++) queue.Accept("queued" + i, "later");
            try { queue.Accept("overflow", "full"); throw new Exception("Expected full queue"); } catch (SpeechQueueFullException) { }
            Check(player.Calls == 0);
            queue.Pause(false);
            await Until(() => player.Calls == 1);
            queue.Pause(true); // A PTT press interrupts playback and holds the next reply.
            await Until(() => queue.Find("one")?.State == "interrupted");
            Check(player.Calls == 1 && queue.Pending == 31);
        }
        settings = settings with { SpeechEnabled = false };
        await using (var queue = new SpeechQueue(directory, () => settings, player))
        {
            Check(queue.Find("one")?.State == "interrupted" && queue.Pending == 31);
            Check(queue.Accept("one", "hello").State == "interrupted");
        }
        var crash = Path.Combine(root, "speech-crash");
        Storage.Write(Path.Combine(crash, "speech.json"), new[] { new SpeechReceipt("crash", "do not repeat", "speaking", DateTimeOffset.UtcNow) });
        await using (var queue = new SpeechQueue(crash, () => settings, player)) Check(queue.Find("crash")?.State == "interrupted");
        Check(SpokenText.FromMarkdown("## Hi\n**there** and [link](https://example.com).\n```js\nsecret code\n```") == "Hi there and link. Code block omitted.");
        Check(string.Concat(SpokenText.Chunks(new string('a', 6000))).Length == 6000);

        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        settings = settings with { SpeechEnabled = true, SpeechPort = port };
        await using (var queue = new SpeechQueue(Path.Combine(root, "speech-http"), () => settings, player))
        await using (var server = new SpeechServer())
        {
            queue.Pause(true);
            await server.Start(settings, queue);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Check((await client.GetAsync("/health")).StatusCode == HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Storage.Reveal(settings.SpeechToken));
            Check((await client.GetAsync("/health")).IsSuccessStatusCode);
            Check((await client.PostAsJsonAsync("/speech", new { id = "http-test", text = "hello" })).StatusCode == HttpStatusCode.Accepted);
            Check((await client.PostAsJsonAsync("/speech", new { id = "http-test", text = "hello" })).StatusCode == HttpStatusCode.Accepted);
            Check(queue.Pending == 1);
            Check((await client.PostAsJsonAsync("/speech", new { id = "http-test", text = "different" })).StatusCode == HttpStatusCode.Conflict);
            Check((await client.PostAsJsonAsync("/speech", new { id = "bad", text = "" })).StatusCode == HttpStatusCode.BadRequest);
            Check((await client.PostAsync("/speech", new StringContent("not json"))).StatusCode == HttpStatusCode.BadRequest);
            Check((await client.GetAsync("/speech/http-test")).IsSuccessStatusCode);
            Check((await client.GetAsync("/speech/missing")).StatusCode == HttpStatusCode.NotFound);
            Check((await client.PostAsJsonAsync("/speech", new { id = "big", text = new string('x', 110_000) })).StatusCode == HttpStatusCode.RequestEntityTooLarge);
        }
        using var http = new HttpClient(new Handler(async request =>
        {
            Check(request.RequestUri!.AbsolutePath == "/v1/audio/speech");
            Check(request.Headers.Authorization?.Parameter == "fake-test-key");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("response_format").GetString() == "wav");
            Check(json.RootElement.GetProperty("input").GetString() == "test");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        var audio = await new SpeechPlayer(http).Synthesize("test", settings with { SpeechApiKey = Storage.Protect("fake-test-key") }, CancellationToken.None);
        Check(audio.Length == 3);
        Console.WriteLine("PASS speech: durable acceptance, deduplication, interruption, restart, bounds, authenticated HTTP and provider serialization");
    }
}
