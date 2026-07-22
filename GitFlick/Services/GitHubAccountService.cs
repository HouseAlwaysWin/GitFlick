using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitFlick.Services;

/// <summary>One GitHub account the <c>gh</c> CLI knows about.</summary>
public sealed record GhAccount(string Host, string Login, bool IsActive);

/// <summary>
/// The account git pushes as, via the <c>gh</c> CLI.
/// <para>
/// GitFlick deliberately owns none of this. Every git invocation runs with
/// <c>GIT_TERMINAL_PROMPT=0</c> so a background fetch can't hang on a credential prompt, which also
/// means the app cannot drive an interactive sign-in — and it has no business holding tokens anyway.
/// So logging in and switching are handed to <c>gh</c>, which already does it properly, and GitFlick
/// only ever learns account <i>names</i>.
/// </para>
/// <para>
/// Nothing here goes through the git command log: <c>gh auth status</c> echoes a masked token, and
/// that must not be recorded anywhere. The raw output is parsed and dropped.
/// </para>
/// </summary>
public sealed class GitHubAccountService
{
    private const string DefaultHost = "github.com";

    private readonly string _ghPath;

    public GitHubAccountService(string? ghExecutablePath = null) =>
        _ghPath = string.IsNullOrWhiteSpace(ghExecutablePath) ? "gh" : ghExecutablePath;

    /// <summary>False once we've learned gh isn't installed, so the UI can offer a hint not dead buttons.</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>Accounts gh is signed in to. Empty when gh is missing or nobody is signed in.</summary>
    public async Task<IReadOnlyList<GhAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        // gh writes the status to stderr on some versions and stdout on others, so read both.
        var (ok, stdout, stderr) = await RunAsync(["auth", "status"], cancellationToken).ConfigureAwait(false);
        if (!IsAvailable)
        {
            return [];
        }

        // A non-zero exit just means "not logged in anywhere" — still parse, it may list hosts.
        _ = ok;
        return ParseAccounts(stdout + "\n" + stderr);
    }

    /// <summary>Makes <paramref name="login"/> the account gh acts as. Non-interactive.</summary>
    public async Task<bool> SwitchAsync(string host, string login, CancellationToken cancellationToken = default)
    {
        var (ok, _, _) = await RunAsync(
            ["auth", "switch", "--hostname", host, "--user", login], cancellationToken).ConfigureAwait(false);
        return ok;
    }

    /// <summary>Signs the account out of gh.</summary>
    public async Task<bool> LogoutAsync(string host, string login, CancellationToken cancellationToken = default)
    {
        var (ok, _, _) = await RunAsync(
            ["auth", "logout", "--hostname", host, "--user", login], cancellationToken).ConfigureAwait(false);
        return ok;
    }

    /// <summary>
    /// Starts <c>gh auth login</c> in its own console window and returns immediately. The device-code
    /// and browser steps happen entirely inside gh; GitFlick just re-reads the account list afterwards.
    /// </summary>
    public bool StartLogin()
    {
        try
        {
            var startInfo = new ProcessStartInfo(_ghPath)
            {
                // A visible console: gh's login is interactive and prints a one-time code to read.
                UseShellExecute = true,
            };

            foreach (var arg in new[] { "auth", "login", "--hostname", DefaultHost, "--git-protocol", "https", "--web" })
            {
                startInfo.ArgumentList.Add(arg);
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception)
        {
            // gh missing or blocked — the caller shows the "install gh" hint instead.
            IsAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Pulls host/login/active out of <c>gh auth status</c>. Everything else in that output — most
    /// importantly the <c>Token:</c> line — is ignored, so no secret can leak into the app.
    /// </summary>
    internal static IReadOnlyList<GhAccount> ParseAccounts(string output)
    {
        const string marker = "Logged in to ";
        const string accountWord = " account ";

        var accounts = new List<GhAccount>();
        string? host = null;
        string? login = null;

        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();

            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                // "✓ Logged in to github.com account HouseAlwaysWin (keyring)"
                var rest = line[(at + marker.Length)..];
                var accountAt = rest.IndexOf(accountWord, StringComparison.Ordinal);
                if (accountAt < 0)
                {
                    continue;
                }

                // Flush the previous account before starting a new one.
                Flush();

                host = rest[..accountAt].Trim();
                login = rest[(accountAt + accountWord.Length)..].Trim();

                // Drop the trailing "(keyring)" / "(oauth_token)" note.
                var paren = login.IndexOf(' ');
                if (paren > 0)
                {
                    login = login[..paren];
                }

                continue;
            }

            if (login is not null && line.StartsWith("- Active account:", StringComparison.OrdinalIgnoreCase))
            {
                var isActive = line.EndsWith("true", StringComparison.OrdinalIgnoreCase);
                accounts.Add(new GhAccount(host ?? DefaultHost, login, isActive));
                host = null;
                login = null;
            }
        }

        Flush();
        return accounts;

        // gh omits "Active account" when only one account exists on a host — that one is active.
        void Flush()
        {
            if (login is not null)
            {
                accounts.Add(new GhAccount(host ?? DefaultHost, login, accounts.Count == 0));
                host = null;
                login = null;
            }
        }
    }

    private async Task<(bool Ok, string StdOut, string StdErr)> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ghPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo)!;
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode == 0, stdout, stderr);
        }
        catch (Exception)
        {
            // Not installed, or not on PATH. Remembered so the UI stops offering gh actions.
            IsAvailable = false;
            return (false, string.Empty, string.Empty);
        }
    }
}
