using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>
/// Static median-split AABB tree over leaf boxes (environment triangles).
/// Built once per cell; queried with an explicit stack (no recursion, no allocation
/// beyond the caller's hit list).
/// </summary>
public sealed class Bvh
{
    // Flat node array: internal nodes store child indices; leaves store a leaf range
    // into _leafIndices (sorted during build).
    struct Node
    {
        public Aabb Bounds;
        public int Left;       // internal: left child node index; leaf: -1
        public int Right;      // internal: right child node index
        public int LeafStart;  // leaf: range into _leafIndices
        public int LeafCount;
    }

    const int LeafSize = 8;

    readonly Node[] _nodes;
    readonly int[] _leafIndices;
    readonly Aabb[] _leafBounds;   // exact per-leaf filter at query time
    public Aabb Bounds { get; }
    public int LeafCountTotal => _leafIndices.Length;

    public Bvh(Aabb[] leaves)
    {
        _leafBounds = leaves;
        _leafIndices = new int[leaves.Length];
        for (int i = 0; i < leaves.Length; i++) _leafIndices[i] = i;

        if (leaves.Length == 0)
        {
            _nodes = [new Node { Bounds = Aabb.Empty, Left = -1, LeafStart = 0, LeafCount = 0 }];
            Bounds = Aabb.Empty;
            return;
        }

        var nodes = new List<Node>(leaves.Length * 2);
        BuildNode(nodes, leaves, 0, leaves.Length);
        _nodes = [.. nodes];
        Bounds = _nodes[0].Bounds;
    }

    int BuildNode(List<Node> nodes, Aabb[] leaves, int start, int count)
    {
        var bounds = Aabb.Empty;
        for (int i = start; i < start + count; i++)
            bounds = bounds.Union(leaves[_leafIndices[i]]);

        int nodeIdx = nodes.Count;
        nodes.Add(default);

        if (count <= LeafSize)
        {
            nodes[nodeIdx] = new Node
            {
                Bounds = bounds, Left = -1, Right = -1,
                LeafStart = start, LeafCount = count,
            };
            return nodeIdx;
        }

        // Split along the widest axis at the median.
        var ext = bounds.Extent;
        int axis = ext.X >= ext.Y
            ? (ext.X >= ext.Z ? 0 : 2)
            : (ext.Y >= ext.Z ? 1 : 2);

        float Center(int leafIdx) => axis switch
        {
            0 => leaves[leafIdx].Center.X,
            1 => leaves[leafIdx].Center.Y,
            _ => leaves[leafIdx].Center.Z,
        };

        Array.Sort(_leafIndices, start, count,
            Comparer<int>.Create((x, y) => Center(x).CompareTo(Center(y))));

        int half = count / 2;
        int left = BuildNode(nodes, leaves, start, half);
        int right = BuildNode(nodes, leaves, start + half, count - half);
        nodes[nodeIdx] = new Node
        {
            Bounds = bounds, Left = left, Right = right,
            LeafStart = 0, LeafCount = 0,
        };
        return nodeIdx;
    }

    /// <summary>Appends every leaf index whose AABB overlaps <paramref name="box"/>.</summary>
    public void Query(in Aabb box, List<int> hits)
    {
        if (_leafIndices.Length == 0) return;

        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            ref readonly var node = ref _nodes[stack[--sp]];
            if (!node.Bounds.Overlaps(box)) continue;

            if (node.Left < 0)
            {
                for (int i = node.LeafStart; i < node.LeafStart + node.LeafCount; i++)
                {
                    int leaf = _leafIndices[i];
                    if (_leafBounds[leaf].Overlaps(box)) hits.Add(leaf);
                }
                continue;
            }

            if (sp + 2 > stack.Length)
            {
                // Tree deeper than the stack (pathological): fall back to child recursion.
                QueryNode(node.Left, box, hits);
                QueryNode(node.Right, box, hits);
                continue;
            }
            stack[sp++] = node.Left;
            stack[sp++] = node.Right;
        }
    }

    void QueryNode(int idx, in Aabb box, List<int> hits)
    {
        ref readonly var node = ref _nodes[idx];
        if (!node.Bounds.Overlaps(box)) return;
        if (node.Left < 0)
        {
            for (int i = node.LeafStart; i < node.LeafStart + node.LeafCount; i++)
            {
                int leaf = _leafIndices[i];
                if (_leafBounds[leaf].Overlaps(box)) hits.Add(leaf);
            }
            return;
        }
        QueryNode(node.Left, box, hits);
        QueryNode(node.Right, box, hits);
    }
}
