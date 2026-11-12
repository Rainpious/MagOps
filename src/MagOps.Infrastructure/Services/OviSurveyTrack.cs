using System.Buffers.Binary;
using MagOps.Core.Routes;

namespace MagOps.Infrastructure.Services;

internal static class OviSurveyTrack
{
    // OviO revision 105, single track object. Accept only the layouts verified
    // against the companion OVKML; never scan arbitrary bytes for coordinates.
    internal static List<AccessPoint> Decode(byte[] data)
    {
        InvalidDataException Unsupported() => new("无法读取此 OVOBJ 轨迹布局，文件可能不完整或包含其他对象；可导出 OVKML。");
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        if (data.Length < 0x222 || !data.AsSpan(0, 4).SequenceEqual("OviO"u8) ||
            U32(12) != 105 || U32(24) != data.Length || U32(0x11c) != 8 ||
            U32(0xf8) != data.Length - 0x108 || U32(0x118) != data.Length - 0x148)
            throw Unsupported();

        int nameEnd = 0x211 + data[0x210];
        if (nameEnd + 29 > data.Length || !data.AsSpan(nameEnd, 5).SequenceEqual(new byte[5]))
            throw Unsupported();
        int offset = nameEnd + 5;
        long count = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
        if (count < 2 || count > (data.Length - offset - 24) / 4 + 1) throw Unsupported();
        long latitude = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 8));
        long longitude = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 16));
        offset += 24;
        var points = new List<AccessPoint>((int)count);
        void Add()
        {
            var point = new AccessPoint(longitude / 1e8, latitude / 1e8);
            if (!RouteAccessPlanner.Valid(point)) throw Unsupported();
            points.Add(point);
        }
        Add();
        while (points.Count < count)
        {
            if (offset >= data.Length) throw Unsupported();
            int header = data[offset++];
            int bytes = (header & 63) >> 2;
            if (bytes is not (3 or 4) || bytes > data.Length - offset) throw Unsupported();
            long packed = header & 3;
            for (int i = 0; i < bytes; i++) packed = (packed << 8) | data[offset++];
            int bits = bytes * 4;
            if (((packed >> bits) & 1) != 0) throw Unsupported();
            long dLat = packed >> (bits + 1);
            long dLon = packed & ((1L << bits) - 1);
            latitude += (header & 128) == 0 ? dLat : -dLat;
            longitude += (header & 64) == 0 ? dLon : -dLon;
            Add();
        }
        return points;
    }
}
