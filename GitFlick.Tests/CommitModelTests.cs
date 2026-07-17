using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The built-in commit-AI pieces: catalog resolution, verified download, engine settings.</summary>
public class CommitModelTests
{
    [Fact]
    public void Resolve_known_id_returns_that_preset()
    {
        var preset = CommitModelCatalog.Resolve("qwen2.5-0.5b");
        Assert.Equal("qwen2.5-0.5b", preset.Id);
    }

    [Fact]
    public void Resolve_unknown_or_empty_id_falls_back_to_the_default()
    {
        Assert.Equal(CommitModelCatalog.DefaultModelId, CommitModelCatalog.Resolve("no-such-model").Id);
        Assert.Equal(CommitModelCatalog.DefaultModelId, CommitModelCatalog.Resolve(null).Id);
    }

    [Fact]
    public async Task Downloader_accepts_a_verified_download()
    {
        var payload = Encoding.UTF8.GetBytes("fake gguf payload for the happy path");
        var preset = ServePreset(payload, Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), out var listener, out _);
        var destination = CommitModelCatalog.GetModelPath(preset);

        try
        {
            var path = await new ModelDownloader().DownloadAsync(preset);

            Assert.Equal(destination, path);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".part"));
        }
        finally
        {
            listener.Stop();
            TryDelete(destination);
        }
    }

    [Fact]
    public async Task Downloader_rejects_a_corrupted_download_and_leaves_nothing_behind()
    {
        var payload = Encoding.UTF8.GetBytes("corrupted payload");
        var wrongSha = new string('0', 64);
        var preset = ServePreset(payload, wrongSha, out var listener, out _);
        var destination = CommitModelCatalog.GetModelPath(preset);

        try
        {
            await Assert.ThrowsAsync<IOException>(() => new ModelDownloader().DownloadAsync(preset));

            Assert.False(File.Exists(destination));           // never promoted
            Assert.False(File.Exists(destination + ".part")); // partial cleaned up
        }
        finally
        {
            listener.Stop();
            TryDelete(destination);
        }
    }

    [Fact]
    public void Engine_choice_persists_through_settings()
    {
        var settings = new FakeSettingsService();
        var vm = new WorkspaceViewModel(new GitService(), new RepositoryItem("r", Path.GetTempPath()), settings);

        Assert.Equal(0, vm.AiEngineIndex);   // Builtin is the default
        Assert.True(vm.UseBuiltinEngine);

        vm.AiEngineIndex = 1;

        Assert.Equal(CommitAiEngine.Ollama, settings.Current.AiEngine);
        Assert.True(vm.UseOllamaEngine);
        Assert.True(settings.SaveCount > 0);
    }

    [Fact]
    public void Selecting_a_builtin_model_persists_its_id()
    {
        var settings = new FakeSettingsService();
        var vm = new WorkspaceViewModel(new GitService(), new RepositoryItem("r", Path.GetTempPath()), settings);

        vm.SelectedBuiltinModel = CommitModelCatalog.Resolve("qwen2.5-coder-1.5b");

        Assert.Equal("qwen2.5-coder-1.5b", settings.Current.BuiltinModelId);
        Assert.True(settings.SaveCount > 0);
    }

    [Fact]
    public async Task Builtin_generator_asks_for_a_download_when_the_model_is_missing()
    {
        var settings = new FakeSettingsService();
        settings.Current.BuiltinModelId = "gemma-4-e2b";   // large model, certainly not downloaded in CI
        using var generator = new LlamaCommitMessageGenerator(settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync("diff --git a/x b/x"));

        Assert.Contains("download", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Serves <paramref name="payload"/> once over localhost and returns a preset pointing at it.</summary>
    private static CommitModelPreset ServePreset(byte[] payload, string sha256, out HttpListener listener, out string url)
    {
        var port = Random.Shared.Next(20000, 60000);
        listener = new HttpListener();
        url = $"http://localhost:{port}/model.gguf";
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var server = listener;
        _ = Task.Run(async () =>
        {
            var context = await server.GetContextAsync();
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });

        return new CommitModelPreset(
            "test-model",
            "Test model",
            url,
            $"test-model-{Guid.NewGuid():N}.gguf",
            sha256,
            payload.Length);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
