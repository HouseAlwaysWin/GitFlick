using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GitFlick.Services;

/// <summary>
/// Generates a commit message from the staged diff using a local Ollama server — nothing leaves
/// the machine. Failures (server down, model missing, timeout) surface as a readable message.
/// </summary>
public sealed class OllamaCommitMessageGenerator : ICommitMessageGenerator
{
    private const int MaxDiffChars = 8000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    public async Task<string> GenerateAsync(string diff, string baseUrl, string model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            throw new InvalidOperationException("Nothing staged to describe.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("No Ollama model set — open ⚙ to configure one.");
        }

        var request = new OllamaGenerateRequest(model, BuildPrompt(diff), false);
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
        return Clean(text ?? string.Empty);
    }

    private static string BuildPrompt(string diff)
    {
        var trimmed = diff.Length > MaxDiffChars ? diff[..MaxDiffChars] + "\n… (diff truncated)" : diff;
        return
            "Write a git commit message for the staged changes below.\n" +
            "- One short imperative summary line (~50-72 chars). Use Conventional Commits " +
            "(feat:, fix:, refactor:, docs:, test:, chore:) when it fits.\n" +
            "- If useful, add a blank line then 1-3 short bullet points.\n" +
            "- Output ONLY the commit message. No code fences, no preamble.\n\n" +
            "Diff:\n" + trimmed;
    }

    // Small local models sometimes wrap the message in a code fence; strip it so the box is clean.
    private static string Clean(string message)
    {
        var text = message.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            if (firstBreak >= 0)
            {
                text = text[(firstBreak + 1)..];
            }

            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text[..^3];
            }
        }

        return text.Trim();
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
