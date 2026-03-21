using System.Collections.Generic;
using System.Linq;

namespace Propability_NTNU_v1.Classes.Toolbox
{
    /// <summary>RHS catalog (hot-finished EN 10210-2). Sizes from eurocodeapplied.com.</summary>
    public static class RHS_Catalog
    {
        public struct Entry
        {
            public int H, B, T;
            public Entry(int h, int b, int t) { H = h; B = b; T = t; }
            public string Tag => $"RHS_{H}x{B}x{T}";
        }

        public static readonly Entry[] Entries = new Entry[]
        {
            new Entry(50, 30, 2), new Entry(50, 30, 3), new Entry(50, 30, 4), new Entry(50, 30, 5),
            new Entry(60, 40, 3), new Entry(60, 40, 4), new Entry(60, 40, 5), new Entry(60, 40, 6),
            new Entry(80, 40, 3), new Entry(80, 40, 4), new Entry(80, 40, 5), new Entry(80, 40, 6), new Entry(80, 40, 8),
            new Entry(90, 50, 3), new Entry(90, 50, 4), new Entry(90, 50, 5), new Entry(90, 50, 6), new Entry(90, 50, 8),
            new Entry(100, 50, 3), new Entry(100, 50, 4), new Entry(100, 50, 5), new Entry(100, 50, 6), new Entry(100, 50, 8),
            new Entry(100, 60, 3), new Entry(100, 60, 4), new Entry(100, 60, 5), new Entry(100, 60, 6), new Entry(100, 60, 8),
            new Entry(120, 60, 4), new Entry(120, 60, 5), new Entry(120, 60, 6), new Entry(120, 60, 8), new Entry(120, 60, 10),
            new Entry(120, 80, 4), new Entry(120, 80, 5), new Entry(120, 80, 6), new Entry(120, 80, 8), new Entry(120, 80, 10),
            new Entry(140, 80, 4), new Entry(140, 80, 5), new Entry(140, 80, 6), new Entry(140, 80, 8), new Entry(140, 80, 10),
            new Entry(150, 100, 4), new Entry(150, 100, 5), new Entry(150, 100, 6), new Entry(150, 100, 8), new Entry(150, 100, 10), new Entry(150, 100, 13),
            new Entry(160, 80, 4), new Entry(160, 80, 5), new Entry(160, 80, 6), new Entry(160, 80, 8), new Entry(160, 80, 10), new Entry(160, 80, 13),
            new Entry(180, 100, 4), new Entry(180, 100, 5), new Entry(180, 100, 6), new Entry(180, 100, 8), new Entry(180, 100, 10), new Entry(180, 100, 13),
            new Entry(200, 100, 4), new Entry(200, 100, 5), new Entry(200, 100, 6), new Entry(200, 100, 8), new Entry(200, 100, 10), new Entry(200, 100, 13), new Entry(200, 100, 16),
            new Entry(200, 120, 6), new Entry(200, 120, 8), new Entry(200, 120, 10), new Entry(200, 120, 13),
            new Entry(250, 150, 6), new Entry(250, 150, 8), new Entry(250, 150, 10), new Entry(250, 150, 13), new Entry(250, 150, 16),
            new Entry(260, 180, 6), new Entry(260, 180, 8), new Entry(260, 180, 10), new Entry(260, 180, 13), new Entry(260, 180, 16),
            new Entry(300, 200, 6), new Entry(300, 200, 8), new Entry(300, 200, 10), new Entry(300, 200, 13), new Entry(300, 200, 16),
            new Entry(350, 250, 6), new Entry(350, 250, 8), new Entry(350, 250, 10), new Entry(350, 250, 13), new Entry(350, 250, 16),
            new Entry(400, 200, 8), new Entry(400, 200, 10), new Entry(400, 200, 13), new Entry(400, 200, 16),
            new Entry(450, 250, 8), new Entry(450, 250, 10), new Entry(450, 250, 13), new Entry(450, 250, 16),
            new Entry(500, 300, 10), new Entry(500, 300, 13), new Entry(500, 300, 16), new Entry(500, 300, 20),
        };

        public static List<(int H, int B, int T)> AllCombinations()
        {
            var list = new List<(int, int, int)>();
            foreach (var e in Entries) list.Add((e.H, e.B, e.T));
            return list;
        }

        public static List<(int H, int B)> AllSizes()
        {
            var set = new HashSet<(int H, int B)>();
            foreach (var e in Entries) set.Add((e.H, e.B));
            return set.OrderBy(x => x.H).ThenBy(x => x.B).ToList();
        }

        public static int[] ThicknessesFor(int h, int b)
        {
            return Entries.Where(e => e.H == h && e.B == b).Select(e => e.T).Distinct().OrderBy(t => t).ToArray();
        }
    }
}
