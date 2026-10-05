using System;
using System.IO;

namespace Layeron.App.Workspaces;

/// <summary>
/// A folder that holds the user's working files. Everything the user creates or edits while working -
/// scripts today, datasets and derived results later - lives under one workspace root, so a workspace can
/// be copied, backed up or shared as a unit.
/// <para>
/// The subfolder layout is fixed by the application rather than chosen per workspace: the point of the
/// convention is that a workspace written on one machine opens identically on another.
/// </para>
/// </summary>
public sealed class Workspace
{
    /// <summary>Folder, relative to the workspace root, that holds C# scripts.</summary>
    public const string ScriptsFolderName = "scripts";

    public Workspace(string root) => Root = Path.GetFullPath(root);

    /// <summary>Absolute path of the workspace root.</summary>
    public string Root { get; }

    /// <summary>Folder name, as shown in the UI.</summary>
    public string Name
    {
        get
        {
            var trimmed = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Root : name;
        }
    }

    /// <summary>Where this workspace's scripts live.</summary>
    public string ScriptsDirectory => Path.Combine(Root, ScriptsFolderName);

    /// <summary>Creates the workspace root and its standard subfolders when they are missing.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ScriptsDirectory);
    }

    public override string ToString() => Root;
}
