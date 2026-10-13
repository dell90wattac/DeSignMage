namespace DeSignMage.Vectorize;

/// <summary>
/// Connected regions (4-connectivity) of equal palette index. Region ids are consecutive from 0.
/// </summary>
internal sealed class RegionMap
{
    public RegionMap(int width, int height, int[] label, int[] area, int[] color)
    {
        Width = width; Height = height; Label = label; Area = area; Color = color;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Region id per pixel.</summary>
    public int[] Label { get; }
    public int[] Area { get; }
    /// <summary>Palette index of each region (-1 = transparent).</summary>
    public int[] Color { get; }
    public int Count => Area.Length;

    public static RegionMap Build(int[] palIndex, int w, int h)
    {
        int n = w * h;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) { if (ra < rb) parent[rb] = ra; else parent[ra] = rb; }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, v = palIndex[p];
                if (x > 0 && palIndex[p - 1] == v) Union(p, p - 1);
                if (y > 0 && palIndex[p - w] == v) Union(p, p - w);
            }
        }

        var label = new int[n];
        var idOfRoot = new int[n];
        Array.Fill(idOfRoot, -1);
        var area = new List<int>();
        var color = new List<int>();
        for (int p = 0; p < n; p++)
        {
            int r = Find(p);
            int id = idOfRoot[r];
            if (id < 0)
            {
                id = area.Count;
                idOfRoot[r] = id;
                area.Add(0);
                color.Add(palIndex[p]);
            }
            label[p] = id;
            area[id]++;
        }
        return new RegionMap(w, h, label, area.ToArray(), color.ToArray());
    }

    /// <summary>
    /// The noise filter. Merges (a) every region smaller than <paramref name="minArea"/> and (b) thin "fringe" regions,
    /// the one-or-two-pixel bands of in-between color that anti-aliasing leaves along every edge, into the neighboring
    /// region with the closest color. Returns the new palette index per pixel; call <see cref="Build"/> again afterwards
    /// because merged regions can now touch same-colored neighbors.
    /// </summary>
    public int[] MergeSmall(Quantizer.PaletteLab palette, int minArea, double thinWidth)
    {
        int w = Width, h = Height, n = w * h;
        int count = Count;
        var perimeter = new int[count];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x, a = Label[p];
                if (x + 1 < w && Label[p + 1] != a) { perimeter[a]++; perimeter[Label[p + 1]]++; }
                if (y + 1 < h && Label[p + w] != a) { perimeter[a]++; perimeter[Label[p + w]]++; }
                if (x == 0) perimeter[a]++;
                if (y == 0) perimeter[a]++;
                if (x == w - 1) perimeter[a]++;
                if (y == h - 1) perimeter[a]++;
            }
        }

        var candidate = new bool[count];
        var order = new List<int>();
        for (int r = 0; r < count; r++)
        {
            bool tiny = Area[r] < minArea;
            bool thin = thinWidth > 0 && Color[r] >= 0 && Area[r] * 2.0 < thinWidth * perimeter[r] && Area[r] < 20000;
            if (tiny || thin) { candidate[r] = true; order.Add(r); }
        }
        var result = new int[n];
        if (order.Count == 0)
        {
            for (int p = 0; p < n; p++) result[p] = Color[Label[p]];
            return result;
        }

        var lists = new List<int>?[count];
        foreach (int r in order) lists[r] = new List<int>(Area[r]);
        for (int p = 0; p < n; p++) lists[Label[p]]?.Add(p);

        var cur = (int[])Label.Clone();
        var area = (int[])Area.Clone();
        var col = (int[])Color.Clone();
        var absorbed = new bool[count];   // true once the region has been merged away
        var grew = new bool[count];       // true if it absorbed others (its shape is no longer what 'perimeter' measured)
        order.Sort((a, b) => Area[a] != Area[b] ? Area[a].CompareTo(Area[b]) : a.CompareTo(b));

        var nb = new Dictionary<int, int>();
        foreach (int s in order)
        {
            if (absorbed[s]) continue;
            bool tiny = area[s] < minArea;
            if (!tiny && grew[s]) continue;
            var list = lists[s]!;
            nb.Clear();
            foreach (int p in list)
            {
                int x = p % w, y = p / w;
                if (x > 0) Count(cur[p - 1]);
                if (x + 1 < w) Count(cur[p + 1]);
                if (y > 0) Count(cur[p - w]);
                if (y + 1 < h) Count(cur[p + w]);
            }
            void Count(int q) { if (q != s) nb[q] = nb.GetValueOrDefault(q) + 1; }
            if (nb.Count == 0) continue;

            if (!tiny)
            {
                // Fringe test: needs two real neighbors and a color lying between theirs.
                if (nb.Count < 2) continue;
                var top = nb.OrderByDescending(kv => kv.Value).Take(2).ToArray();
                if (col[top[0].Key] < 0 || col[top[1].Key] < 0) continue;
                if (!palette.IsBetween(col[s], col[top[0].Key], col[top[1].Key])) continue;
            }

            int best = -1;
            double bestDist = double.MaxValue;
            int bestBorder = -1;
            foreach (var (q, border) in nb)
            {
                double d = palette.Distance(col[s], col[q]);
                if (d < bestDist - 1e-9 || (Math.Abs(d - bestDist) <= 1e-9 && border > bestBorder)) { best = q; bestDist = d; bestBorder = border; }
            }

            foreach (int p in list) cur[p] = best;
            area[best] += area[s];
            grew[best] = true;
            if (lists[best] is { } bl) bl.AddRange(list);
            lists[s] = null;
            absorbed[s] = true;
        }

        for (int p = 0; p < n; p++) result[p] = col[cur[p]];
        return result;
    }
}

