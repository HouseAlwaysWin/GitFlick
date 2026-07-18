using System;
using System.Diagnostics;

namespace GitFlick.Services;

/// <summary>Opens a URL in the user's default browser. A failed launch must never crash the app.</summary>
public static class BrowserLauncher
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser / bad URL / blocked — nothing we can usefully do; swallow.
        }
    }
}
