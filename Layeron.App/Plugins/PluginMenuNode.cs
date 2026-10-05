using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using ReactiveUI.Primitives;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace Layeron.App.Plugins;

/// <summary>
/// One contributed menu entry as the window sees it. Contributions arrive as a flat list of
/// slash-separated paths; the view model turns that into the nested item list the Menu control binds
/// to, so a plugin never has to know anything about the menu's structure.
/// </summary>
public sealed class PluginMenuNode
{
    public PluginMenuNode(string header, int order)
    {
        Header = header;
        Order = order;
    }

    public string Header { get; }

    public int Order { get; }

    public List<PluginMenuNode> Children { get; } = [];

    /// <summary>Present on leaves: the plugin-provided action.</summary>
    public ICommand? Command { get; private set; }

    public bool IsSeparator { get; private init; }

    public static PluginMenuNode Separator() => new("-", 0) { IsSeparator = true };

    internal void SetCommand(ICommand command) => Command = command;

    /// <summary>
    /// Builds the nested tree from flat path descriptions.
    /// <para>
    /// A contribution's <c>Path</c> names the folder it lives in and its <c>Header</c> is the entry
    /// itself, so the leaf is appended under the folders. Treating the path as the whole tree instead -
    /// which this used to do - assigned the command to the last folder and ignored the header, so every
    /// entry sharing a path collapsed onto one item and its own label never appeared.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PluginMenuNode> Build(IEnumerable<MenuDescription> menus)
    {
        var roots = new List<PluginMenuNode>();

        foreach (var menu in menus.OrderBy(m => m.Order).ThenBy(m => m.Header, StringComparer.OrdinalIgnoreCase))
        {
            var folders = menu.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var level = roots;
            foreach (var folder in folders)
            {
                var node = level.FirstOrDefault(n => string.Equals(n.Header, folder, StringComparison.OrdinalIgnoreCase));
                if (node == null)
                {
                    node = new PluginMenuNode(folder, menu.Order);
                    level.Add(node);
                }

                level = node.Children;
            }

            // Reuse an identical entry so a duplicate registration overwrites rather than doubles up.
            var leaf = level.FirstOrDefault(n => string.Equals(n.Header, menu.Header, StringComparison.OrdinalIgnoreCase));
            if (leaf == null)
            {
                leaf = new PluginMenuNode(menu.Header, menu.Order);
                level.Add(leaf);
            }

            leaf.SetCommand(new PluginMenuCommand(menu));
        }

        // Sort every level once, keeping declared order first and header as the tie-breaker.
        Sort(roots);
        return roots;
    }

    private static void Sort(List<PluginMenuNode> nodes)
    {
        nodes.Sort((a, b) =>
        {
            var byOrder = a.Order.CompareTo(b.Order);
            return byOrder != 0 ? byOrder : string.Compare(a.Header, b.Header, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var node in nodes)
            Sort(node.Children);
    }
}

/// <summary>A contributed menu entry in the form the menu builder consumes.</summary>
public sealed record MenuDescription(string Path, string Header, Action Execute, Func<bool>? CanExecute, int Order);

/// <summary>Command adapter so the Menu control can invoke a plugin callback and honour its predicate.</summary>
internal sealed class PluginMenuCommand(MenuDescription description) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        try
        {
            return description.CanExecute?.Invoke() ?? true;
        }
        catch
        {
            return false;
        }
    }

    public void Execute(object? parameter)
    {
        description.Execute();
        RaiseCanExecuteChanged();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
