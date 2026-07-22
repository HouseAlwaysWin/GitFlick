using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GitFlick.Models;

namespace GitFlick.ViewModels;

/// <summary>
/// One row of the commit's file tree: a folder with children, or a file carrying its
/// <see cref="CommitFileEntry"/>. Built from the flat path list git gives us.
/// </summary>
public sealed class CommitFileNode
{
    /// <summary>Folder segment(s) or the file name — not the full path.</summary>
    public required string Name { get; init; }

    public required bool IsFolder { get; init; }

    /// <summary>The underlying entry; null for a folder.</summary>
    public CommitFileEntry? File { get; init; }

    public ObservableCollection<CommitFileNode> Children { get; } = [];

    /// <summary>Status letter for a file row; blank for a folder.</summary>
    public string Badge => File?.Badge ?? string.Empty;

    public override string ToString() => Name;

    /// <summary>
    /// Turns "a/b/c.cs" paths into a folder tree. Runs of single-child folders are collapsed into one
    /// row ("src/Services/Core" rather than three nested levels), because commit paths are usually deep
    /// and narrow — without it the tree is mostly indentation.
    /// </summary>
    public static IReadOnlyList<CommitFileNode> Build(IEnumerable<CommitFileEntry> files)
    {
        var root = new Folder();

        foreach (var file in files)
        {
            var segments = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var folder = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                folder = folder.Sub(segments[i]);
            }

            folder.Files.Add((segments[^1], file));
        }

        return Flatten(root);
    }

    private static List<CommitFileNode> Flatten(Folder folder)
    {
        var nodes = new List<CommitFileNode>();

        foreach (var (name, sub) in folder.Subs)
        {
            nodes.Add(FromFolder(name, sub));
        }

        foreach (var (name, entry) in folder.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new CommitFileNode { Name = name, IsFolder = false, File = entry });
        }

        return nodes;
    }

    private static CommitFileNode FromFolder(string name, Folder folder)
    {
        // Collapse "a" -> "b" -> "c" into a single "a/b/c" row while the chain stays single-file-less.
        while (folder.Files.Count == 0 && folder.Subs.Count == 1)
        {
            var (childName, child) = folder.Subs.First();
            name = name + "/" + childName;
            folder = child;
        }

        var node = new CommitFileNode { Name = name, IsFolder = true };
        foreach (var child in Flatten(folder))
        {
            node.Children.Add(child);
        }

        return node;
    }

    /// <summary>Mutable scaffold used only while building; folders sort case-insensitively.</summary>
    private sealed class Folder
    {
        public SortedDictionary<string, Folder> Subs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Name, CommitFileEntry Entry)> Files { get; } = [];

        public Folder Sub(string name)
        {
            if (!Subs.TryGetValue(name, out var sub))
            {
                sub = new Folder();
                Subs[name] = sub;
            }

            return sub;
        }
    }
}
