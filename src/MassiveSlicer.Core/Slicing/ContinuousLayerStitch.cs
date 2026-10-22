using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Joins the extrude chains on each layer into as few continuous paths as the
/// beads allow. Loose ends stitch to the nearest other end, and an end that
/// only meets a wall or infill line in the middle is bridged onto that line.
/// A bridge is an extruded gap within 3× bead width, the same limit as toolpath
/// optimize. A bridge that would cross or ride a bead is left out. No bead is
/// printed twice.
/// </summary>
public static class ContinuousLayerStitch
{
    public readonly record struct Stats(int PiecesBefore, int PiecesAfter, int Bridges)
    {
        public override string ToString() =>
            $"{PiecesBefore} pieces → {PiecesAfter}, {Bridges} bridge(s)";
    }

    public static Stats Apply(Toolpath toolpath, float beadWidth)
    {
        float bridge = MathF.Max(beadWidth, 0.2f) * 3f;
        int before = 0, after = 0, bridges = 0;
        Vector2? prevEnd = null;
        foreach (var layer in toolpath.Layers)
        {
            bool mill = false;
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Mill) { mill = true; break; }
            if (mill || layer.Moves.Count == 0)
            {
                if (layer.Moves.Count > 0) prevEnd = new Vector2(layer.Moves[^1].To.X, layer.Moves[^1].To.Y);
                continue;
            }

            var graph = new Graph(layer.Z, bridge);
            int pieces = graph.Build(layer);
            if (pieces == 0) continue;
            before += pieces;
            int added = graph.Stitch();
            bridges += added;
            var trails = graph.Trails();
            after += trails.Count;
            graph.Write(layer, trails, prevEnd);
            if (layer.Moves.Count > 0)
                prevEnd = new Vector2(layer.Moves[^1].To.X, layer.Moves[^1].To.Y);
        }
        return new Stats(before, after, bridges);
    }

    sealed class Graph
    {
        readonly float _z;
        readonly float _bridge;
        readonly List<Vector2> _nodes = [];
        readonly List<Edge> _edges = [];
        readonly List<List<int>> _adj = [];
        readonly Dictionary<long, int> _weld = new();
        readonly EdgeGrid _grid;
        int _stamp;

        public Graph(float z, float bridge)
        {
            _z = z;
            _bridge = bridge;
            _grid = new EdgeGrid(MathF.Max(bridge, 1f));
        }

        public int Build(ToolpathLayer layer)
        {
            int pieces = 0;
            int? open = null;
            foreach (var move in layer.Moves)
            {
                if (move.Kind != MoveKind.Extrude || move.IsWipe) { open = null; continue; }
                var a = new Vector2(move.From.X, move.From.Y);
                var b = new Vector2(move.To.X, move.To.Y);
                if (Vector2.DistanceSquared(a, b) < 1e-8f) continue;
                int ia = Node(a);
                int ib = Node(b);
                if (ia == ib) continue;
                bool cont = open is int prev && prev == ia;
                AddEdge(ia, ib, move, original: true);
                if (!cont) pieces++;
                open = ib;
            }
            return Math.Max(pieces, _edges.Count == 0 ? 0 : 1);
        }

        public int Stitch()
        {
            int added = 0;
            added += PairOdds();
            var pending = Odds();
            foreach (var odd in pending)
            {
                if (Degree(odd) % 2 == 0) continue;
                if (TryProject(odd)) added++;
            }
            added += PairOdds();
            return added;
        }

        public List<List<ToolpathMove>> Trails()
        {
            var trails = new List<List<ToolpathMove>>();
            int guard = _edges.Count + 2;
            while (guard-- > 0)
            {
                int start = -1;
                foreach (var odd in Odds())
                    if (UnusedAt(odd)) { start = odd; break; }
                if (start < 0)
                {
                    for (int i = 0; i < _nodes.Count; i++)
                        if (UnusedAt(i)) { start = i; break; }
                }
                if (start < 0) break;
                var steps = Walk(start);
                if (steps.Count == 0) break;
                var moves = new List<ToolpathMove>(steps.Count);
                foreach (var (ei, from, to) in steps)
                    moves.Add(Orient(_edges[ei].Move, _nodes[from], _nodes[to]));
                if (moves.Count > 0) trails.Add(moves);
            }
            return trails;
        }

        public void Write(ToolpathLayer layer, List<List<ToolpathMove>> trails, Vector2? prevEnd)
        {
            layer.Moves.Clear();
            layer.Contours.Clear();
            if (trails.Count == 0) return;
            var segs = new List<(Vector2 A, Vector2 B)>();
            foreach (var trail in trails)
                foreach (var move in trail)
                    segs.Add((new Vector2(move.From.X, move.From.Y), new Vector2(move.To.X, move.To.Y)));
            float near = MathF.Max(_bridge / 3f * 1.6f, 0.4f);
            float snap = MathF.Max(_bridge / 3f * 4f, 1f);
            var pending = new List<List<ToolpathMove>>(trails);
            var cursor = prevEnd ?? new Vector2(pending[0][0].From.X, pending[0][0].From.Y);
            while (pending.Count > 0)
            {
                int best = 0;
                bool reverse = false;
                float bestD = float.MaxValue;
                for (int i = 0; i < pending.Count; i++)
                {
                    var trail = pending[i];
                    float d0 = Dist2(cursor, trail[0].From);
                    float d1 = Dist2(cursor, trail[^1].To);
                    if (d0 < bestD) { bestD = d0; best = i; reverse = false; }
                    if (d1 < bestD) { bestD = d1; best = i; reverse = true; }
                }
                var pick = pending[best];
                pending.RemoveAt(best);
                if (reverse) pick.Reverse();
                if (reverse)
                {
                    for (int i = 0; i < pick.Count; i++)
                        pick[i] = pick[i] with { From = pick[i].To, To = pick[i].From };
                }
                var start = pick[0].From;
                var startXy = new Vector2(start.X, start.Y);
                if (Vector2.DistanceSquared(cursor, startXy) > 0.05f * 0.05f)
                {
                    if (BeadTravelRouter.LeavesBeads(cursor, startXy, segs, near)
                        && BeadTravelRouter.TryRoute(cursor, startXy, segs, snap, out var along))
                    {
                        var cur = new Vector3(cursor.X, cursor.Y, layer.Z);
                        for (int i = 1; i < along.Count; i++)
                        {
                            var nxt = new Vector3(along[i].X, along[i].Y, layer.Z);
                            if (Vector3.DistanceSquared(cur, nxt) < 1e-4f) continue;
                            layer.Moves.Add(new ToolpathMove(cur, nxt, MoveKind.Travel));
                            cur = nxt;
                        }
                        if (Vector3.DistanceSquared(cur, start) > 1e-4f)
                            layer.Moves.Add(new ToolpathMove(cur, start, MoveKind.Travel));
                    }
                    else
                        layer.Moves.Add(new ToolpathMove(new Vector3(cursor.X, cursor.Y, layer.Z), start, MoveKind.Travel));
                }
                layer.Moves.AddRange(pick);
                var end = pick[^1].To;
                cursor = new Vector2(end.X, end.Y);
            }
        }

        int PairOdds()
        {
            int added = 0;
            while (true)
            {
                var odds = Odds();
                int bu = -1, bv = -1;
                float best = _bridge;
                for (int i = 0; i < odds.Count; i++)
                for (int j = i + 1; j < odds.Count; j++)
                {
                    float d = Vector2.Distance(_nodes[odds[i]], _nodes[odds[j]]);
                    if (d >= best || d < 0.05f) continue;
                    if (Hits(_nodes[odds[i]], _nodes[odds[j]], -1)) continue;
                    best = d;
                    bu = odds[i];
                    bv = odds[j];
                }
                if (bu < 0) return added;
                AddEdge(bu, bv, Bridge(_nodes[bu], _nodes[bv]), original: false);
                added++;
            }
        }

        bool TryProject(int odd)
        {
            var p = _nodes[odd];
            int host = -1;
            float best = _bridge;
            Vector2 at = default;
            for (int ei = 0; ei < _edges.Count; ei++)
            {
                if (!_edges[ei].Original) continue;
                if (_edges[ei].A == odd || _edges[ei].B == odd) continue;
                var a = _nodes[_edges[ei].A];
                var b = _nodes[_edges[ei].B];
                if (!Closest(p, a, b, out var q, out float d)) continue;
                if (d >= best) continue;
                if (Hits(p, q, ei)) continue;
                best = d;
                host = ei;
                at = q;
            }
            if (host < 0) return false;
            int onto = Split(host, at);
            if (onto == odd) return false;
            if (Vector2.Distance(_nodes[odd], _nodes[onto]) < 0.05f) return false;
            if (Hits(_nodes[odd], _nodes[onto], host)) return false;
            AddEdge(odd, onto, Bridge(_nodes[odd], _nodes[onto]), original: false);
            return true;
        }

        List<(int Edge, int From, int To)> Walk(int start)
        {
            var tour = new List<(int Edge, int From, int To)>();
            void Dfs(int u)
            {
                while (true)
                {
                    int ei = Take(u);
                    if (ei < 0) break;
                    int v = _edges[ei].A == u ? _edges[ei].B : _edges[ei].A;
                    Dfs(v);
                    tour.Add((ei, u, v));
                }
            }
            Dfs(start);
            tour.Reverse();
            return tour;
        }

        int Take(int node)
        {
            var list = _adj[node];
            while (list.Count > 0)
            {
                int ei = list[^1];
                list.RemoveAt(list.Count - 1);
                if (!_edges[ei].Used)
                {
                    _edges[ei].Used = true;
                    return ei;
                }
            }
            return -1;
        }

        bool UnusedAt(int node)
        {
            foreach (int ei in _adj[node])
                if (!_edges[ei].Used) return true;
            return false;
        }

        int Split(int edgeIndex, Vector2 p)
        {
            var edge = _edges[edgeIndex];
            if (Vector2.Distance(_nodes[edge.A], p) < 0.05f) return edge.A;
            if (Vector2.Distance(_nodes[edge.B], p) < 0.05f) return edge.B;
            int mid = AddNode(p);
            var move = edge.Move;
            var point = new Vector3(p.X, p.Y, move.From.Z);
            Unlink(edge.A, edgeIndex);
            Unlink(edge.B, edgeIndex);
            _edges[edgeIndex] = new Edge(edge.A, mid, move with { To = point }, edge.Original);
            Link(edge.A, edgeIndex);
            Link(mid, edgeIndex);
            _grid.Add(edgeIndex, _nodes[edge.A], p);
            int second = _edges.Count;
            _edges.Add(new Edge(mid, edge.B, move with { From = point }, edge.Original));
            Link(mid, second);
            Link(edge.B, second);
            _grid.Add(second, p, _nodes[edge.B]);
            return mid;
        }

        int Node(Vector2 p)
        {
            // Cast through int. (uint) of a negative float is 0, which welded every
            // point below Y = 0 that shared an X into one vertex and drew a chord.
            int ix = (int)MathF.Round(p.X * 20f);
            int iy = (int)MathF.Round(p.Y * 20f);
            long key = ((long)ix << 32) | (uint)iy;
            if (_weld.TryGetValue(key, out int id)) return id;
            id = AddNode(p);
            _weld[key] = id;
            return id;
        }

        int AddNode(Vector2 p)
        {
            int id = _nodes.Count;
            _nodes.Add(p);
            _adj.Add([]);
            return id;
        }

        void AddEdge(int a, int b, ToolpathMove move, bool original)
        {
            int ei = _edges.Count;
            _edges.Add(new Edge(a, b, move, original));
            Link(a, ei);
            Link(b, ei);
            _grid.Add(ei, _nodes[a], _nodes[b]);
        }

        void Link(int node, int edge) => _adj[node].Add(edge);

        void Unlink(int node, int edge) => _adj[node].Remove(edge);

        List<int> Odds()
        {
            var odds = new List<int>();
            for (int i = 0; i < _nodes.Count; i++)
                if ((_adj[i].Count & 1) == 1) odds.Add(i);
            return odds;
        }

        int Degree(int node) => _adj[node].Count;

        bool Hits(Vector2 a, Vector2 b, int ignore)
        {
            _stamp++;
            _grid.Query(a, b, out var hits);
            foreach (int ei in hits)
            {
                if (ei == ignore || _edges[ei].Stamp == _stamp) continue;
                _edges[ei].Stamp = _stamp;
                var c = _nodes[_edges[ei].A];
                var d = _nodes[_edges[ei].B];
                if (ContinuousPlanarField.ProperCross(a, b, c, d)
                    || ContinuousPlanarField.OverlapsBead(a, b, c, d))
                    return true;
            }
            return false;
        }

        ToolpathMove Bridge(Vector2 a, Vector2 b) =>
            new(new Vector3(a.X, a.Y, _z), new Vector3(b.X, b.Y, _z), MoveKind.Extrude)
            {
                Normal = Vector3.UnitZ,
            };

        static ToolpathMove Orient(ToolpathMove move, Vector2 from, Vector2 to)
        {
            bool forward = Vector2.DistanceSquared(new Vector2(move.From.X, move.From.Y), from)
                <= Vector2.DistanceSquared(new Vector2(move.To.X, move.To.Y), from);
            if (forward)
                return move with
                {
                    From = new Vector3(from.X, from.Y, move.From.Z),
                    To = new Vector3(to.X, to.Y, move.To.Z),
                };
            return move with
            {
                From = new Vector3(from.X, from.Y, move.To.Z),
                To = new Vector3(to.X, to.Y, move.From.Z),
            };
        }

        static float Dist2(Vector2 a, Vector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        static bool Closest(Vector2 p, Vector2 a, Vector2 b, out Vector2 q, out float d)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-8f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            q = a + ab * t;
            d = Vector2.Distance(p, q);
            return d <= 1e6f;
        }

        sealed class Edge
        {
            public int A;
            public int B;
            public ToolpathMove Move;
            public bool Original;
            public bool Used;
            public int Stamp;
            public Edge(int a, int b, ToolpathMove move, bool original)
            {
                A = a; B = b; Move = move; Original = original;
            }
        }

        sealed class EdgeGrid
        {
            readonly float _cell;
            readonly Dictionary<(int X, int Y), List<int>> _bins = new();
            public EdgeGrid(float cell) { _cell = cell; }
            public void Add(int edge, Vector2 a, Vector2 b) =>
                Touch(a, b, key =>
                {
                    if (!_bins.TryGetValue(key, out var list)) _bins[key] = list = [];
                    list.Add(edge);
                });
            public void Query(Vector2 a, Vector2 b, out List<int> hits)
            {
                var found = new List<int>();
                Touch(a, b, key =>
                {
                    if (_bins.TryGetValue(key, out var list)) found.AddRange(list);
                });
                hits = found;
            }
            void Touch(Vector2 a, Vector2 b, Action<(int X, int Y)> visit)
            {
                int x0 = Cell(MathF.Min(a.X, b.X)), x1 = Cell(MathF.Max(a.X, b.X));
                int y0 = Cell(MathF.Min(a.Y, b.Y)), y1 = Cell(MathF.Max(a.Y, b.Y));
                for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    visit((x, y));
            }
            int Cell(float v) => (int)MathF.Floor(v / _cell);
        }
    }
}
