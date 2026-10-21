using System.Collections.ObjectModel;
using System.ComponentModel;
using Study.Core.Config;
using Study.Core.Models;

namespace Study.ViewModels;

/// <summary>A row of the PLC tree: a folder or a PLC. Holds the expand/select state the TreeView binds to.</summary>
public abstract class PlcTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PlcTreeNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}

public sealed class FolderNode : PlcTreeNode
{
    public FolderNode(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }

    /// <summary>Full '/' path, e.g. "Line1/Stacker".</summary>
    public string Path { get; }

    public int PlcCount { get; set; }

    public string CountText => PlcCount == 1 ? "1 PLC" : $"{PlcCount} PLCs";
}

public sealed class PlcNode : PlcTreeNode
{
    public PlcNode(PlcEntry entry) => Entry = entry;

    public PlcEntry Entry { get; }
}

public static class PlcTreeBuilder
{
    /// <summary>
    /// Builds sorted tree rows (folders first, then PLCs, each A→Z). <paramref name="isExpanded"/>
    /// decides each folder's initial state so a rebuild keeps what the user opened.
    /// </summary>
    public static List<PlcTreeNode> Build(IEnumerable<PlcEntry> plcs, Func<string, bool> isExpanded)
    {
        var tree = PlcTree.ToTree(plcs);
        var lookup = plcs.ToDictionary(p => p.Ip);
        var rows = new List<PlcTreeNode>();
        foreach (var g in tree.Groups)
        {
            rows.Add(BuildFolder(g, "", lookup, isExpanded));
        }

        rows.AddRange(tree.Plcs.Select(i => new PlcNode(lookup[i.Ip])));
        return rows;
    }

    private static FolderNode BuildFolder(PlcGroupNode g, string parent, Dictionary<string, PlcEntry> lookup,
        Func<string, bool> isExpanded)
    {
        var path = parent.Length == 0 ? g.Name : parent + PlcTree.Separator + g.Name;
        var folder = new FolderNode(g.Name, path) { IsExpanded = isExpanded(path) };
        var count = 0;
        foreach (var child in g.Groups)
        {
            var sub = BuildFolder(child, path, lookup, isExpanded);
            count += sub.PlcCount;
            folder.Children.Add(sub);
        }

        foreach (var item in g.Plcs)
        {
            folder.Children.Add(new PlcNode(lookup[item.Ip]));
            count++;
        }

        folder.PlcCount = count;
        return folder;
    }
}
