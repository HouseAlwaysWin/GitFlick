using Avalonia.Media;
using Avalonia.Styling;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

public class ThemeAndAccentTests
{
    [Fact]
    public void Parse_reads_a_valid_hex()
    {
        var c = AccentService.Parse("#588CF0");
        Assert.Equal(0xFF, c.A);
        Assert.Equal(0x58, c.R);
        Assert.Equal(0x8C, c.G);
        Assert.Equal(0xF0, c.B);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-colour")]
    public void Parse_falls_back_to_the_default_blue(string? input)
    {
        Assert.Equal(Color.FromRgb(0x58, 0x8C, 0xF0), AccentService.Parse(input));
    }

    [Fact]
    public void Lighten_moves_fully_to_white_at_t1_and_not_at_all_at_t0()
    {
        var grey = Color.FromRgb(100, 100, 100);
        Assert.Equal(Colors.White, AccentService.Lighten(grey, 1.0));
        Assert.Equal(grey, AccentService.Lighten(grey, 0.0));
    }

    [Fact]
    public void Darken_moves_fully_to_black_at_t1()
    {
        var grey = Color.FromRgb(200, 200, 200);
        Assert.Equal(Colors.Black, AccentService.Darken(grey, 1.0));
    }

    [Fact]
    public void Shade_math_preserves_the_source_alpha()
    {
        var translucent = Color.FromArgb(0x80, 10, 20, 30);
        Assert.Equal(0x80, AccentService.Lighten(translucent, 0.5).A);
        Assert.Equal(0x80, AccentService.Darken(translucent, 0.5).A);
    }

    [Fact]
    public void WithAlpha_swaps_only_the_alpha_channel()
    {
        var opaque = Color.FromRgb(0x58, 0x8C, 0xF0);
        var faded = AccentService.WithAlpha(opaque, 0x2E);
        Assert.Equal(0x2E, faded.A);
        Assert.Equal(opaque.R, faded.R);
        Assert.Equal(opaque.G, faded.G);
        Assert.Equal(opaque.B, faded.B);
    }

    [Theory]
    [InlineData(AppThemeVariant.System)]
    [InlineData(AppThemeVariant.Light)]
    [InlineData(AppThemeVariant.Dark)]
    public void ToThemeVariant_maps_every_variant(AppThemeVariant variant)
    {
        var expected = variant switch
        {
            AppThemeVariant.Light => ThemeVariant.Light,
            AppThemeVariant.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        Assert.Equal(expected, ThemeService.ToThemeVariant(variant));
    }

    [Fact]
    public void ToThemeVariant_maps_system_to_default_not_a_fixed_variant()
    {
        // System must defer to the OS, i.e. Default — never a pinned Light/Dark.
        var result = ThemeService.ToThemeVariant(AppThemeVariant.System);
        Assert.Equal(ThemeVariant.Default, result);
        Assert.NotEqual(ThemeVariant.Light, result);
        Assert.NotEqual(ThemeVariant.Dark, result);
    }
}
