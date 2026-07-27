using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using GitFlick.Models;
using GitFlick.ViewModels;
using GitFlick.Views;

namespace GitFlick.Tests;

/// <summary>
/// Shows the real <see cref="ConflictWindow"/> on a headless Avalonia dispatcher: proves the XAML
/// loads, the AvaloniaEdit editor realises, and the code-behind wires the editor delegates the view
/// model drives. Compiled bindings are checked at build time; this catches the runtime-only failures
/// (a missing asset, an editor that won't template) that a build can't.
/// </summary>
public class ConflictWindowSmokeTests
{
    [Fact]
    public void Window_loads_shows_and_feeds_the_selected_conflict_into_the_editor()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

        session.Dispatch(() =>
        {
            using var repo = new TestRepo();
            repo.WriteFile("a.txt", "<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> feature\n");

            var git = new FakeGitService
            {
                StubStatus = new GitStatus
                {
                    Entries =
                    [
                        new GitStatusEntry { Path = "a.txt", Kind = GitChangeKind.Unmerged, UnmergedCode = "UU" },
                    ],
                },
            };

            var vm = new ConflictResolverViewModel(git, repo.Path, ConflictOperation.Merge);
            var window = new ConflictWindow { DataContext = vm };

            // The window itself doesn't carry AvaloniaEdit's control theme (App.axaml does in the real
            // app); scope it here so the TextEditor can template when the window is shown.
            window.Styles.Add(new StyleInclude(new Uri("avares://GitFlick.Tests/"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
            });

            window.Show();
            Dispatcher.UIThread.RunJobs();

            // MainWindow kicks the load right after showing; do the same.
            vm.LoadAsync().GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();

            Assert.NotEmpty(vm.Conflicts);
            Assert.True(vm.IsTextConflict);
            Assert.NotNull(vm.GetEditorText);
            Assert.Contains("<<<<<<<", vm.GetEditorText!());   // the working file reached the editor

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
