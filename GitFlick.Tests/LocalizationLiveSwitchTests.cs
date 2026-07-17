using System.Threading;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;
using GitFlick.Markup;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// Guards the live language switch: <c>{loc:Tr Key}</c> must re-render when the language changes with no
/// rebuild. Uses the real <see cref="TrExtension"/> markup on a rooted control in a headless window —
/// a faithful reproduction of the app, where the earlier string-indexer binding failed to refresh.
/// </summary>
public class LocalizationLiveSwitchTests
{
    [Fact]
    public void Changing_language_re_renders_a_Tr_binding_live()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

        session.Dispatch(() =>
        {
            var loc = LocalizationService.Instance;
            var original = loc.CurrentLanguage;

            try
            {
                loc.CurrentLanguage = Language.English;

                var target = new TextBlock();
                var binding = (Binding)new TrExtension("Toolbar_Fetch").ProvideValue(null!);
                target.Bind(TextBlock.TextProperty, binding);

                var window = new Window { Content = target };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Fetch", target.Text);

                loc.CurrentLanguage = Language.Japanese;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("フェッチ", target.Text);

                loc.CurrentLanguage = Language.SimplifiedChinese;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("获取", target.Text);

                loc.CurrentLanguage = Language.TraditionalChinese;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("抓取", target.Text);
            }
            finally
            {
                loc.CurrentLanguage = original;
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
