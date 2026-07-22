using System.Linq;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// Parsing <c>gh auth status</c>. It is human-readable text with no --json, so this is the piece most
/// likely to break on a gh upgrade — and the piece that must never let a token through.
/// </summary>
public class GitHubAccountParseTests
{
    /// <summary>Real output from gh 2.83.2, token masked exactly as gh prints it.</summary>
    private const string SingleAccount = """
        github.com
          ✓ Logged in to github.com account HouseAlwaysWin (keyring)
          - Active account: true
          - Git operations protocol: https
          - Token: gho_************************************
          - Token scopes: 'gist', 'read:org', 'repo', 'workflow'
        """;

    private const string TwoAccounts = """
        github.com
          ✓ Logged in to github.com account work-user (keyring)
          - Active account: false
          - Git operations protocol: https
          - Token: gho_************************************
          ✓ Logged in to github.com account HouseAlwaysWin (keyring)
          - Active account: true
          - Git operations protocol: https
          - Token: gho_************************************
        """;

    [Fact]
    public void Reads_the_account_and_marks_it_active()
    {
        var account = Assert.Single(GitHubAccountService.ParseAccounts(SingleAccount));

        Assert.Equal("github.com", account.Host);
        Assert.Equal("HouseAlwaysWin", account.Login);
        Assert.True(account.IsActive);
    }

    [Fact]
    public void Reads_several_accounts_and_picks_out_the_active_one()
    {
        var accounts = GitHubAccountService.ParseAccounts(TwoAccounts);

        Assert.Equal(["work-user", "HouseAlwaysWin"], accounts.Select(a => a.Login));
        Assert.Equal("HouseAlwaysWin", Assert.Single(accounts, a => a.IsActive).Login);
    }

    [Fact]
    public void The_keyring_note_is_not_part_of_the_login()
    {
        // "(keyring)" / "(oauth_token)" trails the name and must not end up in it.
        var account = Assert.Single(GitHubAccountService.ParseAccounts(SingleAccount));
        Assert.DoesNotContain("(", account.Login);
    }

    [Fact]
    public void No_token_material_survives_the_parse()
    {
        // The whole point: gh echoes a token and none of it may reach the app.
        var parsed = string.Join(
            "|",
            GitHubAccountService.ParseAccounts(TwoAccounts).Select(a => $"{a.Host}{a.Login}{a.IsActive}"));

        Assert.DoesNotContain("gho_", parsed);
        Assert.DoesNotContain("*", parsed);
        Assert.DoesNotContain("Token", parsed);
        Assert.DoesNotContain("scopes", parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("You are not logged into any GitHub hosts. To log in, run: gh auth login")]
    [InlineData("'gh' is not recognized as an internal or external command")]
    public void Nothing_usable_yields_no_accounts(string output) =>
        Assert.Empty(GitHubAccountService.ParseAccounts(output));

    [Fact]
    public void A_lone_account_with_no_active_line_is_treated_as_active()
    {
        // Older gh omits "Active account" when there is only one.
        var account = Assert.Single(GitHubAccountService.ParseAccounts(
            "github.com\n  ✓ Logged in to github.com account solo (oauth_token)\n  - Token: gho_****\n"));

        Assert.Equal("solo", account.Login);
        Assert.True(account.IsActive);
    }
}
