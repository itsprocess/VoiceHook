using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace VoiceHook;

public sealed record SpeechRequest(string Id, string Text);

public sealed class SpeechServer : IAsyncDisposable
{
    WebApplication? app;
    public async Task Start(Settings settings, SpeechQueue queue)
    {
        if (!settings.SpeechEnabled) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // Never log incoming text or credentials.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, settings.SpeechPort);
            options.Limits.MaxRequestBodySize = 100_000;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        var server = builder.Build();
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + Storage.Reveal(settings.SpeechToken)));
        server.Use(async (ctx, next) =>
        {
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) { ctx.Response.StatusCode = 401; return; }
            await next(ctx);
        });
        server.MapGet("/health", () => Results.Json(new { healthy = true, protocol = "voicehook.speech/1" }));
        server.MapGet("/speech/{id}", (string id) => queue.Find(id) is { } receipt ? Results.Json(receipt, Storage.Json) : Results.NotFound());
        server.MapPost("/speech", async (HttpContext ctx) =>
        {
            try
            {
                var request = await JsonSerializer.DeserializeAsync<SpeechRequest>(ctx.Request.Body, Storage.Json, ctx.RequestAborted);
                if (request == null) return Results.BadRequest();
                var receipt = queue.Accept(request.Id, request.Text);
                return Results.Json(receipt, Storage.Json, statusCode: 202);
            }
            catch (SpeechConflictException) { return Results.Conflict(new { error = "ID already used with different text." }); }
            catch (SpeechQueueFullException) { return Results.StatusCode(429); }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (JsonException) { return Results.BadRequest(); }
            catch (BadHttpRequestException e) { return Results.StatusCode(e.StatusCode); }
            catch (ObjectDisposedException) { return Results.StatusCode(503); }
            catch (IOException) { return Results.StatusCode(503); }
        });
        try { await server.StartAsync(); app = server; }
        catch { await server.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (app == null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); } finally { await app.DisposeAsync(); app = null; }
    }
}
