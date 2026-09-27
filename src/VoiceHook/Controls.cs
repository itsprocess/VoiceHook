using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceHook;

public sealed class Controls(Engine engine, Func<Settings> settings) : IDisposable
{
    readonly CancellationTokenSource quit = new();
    UdpClient? udp;

    public void Start()
    {
        if (settings().Udp)
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, settings().UdpPort));
            _ = UdpLoop();
        }
        _ = PipeLoop();
    }

    public static string PipeName => "VoiceHook-" + Environment.UserName;

    public static bool Authorized(string? actual, string expected) =>
        actual != null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(expected)
        );

    async Task UdpLoop()
    {
        while (!quit.IsCancellationRequested)
            try
            {
                var r = await udp!.ReceiveAsync(quit.Token);
                if (r.Buffer.Length > 4096)
                    continue;
                var c = JsonSerializer.Deserialize<Control>(r.Buffer, Storage.Json);
                if (c == null || !Authorized(c.Token, settings().UdpToken))
                    continue;
                var reply = engine.Command(c, "udp");
                await udp.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(reply, Storage.Json),
                    r.RemoteEndPoint,
                    quit.Token
                );
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (JsonException) { }
            catch (SocketException)
            {
                if (quit.IsCancellationRequested)
                    break;
            }
    }

    async Task PipeLoop()
    {
        while (!quit.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );
                await pipe.WaitForConnectionAsync(quit.Token);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(quit.Token);
                limit.CancelAfter(TimeSpan.FromSeconds(3));
                var bytes = new List<byte>();
                var one = new byte[1];
                while (bytes.Count <= 4096 && await pipe.ReadAsync(one, limit.Token) > 0)
                {
                    if (one[0] == 10)
                        break;
                    bytes.Add(one[0]);
                }
                if (bytes.Count > 4096)
                    continue;
                var c = JsonSerializer.Deserialize<Control>(bytes.ToArray(), Storage.Json);
                if (c == null)
                    continue;
                var reply = engine.Command(c, "streamdeck");
                var output = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        reply,
                        new JsonSerializerOptions(Storage.Json) { WriteIndented = false }
                    ) + "\n"
                );
                await pipe.WriteAsync(output, limit.Token);
            }
            catch (OperationCanceledException)
            {
                if (quit.IsCancellationRequested)
                    break;
            }
            catch (IOException)
            {
                try
                {
                    await Task.Delay(100, quit.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (JsonException) { }
        }
    }

    public void Dispose()
    {
        quit.Cancel();
        udp?.Dispose();
    }
}

public sealed class KeyboardPtt : IDisposable
{
    delegate IntPtr Hook(int code, IntPtr w, IntPtr l);
    readonly Hook callback;
    readonly Engine engine;
    readonly Func<Settings> settings;
    readonly SynchronizationContext context;
    IntPtr hook;
    string? active;
    bool disposed;

    public KeyboardPtt(Engine engine, Func<Settings> settings)
    {
        this.engine = engine;
        this.settings = settings;
        context =
            SynchronizationContext.Current
            ?? throw new InvalidOperationException("Keyboard PTT requires the UI thread.");
        callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            throw new InvalidOperationException("Cannot register keyboard PTT.");
    }

    void Dispatch(string action, string id) =>
        context.Post(
            _ =>
            {
                if (!disposed)
                    engine.Command(new(action, id), "keyboard");
            },
            null
        );

    IntPtr OnKey(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(l);
            var flags = Marshal.ReadInt32(l, 8);
            var down = w == (IntPtr)0x100 || w == (IntPtr)0x104;
            var up = w == (IntPtr)0x101 || w == (IntPtr)0x105;
            var s = settings();
            if ((flags & 0x10) == 0)
            {
                if (active != null && up && key == s.Hotkey)
                {
                    var id = active;
                    active = null;
                    Dispatch("stop", id);
                    return (IntPtr)1;
                }
                if (
                    s.Keyboard
                    && key == s.Hotkey
                    && down
                    && (!s.Ctrl || Pressed(0x11))
                    && (!s.Alt || Pressed(0x12))
                    && (!s.Shift || Pressed(0x10))
                )
                {
                    if (active == null)
                    {
                        active = Guid.NewGuid().ToString("N");
                        Dispatch("start", active);
                    }
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(hook, code, w, l);
    }

    static bool Pressed(int code) => (GetAsyncKeyState(code) & 0x8000) != 0;

    public void Dispose()
    {
        disposed = true;
        if (active != null)
            engine.Command(new("cancel", active), "keyboard");
        if (hook != IntPtr.Zero)
            UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr GetModuleHandle(string? name);
}
