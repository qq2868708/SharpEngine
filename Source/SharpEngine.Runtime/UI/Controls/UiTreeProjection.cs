namespace SharpEngine.UI;

public sealed record UiTreeNode(string Key, string? ParentKey);
public sealed record UiVisibleTreeNode(string Key, int Depth, bool HasChildren, bool Expanded);

/// <summary>Shared tree traversal. Expansion and selection belong to each view instance, never to the content asset.</summary>
public static class UiTreeProjection
{
    public static IReadOnlyList<UiVisibleTreeNode> Visible(IReadOnlyList<UiTreeNode> Nodes,
        IReadOnlySet<string> Collapsed, IReadOnlySet<string>? Included = null)
    {
        var Keys = Nodes.Select(Node => Node.Key).ToHashSet(StringComparer.Ordinal);
        if (Keys.Count != Nodes.Count)
        {
            throw new InvalidDataException("UI_TREE_DUPLICATE: tree keys must be unique");
        }
        var Children = Nodes.ToLookup(Node => Node.ParentKey ?? string.Empty, StringComparer.Ordinal);
        if (Nodes.Any(Node => string.IsNullOrEmpty(Node.Key) || Node.ParentKey is not null && !Keys.Contains(Node.ParentKey)))
        {
            throw new InvalidDataException("UI_TREE_PARENT: non-empty keys and existing parents are required");
        }
        var Reachable = new HashSet<string>(StringComparer.Ordinal);
        var Validation = new Queue<UiTreeNode>(Children[string.Empty]);
        while (Validation.Count != 0)
        {
            var Node = Validation.Dequeue();
            if (!Reachable.Add(Node.Key))
            {
                throw new InvalidDataException("UI_TREE_CYCLE: tree contains a cycle");
            }
            foreach (var Child in Children[Node.Key])
            {
                Validation.Enqueue(Child);
            }
        }
        if (Reachable.Count != Nodes.Count)
        {
            throw new InvalidDataException("UI_TREE_CYCLE: tree contains unreachable cyclic nodes");
        }
        var Pending = new Stack<(UiTreeNode Node, int Depth)>();
        foreach (var Node in Children[string.Empty].Reverse())
        {
            Pending.Push((Node, 0));
        }
        var Result = new List<UiVisibleTreeNode>();
        var Visited = new HashSet<string>(StringComparer.Ordinal);
        while (Pending.Count != 0)
        {
            var Entry = Pending.Pop();
            if (!Visited.Add(Entry.Node.Key))
            {
                throw new InvalidDataException("UI_TREE_CYCLE: tree contains a cycle");
            }
            if (Included is not null && !Included.Contains(Entry.Node.Key))
            {
                continue;
            }
            var HasChildren = Children.Contains(Entry.Node.Key);
            var Expanded = HasChildren && (Included is not null || !Collapsed.Contains(Entry.Node.Key));
            Result.Add(new UiVisibleTreeNode(Entry.Node.Key, Entry.Depth, HasChildren, Expanded));
            if (Expanded)
            {
                foreach (var Child in Children[Entry.Node.Key].Reverse())
                {
                    Pending.Push((Child, Entry.Depth + 1));
                }
            }
        }
        return Result;
    }
}
