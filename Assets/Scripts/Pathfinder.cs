using System;
using System.Collections.Generic;
using UnityEngine;

// A* over the infinite tile grid for click-to-move. 8-directional with integer costs (orthogonal 10,
// diagonal 14) and an octile heuristic. A diagonal step is rejected when it would cut a water corner,
// matching the player's WASD slide rule. The search is capped by a node budget so an ocean/unreachable
// target can't spin forever; if the goal can't be reached it returns the route to the closest reachable
// cell, so clicking on water walks the player to the nearest shore instead of refusing to move.
public static class Pathfinder
{
    const int Ortho = 10, Diag = 14;

    static readonly Vector2Int[] Dirs =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),     // orthogonal
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),   // diagonal
    };

    // Cells to step onto in order (start excluded, goal included when reachable). Empty if already at
    // the goal or boxed in. walkable(x,y) decides which cells may be entered.
    public static List<Vector2Int> FindPath(Vector2Int start, Vector2Int goal,
                                            Func<int, int, bool> walkable, int maxNodes = 8000)
    {
        var path = new List<Vector2Int>();
        if (start == goal) return path;

        var open = new MinHeap();
        var came = new Dictionary<Vector2Int, Vector2Int>();
        var g = new Dictionary<Vector2Int, int> { [start] = 0 };
        var closed = new HashSet<Vector2Int>();

        open.Push(start, Heuristic(start, goal));
        Vector2Int best = start;                            // closest cell reached, for the unreachable case
        int bestH = Heuristic(start, goal);
        int expanded = 0;

        while (open.Count > 0 && expanded < maxNodes)
        {
            Vector2Int cur = open.Pop();
            if (!closed.Add(cur)) continue;                 // stale heap duplicate, already finalized
            if (cur == goal) { best = goal; break; }
            expanded++;

            int curH = Heuristic(cur, goal);
            if (curH < bestH) { bestH = curH; best = cur; }

            int cg = g[cur];
            for (int i = 0; i < 8; i++)
            {
                Vector2Int d = Dirs[i];
                Vector2Int nb = new(cur.x + d.x, cur.y + d.y);
                if (closed.Contains(nb) || !walkable(nb.x, nb.y)) continue;
                bool diag = d.x != 0 && d.y != 0;
                if (diag && (!walkable(cur.x + d.x, cur.y) || !walkable(cur.x, cur.y + d.y)))
                    continue;                               // don't slip diagonally between two water cells

                int ng = cg + (diag ? Diag : Ortho);
                if (g.TryGetValue(nb, out int known) && ng >= known) continue;
                g[nb] = ng;
                came[nb] = cur;
                open.Push(nb, ng + Heuristic(nb, goal));
            }
        }

        Vector2Int end = came.ContainsKey(goal) ? goal : best;
        if (end == start || !came.ContainsKey(end)) return path;   // never moved (boxed in / clicked self)
        for (Vector2Int c = end; c != start; c = came[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    static int Heuristic(Vector2Int a, Vector2Int b)
    {
        int dx = Mathf.Abs(a.x - b.x), dy = Mathf.Abs(a.y - b.y);
        int min = Mathf.Min(dx, dy);
        return Diag * min + Ortho * (Mathf.Max(dx, dy) - min);
    }

    // Binary min-heap keyed by int priority (Unity's runtime has no PriorityQueue). Lazy duplicates: a
    // node may be pushed more than once with a better priority; the closed set discards the stale pops.
    sealed class MinHeap
    {
        readonly List<Vector2Int> items = new();
        readonly List<int> prio = new();
        public int Count => items.Count;

        public void Push(Vector2Int item, int priority)
        {
            items.Add(item); prio.Add(priority);
            for (int c = items.Count - 1; c > 0;)
            {
                int p = (c - 1) / 2;
                if (prio[c] >= prio[p]) break;
                Swap(c, p); c = p;
            }
        }

        public Vector2Int Pop()
        {
            Vector2Int root = items[0];
            int last = items.Count - 1;
            items[0] = items[last]; prio[0] = prio[last];
            items.RemoveAt(last); prio.RemoveAt(last);
            for (int c = 0, n = items.Count; ;)
            {
                int l = 2 * c + 1, r = 2 * c + 2, s = c;
                if (l < n && prio[l] < prio[s]) s = l;
                if (r < n && prio[r] < prio[s]) s = r;
                if (s == c) break;
                Swap(c, s); c = s;
            }
            return root;
        }

        void Swap(int a, int b)
        {
            (items[a], items[b]) = (items[b], items[a]);
            (prio[a], prio[b]) = (prio[b], prio[a]);
        }
    }
}
