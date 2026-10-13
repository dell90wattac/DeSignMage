namespace DeSignMage.Vectorize;

/// <summary>
/// A run of pixel-edge boundary between two regions, from one junction to the next (or a closed loop with no
/// junction). Corner coordinates are integers on the (W+1)×(H+1) grid of pixel corners, y down. Direction is canonical:
/// <see cref="Left"/> is the region on the walker's left (as seen on screen), <see cref="Right"/> on the right;
/// -1 means outside the image. Every boundary between two regions is exactly one chain, so both neighbors
/// share one fitted curve and cannot drift apart.
/// </summary>
internal sealed class Chain
{
    public int PointStart, PointCount;
    public bool Closed;
    public int Left, Right;
    public int StartCorner, EndCorner;
    /// <summary>Direction (0=E,1=S,2=W,3=N) of the first and last unit edge.</summary>
    public int FirstDir, LastDir;
}

internal sealed class BoundaryGraph
{
    public static readonly int[] Dx = [1, 0, -1, 0];
    public static readonly int[] Dy = [0, 1, 0, -1];

    private readonly int _w, _h;
    private readonly int[] _label;
    private readonly byte[] _hb, _vb;       // boundary flags: horizontal edges (W x (H+1)), vertical edges ((W+1) x H)
    private readonly byte[] _hSeen, _vSeen;
    private readonly List<int> _px = [], _py = [];

    public BoundaryGraph(int[] label, int w, int h)
    {
        _w = w; _h = h; _label = label;
        _hb = new byte[w * (h + 1)];
        _vb = new byte[(w + 1) * h];
        _hSeen = new byte[_hb.Length];
        _vSeen = new byte[_vb.Length];

        for (int y = 0; y <= h; y++)
            for (int x = 0; x < w; x++)
                if (Lab(x, y - 1) != Lab(x, y)) _hb[y * w + x] = 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x <= w; x++)
                if (Lab(x - 1, y) != Lab(x, y)) _vb[y * (w + 1) + x] = 1;

        Build();
        PointX = _px.ToArray();
        PointY = _py.ToArray();
        _px.Clear(); _py.Clear();
    }

    public int Width => _w;
    public int Height => _h;
    public List<Chain> Chains { get; } = [];
    public int[] PointX { get; private set; }
    public int[] PointY { get; private set; }

    public int CornerIndex(int x, int y) => y * (_w + 1) + x;

    private int Lab(int x, int y) => (uint)x < (uint)_w && (uint)y < (uint)_h ? _label[y * _w + x] : -1;

    private bool HasEdge(int x, int y, int dir) => dir switch
    {
        0 => x < _w && _hb[y * _w + x] != 0,
        1 => y < _h && _vb[y * (_w + 1) + x] != 0,
        2 => x > 0 && _hb[y * _w + x - 1] != 0,
        _ => y > 0 && _vb[(y - 1) * (_w + 1) + x] != 0,
    };

    private bool IsSeen(int x, int y, int dir) => dir switch
    {
        0 => _hSeen[y * _w + x] != 0,
        1 => _vSeen[y * (_w + 1) + x] != 0,
        2 => _hSeen[y * _w + x - 1] != 0,
        _ => _vSeen[(y - 1) * (_w + 1) + x] != 0,
    };

    private void MarkSeen(int x, int y, int dir)
    {
        switch (dir)
        {
            case 0: _hSeen[y * _w + x] = 1; break;
            case 1: _vSeen[y * (_w + 1) + x] = 1; break;
            case 2: _hSeen[y * _w + x - 1] = 1; break;
            default: _vSeen[(y - 1) * (_w + 1) + x] = 1; break;
        }
    }

    private int Degree(int x, int y)
    {
        int d = 0;
        for (int k = 0; k < 4; k++) if (HasEdge(x, y, k)) d++;
        return d;
    }

    private bool IsJunction(int x, int y) => Degree(x, y) >= 3;

    private void Build()
    {
        // Chains that start at junctions first, then the closed loops that have none.
        for (int y = 0; y <= _h; y++)
        {
            for (int x = 0; x <= _w; x++)
            {
                if (!IsJunction(x, y)) continue;
                for (int d = 0; d < 4; d++)
                    if (HasEdge(x, y, d) && !IsSeen(x, y, d)) Walk(x, y, d, startIsJunction: true);
            }
        }
        for (int y = 0; y < _h + 1; y++)
        {
            for (int x = 0; x <= _w; x++)
            {
                if (x < _w && _hb[y * _w + x] != 0 && _hSeen[y * _w + x] == 0) Walk(x, y, 0, startIsJunction: false);
                if (y < _h && _vb[y * (_w + 1) + x] != 0 && _vSeen[y * (_w + 1) + x] == 0) Walk(x, y, 1, startIsJunction: false);
            }
        }
    }

    private void Walk(int sx, int sy, int dir0, bool startIsJunction)
    {
        var chain = new Chain { PointStart = _px.Count, FirstDir = dir0, StartCorner = CornerIndex(sx, sy) };
        (chain.Left, chain.Right) = SideLabels(sx, sy, dir0);
        int x = sx, y = sy, d = dir0;
        _px.Add(x); _py.Add(y);
        while (true)
        {
            MarkSeen(x, y, d);
            x += Dx[d]; y += Dy[d];
            _px.Add(x); _py.Add(y);
            chain.LastDir = d;
            if (IsJunction(x, y)) break;
            if (!startIsJunction && x == sx && y == sy)
            {
                chain.Closed = true;
                _px.RemoveAt(_px.Count - 1); _py.RemoveAt(_py.Count - 1);   // the loop's last point repeats its first
                break;
            }
            int nd = -1;
            for (int k = 0; k < 4; k++)
                if (k != ((d + 2) & 3) && HasEdge(x, y, k)) { nd = k; break; }
            d = nd;
        }
        chain.PointCount = _px.Count - chain.PointStart;
        chain.EndCorner = chain.Closed ? chain.StartCorner : CornerIndex(x, y);
        Chains.Add(chain);
    }

    /// <summary>Labels on the left and right of the unit edge leaving corner (x,y) in direction <paramref name="dir"/>.</summary>
    private (int Left, int Right) SideLabels(int x, int y, int dir) => dir switch
    {
        0 => (Lab(x, y - 1), Lab(x, y)),
        1 => (Lab(x, y), Lab(x - 1, y)),
        2 => (Lab(x - 1, y), Lab(x - 1, y - 1)),
        _ => (Lab(x - 1, y - 1), Lab(x, y - 1)),
    };

    /// <summary>Chains touching each region (either side), as arrays indexed by region id.</summary>
    public int[][] ChainsByRegion(int regionCount)
    {
        var counts = new int[regionCount];
        foreach (var c in Chains)
        {
            if (c.Left >= 0) counts[c.Left]++;
            if (c.Right >= 0) counts[c.Right]++;
        }
        var result = new int[regionCount][];
        for (int r = 0; r < regionCount; r++) result[r] = new int[counts[r]];
        Array.Clear(counts);
        for (int i = 0; i < Chains.Count; i++)
        {
            var c = Chains[i];
            if (c.Left >= 0) result[c.Left][counts[c.Left]++] = i;
            if (c.Right >= 0) result[c.Right][counts[c.Right]++] = i;
        }
        return result;
    }
}
