using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using VoiceHook;
using Command = VoiceHook.Control;

internal static class Program
{
    static string root = Path.Combine(
        Path.GetTempPath(),
        "VoiceHook-tests-" + Guid.NewGuid().ToString("N")
    );
    static int passed;

    static void Check(bool value, string message = "Assertion failed")
    {
        if (!value)
            throw new Exception(message);
    }

    static async Task Test(string name, Func<Task> action)
    {
        await action();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    static Settings S() => new() { Keyboard = false, AutoSend = false };

    static Outbox Box(HttpMessageHandler? handler = null) =>
        new(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            new HttpClient(
                handler
                    ?? new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
            )
        );

    static Engine E(
        FakeCapture mic,
        FakeTranscriber transcriber,
        Settings? s = null,
        Outbox? box = null
    ) => new(() => s ?? S(), () => mic, transcriber, box ?? Box());

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(root);
            if (args.Contains("--ui"))
            {
                Storage.DirectoryPath = Path.Combine(root, "ui");
                ApplicationConfiguration.Initialize();
                using var form = new MainForm(preview: true);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new(-10000, -10000);
                form.ShowInTaskbar = false;
                form.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(root, "capture.png"));
                var tabs = form.Controls.OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                Application.DoEvents();
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(root, "settings.png"));
                Console.WriteLine(root);
                return 0;
            }
            if (args.Contains("--startup"))
            {
                Storage.DirectoryPath = Path.Combine(root, "startup");
                ApplicationConfiguration.Initialize();
                using var form = new MainForm();
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new(-10000, -10000);
                form.ShowInTaskbar = false;
                Exception? failure = null;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        Check(
                            !Descendants(form).Any(c => c.Text.StartsWith("Control setup failed")),
                            "Input controls did not start"
                        );
                        using var pipe = new NamedPipeClientStream(
                            ".",
                            VoiceHook.Controls.PipeName,
                            PipeDirection.InOut,
                            PipeOptions.Asynchronous
                        );
                        await pipe.ConnectAsync(2000);
                        await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"action\":\"status\"}\n"));
                        using var reader = new StreamReader(pipe);
                        var reply = await reader.ReadLineAsync();
                        Check(
                            JsonDocument.Parse(reply!).RootElement.GetProperty("state").GetString()
                                == "idle"
                        );
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }
                    finally
                    {
                        form.ExitApplication();
                    }
                };
                Application.Run(form);
                if (failure != null)
                    throw failure;
                Console.WriteLine(
                    "PASS tray startup, keyboard hook registration, IPC and application exit"
                );
                return 0;
            }
            if (args.Contains("--bridge"))
            {
                RunBridge(args[^1]).GetAwaiter().GetResult();
                return 0;
            }
            Run().GetAwaiter().GetResult();
            Console.WriteLine($"{passed} checks passed. Test files: {root}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    static IEnumerable<System.Windows.Forms.Control> Descendants(
        System.Windows.Forms.Control parent
    )
    {
        foreach (System.Windows.Forms.Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    static byte[] SpeechFixture()
    {
        using var memory = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToWaveStream(memory);
            synth.Speak("This is a test. Please turn the lights blue.");
            synth.SetOutputToNull();
        }
        return memory.ToArray();
    }

    static async Task RunBridge(string webhook)
    {
        var wav = SpeechFixture();
        var settings = S() with { AutoSend = true, Webhook = webhook };
        using var http = Net.Client();
        var outbox = new Outbox(Path.Combine(root, "bridge"), http);
        using var engine = new Engine(
            () => settings,
            () => new FixtureCapture(wav),
            new Transcriber(http),
            outbox
        );
        using var controls = new VoiceHook.Controls(engine, () => settings);
        controls.Start();
        Console.WriteLine("BRIDGE_READY");
        while (true)
        {
            await outbox.Flush();
            await Task.Delay(50);
        }
    }

    static async Task Run()
    {
        await Test(
            "settings and protected secrets",
            () =>
            {
                var secret = Storage.Protect("test-key");
                Check(secret != "test-key");
                Check(Storage.Reveal(secret) == "test-key");
                Settings.CheckUrl("http://127.0.0.1:1234/hook");
                Settings.CheckUrl("https://example.com/hook");
                foreach (
                    var url in new[]
                    {
                        "http://example.com",
                        "https://name:secret@example.com",
                        "file:///C:/a",
                        "https://example.com/#x",
                    }
                )
                {
                    bool failed = false;
                    try
                    {
                        Settings.CheckUrl(url);
                    }
                    catch (ArgumentException)
                    {
                        failed = true;
                    }
                    Check(failed, url);
                }
                Check(!VoiceHook.Controls.Authorized(null, "secret"));
                Check(VoiceHook.Controls.Authorized("secret", "secret"));
                return Task.CompletedTask;
            }
        );
        await Test(
            "single capture, duplicate controls and ownership",
            async () =>
            {
                var mic = new FakeCapture();
                var t = new FakeTranscriber();
                using var e = E(mic, t);
                Check(e.Command(new("start", "a"), "keyboard").Ok);
                e.Command(new("start", "a"), "keyboard");
                Check(mic.Starts == 1);
                Check(!e.Command(new("start", "other"), "udp").Ok);
                e.Command(new("stop", "a"), "udp");
                Check(e.State == "recording");
                e.Command(new("stop", "a"), "keyboard");
                await e.Work;
                e.Command(new("stop", "a"), "keyboard");
                Check(t.Calls == 1);
                Check(e.State == "idle");
                Check(e.Last?.Text == "test transcript");
                Check(!e.Command(new("start", "a"), "keyboard").Ok);
                e.Command(new("stop", "early"), "udp");
                Check(!e.Command(new("start", "early"), "udp").Ok);
                Check(!e.Command(new("start", null!), "udp").Ok);
            }
        );
        await Test(
            "cancel recording and cancel in-flight transcription",
            async () =>
            {
                var mic = new FakeCapture();
                var t = new FakeTranscriber();
                using var e = E(mic, t);
                e.Command(new("start", "a"), "button");
                e.CancelActive();
                await e.Work;
                Check(t.Calls == 0);
                Check(e.Last == null);
                var entered = new TaskCompletionSource();
                var slow = new FakeTranscriber(async ct =>
                {
                    entered.SetResult();
                    await Task.Delay(10000, ct);
                    return "wrong";
                });
                using var e2 = E(new(), slow);
                e2.Command(new("start", "b"), "button");
                e2.Command(new("stop", "b"), "button");
                await entered.Task;
                e2.CancelActive();
                await e2.Work;
                Check(e2.Last == null);
                Check(e2.State == "idle");
            }
        );
        await Test(
            "maximum duration stops capture; blank speech sends nothing",
            async () =>
            {
                var mic = new FakeCapture();
                var t = new FakeTranscriber(_ => Task.FromResult(" "));
                var box = Box();
                using var e = E(
                    mic,
                    t,
                    S() with
                    {
                        MaxSeconds = 1,
                        AutoSend = true,
                        Webhook = "http://localhost/hook",
                    },
                    box
                );
                e.Command(new("start", "limit"), "udp");
                await Task.Delay(1400);
                await e.Work;
                Check(e.State == "idle");
                Check(t.Calls == 1);
                Check(box.Count == 0);
            }
        );
        await Test(
            "OpenAI-compatible multipart and error contract",
            async () =>
            {
                var s = S() with
                {
                    Provider = "openai",
                    ApiKey = Storage.Protect("unit-test-secret"),
                };
                var http = new HttpClient(
                    new Handler(async request =>
                    {
                        Check(request.RequestUri?.AbsolutePath == "/v1/audio/transcriptions");
                        Check(request.Headers.Authorization?.Parameter == "unit-test-secret");
                        var form = (MultipartFormDataContent)request.Content!;
                        var parts = form.ToDictionary(p =>
                            p.Headers.ContentDisposition!.Name!.Trim('"')
                        );
                        Check(await parts["model"].ReadAsStringAsync() == "gpt-4o-mini-transcribe");
                        Check(await parts["response_format"].ReadAsStringAsync() == "json");
                        Check(parts["file"].Headers.ContentType?.MediaType == "audio/wav");
                        Check(
                            (await parts["file"].ReadAsByteArrayAsync()).SequenceEqual(
                                new byte[] { 1, 2, 3 }
                            )
                        );
                        return new(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"text\":\"lights blue\"}"),
                        };
                    })
                );
                Check(
                    await new Transcriber(http).Transcribe([1, 2, 3], s, CancellationToken.None)
                        == "lights blue"
                );
                foreach (
                    var response in new[]
                    {
                        new HttpResponseMessage(HttpStatusCode.Unauthorized),
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}"),
                        },
                    }
                )
                {
                    bool failed = false;
                    try
                    {
                        await new Transcriber(
                            new HttpClient(new Handler(_ => Task.FromResult(response)))
                        ).Transcribe([], s, CancellationToken.None);
                    }
                    catch (InvalidOperationException)
                    {
                        failed = true;
                    }
                    Check(failed);
                }
                bool bounded = false;
                try
                {
                    await Net.ReadBounded(
                        new StringContent(new string('x', 100)),
                        20,
                        CancellationToken.None
                    );
                }
                catch (InvalidOperationException)
                {
                    bounded = true;
                }
                Check(bounded);
            }
        );
        await Test(
            "outbox survives restart, stable identity, no repeated send",
            async () =>
            {
                var dir = Path.Combine(root, "outbox");
                var ids = new List<string>();
                int calls = 0;
                var http = new HttpClient(
                    new Handler(async req =>
                    {
                        ids.Add(req.Headers.GetValues("Idempotency-Key").Single());
                        var json = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
                        Check(json.RootElement.GetProperty("text").GetString() == "hello");
                        Check(req.Headers.Authorization?.Parameter == "hook-test");
                        return new(
                            ++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
                        );
                    })
                );
                var box = new Outbox(dir, http);
                var payload = new Transcript(
                    "stable-id",
                    "transcript.completed",
                    "hello",
                    "udp",
                    "windows",
                    DateTimeOffset.UtcNow.ToString("O"),
                    2
                );
                var s = S() with
                {
                    Webhook = "http://localhost/hook",
                    WebhookToken = Storage.Protect("hook-test"),
                };
                box.Enqueue(payload, s);
                await box.Flush();
                Check(box.Count == 1);
                box = new Outbox(dir, http);
                await box.RetryAll();
                Check(box.Count == 0);
                Check(ids.SequenceEqual(new[] { "stable-id", "stable-id" }));
                box.Enqueue(payload, s);
                await box.Flush();
                Check(calls == 2);
            }
        );
        await Test(
            "real local pipe and authenticated UDP transports",
            async () =>
            {
                using var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
                reserve.Close();
                var s = S() with { Udp = true, UdpPort = port };
                var mic = new FakeCapture();
                using var e = E(mic, new(), s);
                using var controls = new VoiceHook.Controls(e, () => s);
                controls.Start();
                using (
                    var pipe = new NamedPipeClientStream(
                        ".",
                        VoiceHook.Controls.PipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous
                    )
                )
                {
                    await pipe.ConnectAsync(2000);
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"action\":\"status\"}\n"));
                    using var reader = new StreamReader(pipe);
                    var reply = await reader.ReadLineAsync();
                    Check(
                        JsonDocument.Parse(reply!).RootElement.GetProperty("state").GetString()
                            == "idle"
                    );
                }
                using var udp = new UdpClient();
                udp.Connect(IPAddress.Loopback, port);
                await udp.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new Command("start", "bad", "wrong"),
                        Storage.Json
                    )
                );
                await Task.Delay(100);
                Check(mic.Starts == 0);
                await udp.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new Command("start", "u", s.UdpToken),
                        Storage.Json
                    )
                );
                using var timeout = new CancellationTokenSource(3000);
                var reply2 = await udp.ReceiveAsync(timeout.Token);
                Check(JsonDocument.Parse(reply2.Buffer).RootElement.GetProperty("ok").GetBoolean());
                Check(mic.Starts == 1);
                await udp.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new Command("stop", "u", s.UdpToken),
                        Storage.Json
                    )
                );
                await udp.ReceiveAsync(timeout.Token);
                await e.Work;
                Check(e.Last?.Source == "udp");
            }
        );
        await Test(
            "installed Windows recognizer transcribes real WAV fixture",
            async () =>
            {
                using var http = Net.Client();
                var text = await new Transcriber(http).Transcribe(
                    SpeechFixture(),
                    S(),
                    CancellationToken.None
                );
                Check(!string.IsNullOrWhiteSpace(text), "Windows recognizer returned empty text");
                Console.WriteLine("  Windows fixture: " + text);
            }
        );
    }

    sealed class FixtureCapture(byte[] wav) : ICapture
    {
        public void Start(int device) { }

        public Task<byte[]> Stop() => Task.FromResult(wav);

        public void Dispose() { }
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        ) => send(request);
    }

    sealed class FakeCapture : ICapture
    {
        public int Starts;

        public void Start(int device) => Starts++;

        public Task<byte[]> Stop() => Task.FromResult(new byte[] { 1, 2, 3 });

        public void Dispose() { }
    }

    sealed class FakeTranscriber(Func<CancellationToken, Task<string>>? run = null) : ITranscriber
    {
        public int Calls;

        public Task<string> Transcribe(byte[] wav, Settings s, CancellationToken token)
        {
            Calls++;
            return run?.Invoke(token) ?? Task.FromResult("test transcript");
        }
    }
}
