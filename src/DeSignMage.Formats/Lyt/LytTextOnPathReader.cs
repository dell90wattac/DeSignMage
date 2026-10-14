using System.Buffers.Binary;

namespace DeSignMage.Formats.Lyt;

/// <summary>
/// Finds the text-on-a-path effect record inside a text record's body. The record is a 2-byte effect type (9), a payload version byte (2), a 2-byte flags
/// word (bit 0 = on), a 4-byte node count (2 to 49), that many 28-byte nodes in the same layout as outline nodes, and a 4-byte curve compensation. It
/// sits after the style blocks, whose exact layout past the fill is not decoded, so the body is searched for a run that has exactly this shape (known
/// node flags, finite coordinates, the first node a start, the last flagged as last, a plausible compensation). Seen in one sample file only.
/// </summary>
internal static class LytTextOnPathReader
{
    private const int NodeSize = 28, HeaderSize = 9;
    private const uint KnownFlags = 0x00A0A4F7;

    public static LytTextOnPath? Find(byte[] d, int start, int end)
    {
        for (int i = Math.Max(start, 0); i + HeaderSize + 2 * NodeSize + 4 <= end; i++)
        {
            if (d[i] != 9 || d[i + 1] != 0 || d[i + 2] != 2) continue;
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i + 3));
            if ((flags & 1) == 0 || flags > 7) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(i + 5));
            if (count < 2 || count > 49) continue;
            int nodesAt = i + HeaderSize, compAt = nodesAt + count * NodeSize;
            if (compAt + 4 > end) continue;

            var nodes = new LytNode[count];
            bool ok = true;
            for (int k = 0; k < count && ok; k++)
            {
                var span = d.AsSpan(nodesAt + k * NodeSize, NodeSize);
                uint nodeFlags = BinaryPrimitives.ReadUInt32LittleEndian(span);
                ok = (nodeFlags & ~KnownFlags) == 0 && nodeFlags != LytNode.FlagMarker && (k > 0 || (nodeFlags & LytNode.FlagMove) != 0);
                if (!ok) break;
                nodes[k] = LytNode.Decode(span);
                ok = float.IsFinite(nodes[k].X) && float.IsFinite(nodes[k].Y) && Math.Abs(nodes[k].X) < 1e6 && Math.Abs(nodes[k].Y) < 1e6 &&
                     float.IsFinite(nodes[k].Cx) && float.IsFinite(nodes[k].Cy) && float.IsFinite(nodes[k].C1X) && float.IsFinite(nodes[k].C1Y);
            }
            if (!ok || (nodes[^1].Flags & LytNode.FlagClose) == 0) continue;

            float comp = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(compAt));
            if (!float.IsFinite(comp) || comp < 0 || comp > 100) continue;
            // The "last node" flag is not a close: take it off so the path stays open.
            for (int k = 0; k < count; k++) nodes[k] = nodes[k] with { Flags = nodes[k].Flags & ~LytNode.FlagClose };
            return new LytTextOnPath(nodes, comp);
        }
        return null;
    }
}