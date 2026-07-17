using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GitFlick.Services;

/// <summary>
/// The Ollama engine, for users who already run a local Ollama server. Reads the URL and model
/// from settings; failures (server down, model missing, timeout) surface as a readable message.
/// </summary>
public sealed class OllamaCommitMessageGenerator : ICommitMessageGenerator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    private readonly ISettingsService _settings;

    public OllamaCommitMessageGenerator(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task<string> GenerateAsync(string diff, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            throw new InvalidOperationException("Nothing staged to describe.");
        }

        var baseUrl = _settings.Current.OllamaUrl;
        var model = _settings.Current.OllamaModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("No Ollama model set — open ⚙ to configure one.");
        }

        var request = new OllamaGenerateRequest(model, CommitMessagePrompt.Build(diff), false);
        var json = JsonSerializer.Serialize(request, OllamaJsonContext.Default.OllamaGenerateRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var url = baseUrl.TrimEnd('/') + "/api/generate";

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Can't reach Ollama at {baseUrl} — is it running? ({ex.Message})");
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("Ollama timed out generating the message.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama error: {ExtractError(body) ?? $"HTTP {(int)response.StatusCode}"}");
        }

        using var document = JsonDocument.Parse(body);
        var text = document.RootElement.TryGetProperty("response", out var element) ? element.GetString() : null;
        return CommitMessagePrompt.Clean(text ?? string.Empty);
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var element) ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record OllamaGenerateRequest(string Model, string Prompt, bool Stream);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OllamaGenerateRequest))]
internal partial class OllamaJsonContext : JsonSerializerContext;
