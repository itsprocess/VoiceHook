using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceHook;

public sealed record Settings
{
    public string Provider { get; init; } = "windows";
    public int Device { get; init; } = -1;
    public string Language { get; init; } = "en-US";
    public string Endpoint { get; init; } = "https://api.openai.com/v1/audio/transcriptions";
    public string Model { get; init; } = "gpt-4o-mini-transcribe";
    public string ApiKey { get; init; } = ""; // DPAPI ciphertext, never a plaintext config secret
    public string Webhook { get; init; } = "";
    public string WebhookToken { get; init; } = "";
    public bool AutoSend { get; init; } = true;
    public int Hotkey { get; init; } = 120; // F9
    public bool Keyboard { get; init; } = true;
    public bool Ctrl { get; init; } = false;
    public bool Alt { get; init; } = false;
    public bool Shift { get; init; } = false;
    public bool Udp { get; init; } = false;
    public int UdpPort { get; init; } = 17654;
    public string UdpToken { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public int MaxSeconds { get; init; } = 60;

    public Settings Validate()
    {
        if (Provider is not ("windows" or "openai"))
            throw new ArgumentException("Choose Windows or an OpenAI-compatible service.");
        if (MaxSeconds < 1 || MaxSeconds > 300)
            throw new ArgumentException("Recording limit must be 1–300 seconds.");
        if (Hotkey < 112 || Hotkey > 135)
            throw new ArgumentException("Choose F1–F24 for PTT.");
        if (UdpPort < 1024 || UdpPort > 65535 || UdpToken.Length < 32)
            throw new ArgumentException("Invalid UDP port or token.");
        if (Device < -1)
            throw new ArgumentException("Invalid microphone.");
        if (Provider == "openai")
        {
            CheckUrl(Endpoint);
            if (string.IsNullOrWhiteSpace(Model))
                throw new ArgumentException("A transcription model is required.");
        }
        if (Webhook.Length > 0)
            CheckUrl(Webhook);
        return this;
    }

    public static Uri CheckUrl(string value)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var u)
            || u.UserInfo.Length > 0
            || u.Fragment.Length > 0
            || !(u.Scheme == "https" || u.Scheme == "http" && u.IsLoopback)
        )
            throw new ArgumentException(
                "Use HTTPS, or HTTP on loopback; no embedded credentials or fragments."
            );
        return u;
    }
}

public static class Storage
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    public static string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceHook"
    );

    public static void Write<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        File.Move(temp, file, true);
    }

    public static Settings Load()
    {
        var f = Path.Combine(DirectoryPath, "settings.json");
        return File.Exists(f)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(f), Json)!.Validate()
            : new Settings();
    }

    public static void Save(Settings value) =>
        Write(Path.Combine(DirectoryPath, "settings.json"), value.Validate());

    public static string Protect(string value) =>
        value.Length == 0
            ? ""
            : Convert.ToBase64String(
                ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(value),
                    null,
                    DataProtectionScope.CurrentUser
                )
            );

    public static string Reveal(string value) =>
        value.Length == 0
            ? ""
            : Encoding.UTF8.GetString(
                ProtectedData.Unprotect(
                    Convert.FromBase64String(value),
                    null,
                    DataProtectionScope.CurrentUser
                )
            );
}
