using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTrial
{
    // Observed occupancy only: no route, action scoring, or reachability oracle.
    internal sealed class ExplorationMemory
    {
        const double CellSize = 2;
        sealed class Visit { public int entries; public double seconds; }
        readonly Dictionary<Tuple<int, int>, Visit> cells = new Dictionary<Tuple<int, int>, Visit>();
        Tuple<int, int> previousCell;
        double? previousTime;
        double activeSeconds, lastNewCellTime;

        static Tuple<int, int> Key(double x, double y) { return Tuple.Create((int)Math.Floor(x / CellSize), (int)Math.Floor(y / CellSize)); }
        public void Reset() { cells.Clear(); previousCell = null; previousTime = null; activeSeconds = lastNewCellTime = 0; }
        public void Suspend() { previousTime = null; }

        public void Observe(double x, double y, double time, bool active)
        {
            if (!active) { Suspend(); return; }
            if (previousTime.HasValue)
            {
                double elapsed = Math.Max(0, time - previousTime.Value);
                activeSeconds += elapsed;
                if (previousCell != null && cells.ContainsKey(previousCell)) cells[previousCell].seconds += elapsed;
            }
            previousTime = time;
            var key = Key(x, y);
            Visit visit;
            if (!cells.TryGetValue(key, out visit))
            {
                visit = new Visit(); cells[key] = visit; lastNewCellTime = activeSeconds;
            }
            if (previousCell == null || !previousCell.Equals(key)) visit.entries++;
            previousCell = key;
        }

        public object Snapshot(double x, double y, double left, double right, double bottom, double top)
        {
            Visit current;
            cells.TryGetValue(Key(x, y), out current);
            var nearby = cells.Select(pair => new { x = (pair.Key.Item1 + .5) * CellSize, y = (pair.Key.Item2 + .5) * CellSize, visit = pair.Value })
                .Where(c => c.x + CellSize / 2 >= left && c.x - CellSize / 2 <= right && c.y + CellSize / 2 >= bottom && c.y - CellSize / 2 <= top)
                .OrderBy(c => (c.x - x) * (c.x - x) + (c.y - y) * (c.y - y)).Take(64)
                .Select(c => new { x = Math.Round(c.x - x, 2), y = Math.Round(c.y - y, 2), entries = c.visit.entries, seconds = Math.Round(c.visit.seconds, 2) }).ToArray();
            return new { cell_size = CellSize, seconds_since_new_cell = Math.Round(activeSeconds - lastNewCellTime, 2),
                current_cell_seconds = current == null ? 0 : Math.Round(current.seconds, 2), current_cell_entries = current == null ? 0 : current.entries,
                nearby_visited_cells = nearby };
        }

        public static void SelfTest()
        {
            var m = new ExplorationMemory();
            m.Observe(0, 0, 0, true); m.Observe(0, 0, 1, true);
            m.Observe(0, 0, 11, false); m.Observe(0, 0, 21, true);
            m.Observe(2.1, 0, 22, true); m.Observe(0, 0, 23, true);
            if (m.cells[Key(0, 0)].seconds != 2 || m.cells[Key(0, 0)].entries != 2
                || m.cells[Key(2.1, 0)].seconds != 1 || m.activeSeconds - m.lastNewCellTime != 1)
                throw new Exception("visit memory dwell/re-entry/paused-time exclusion");
            if (Key(-.1, -.1).Item1 != -1 || Key(-.1, -.1).Item2 != -1) throw new Exception("visit memory negative cells");
            m.Reset(); if (m.cells.Count != 0 || m.activeSeconds != 0 || m.previousTime.HasValue) throw new Exception("visit memory reset");
        }
    }
}
