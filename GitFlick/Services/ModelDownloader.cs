using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GitFlick.Services;

/// <summary>
/// Downloads a model file with SHA-256 verification (ported from GimmeCapture's
/// ArtifactDownloader). Writes to a .part file and only moves it into place once the hash
/// matches, so an interrupted or corrupted download can never be mistaken for a model.
/// </summary>
public sealed class ModelDownloader
{
    private const int BufferSize = 128 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitFlick/1.0");
        return client;
    }

    /// <summary>Progress is fraction-complete in [0,1].</summary>
    public async Task<string> DownloadAsync(
        CommitModelPreset preset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CommitModelCatalog.ModelsDirectory);
        var destinationPath = CommitModelCatalog.GetModelPath(preset);
        var partPath = destinationPath + ".part";
        TryDelete(partPath);

        try
        {
            using var response = await Http.GetAsync(
                preset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long totalRead = 0;

                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        totalRead += read;

                        if (preset.Size > 0)
                        {
                            progress?.Report(Math.Clamp((double)totalRead / preset.Size, 0, 1));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (preset.Size > 0 && totalRead != preset.Size)
                {
                    throw new IOException(
                        $"Download incomplete: expected {preset.Size} bytes, received {totalRead}.");
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actual, preset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"SHA-256 mismatch for {preset.FileName} — download corrupted.");
                }
            }

            File.Move(partPath, destinationPath, overwrite: true);
            progress?.Report(1);
            return destinationPath;
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
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
            // Best effort; a leftover .part is harmless (overwritten on the next attempt).
        }
    }
}
