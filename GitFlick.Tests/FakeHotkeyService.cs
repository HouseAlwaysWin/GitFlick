using System;
using System.Collections.Generic;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>Records what got registered, and can refuse a combo the way a taken hotkey would.</summary>
internal sealed class FakeHotkeyService : IGlobalHotkeyService
{
    public List<HotkeyDefinition> Registered { get; } = [];

    public int UnregisterCount { get; private set; }

    /// <summary>Return a message to make <see cref="TryRegister"/> fail for that combo.</summary>
    public Func<HotkeyDefinition, string?>? Reject { get; set; }

    public event EventHandler? HotkeyPressed { add { } remove { } }

    public bool TryRegister(HotkeyDefinition hotkey, out string? error)
    {
        error = Reject?.Invoke(hotkey);

        if (error is not null)
        {
            return false;
        }

        Registered.Add(hotkey);
        return true;
    }

    public void Unregister() => UnregisterCount++;

    public void Dispose()
    {
    }
}
