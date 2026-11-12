using System.Buffers.Binary;
using System.Globalization;

namespace MagOps.Infrastructure.Services;

public sealed class DemNoDataException(string message) : InvalidOperationException(message);

public sealed record GeoTiffDemInfo(
    int Width,
    int Height,
    int Epsg,
    double MinLongitude,
    double MinLatitude,
    double MaxLongitude,
    double MaxLatitude,
    double PixelSizeLongitude,
    double PixelSizeLatitude,
    double? NoDataValue);

public sealed class GeoTiffDemSampler
{
    private readonly byte[] _bytes;
    private readonly bool _littleEndian;
    private readonly Dictionary<ushort, TiffEntry> _entries = [];
    private readonly ulong[] _stripOffsets;
    private readonly ulong[] _stripByteCounts;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitsPerSample;
    private readonly int _sampleFormat;
    private readonly int _samplesPerPixel;
    private readonly int _rowsPerStrip;
    private readonly double _tieRasterX;
    private readonly double _tieRasterY;
    private readonly double _tieLongitude;
    private readonly double _tieLatitude;
    private readonly double _pixelSizeLongitude;
    private readonly double _pixelSizeLatitude;
    private readonly int _rasterType;
    private readonly double? _noData;

    public GeoTiffDemSampler(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("请选择地形 GeoTIFF 文件。");
        if (!File.Exists(path))
            throw new FileNotFoundException("未找到地形 GeoTIFF 文件。", path);

        _bytes = File.ReadAllBytes(path);
        ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_bytes));
        if (_bytes.Length < 8)
            throw new InvalidDataException("GeoTIFF 文件过短。 ");

        _littleEndian = _bytes[0] == (byte)'I' && _bytes[1] == (byte)'I';
        bool bigEndian = _bytes[0] == (byte)'M' && _bytes[1] == (byte)'M';
        if (!_littleEndian && !bigEndian)
            throw new InvalidDataException("GeoTIFF 字节序标记无效。 ");
        if (ReadUInt16(2) != 42)
            throw new InvalidDataException("当前仅支持经典 TIFF/GeoTIFF（magic=42），不支持 BigTIFF。 ");

        uint ifdOffset = ReadUInt32(4);
        ReadIfd(ifdOffset);

        _width = checked((int)ReadSingleUnsigned(256, "ImageWidth"));
        _height = checked((int)ReadSingleUnsigned(257, "ImageLength"));
        _bitsPerSample = checked((int)ReadSingleUnsignedOrDefault(258, 8));
        int compression = checked((int)ReadSingleUnsignedOrDefault(259, 1));
        _samplesPerPixel = checked((int)ReadSingleUnsignedOrDefault(277, 1));
        _rowsPerStrip = checked((int)ReadSingleUnsignedOrDefault(278, (ulong)_height));
        _sampleFormat = checked((int)ReadSingleUnsignedOrDefault(339, 1));

        if (compression != 1)
            throw new InvalidDataException(
                $"当前航线 KML 地形采样只支持未压缩 GeoTIFF（Compression=1）；当前 Compression={compression}。请先转换为未压缩 TIFF。 ");
        if (_samplesPerPixel != 1)
            throw new InvalidDataException("当前航线 KML 地形采样只支持单波段 GeoTIFF。 ");
        bool supportedSampleType =
            (_bitsPerSample == 16 && _sampleFormat is 1 or 2) ||
            (_bitsPerSample == 32 && _sampleFormat == 3);
        if (!supportedSampleType)
            throw new InvalidDataException(
                $"当前航线 KML 地形采样支持 UInt16 / Int16 / Float32；当前 BitsPerSample={_bitsPerSample}, SampleFormat={_sampleFormat}。 ");

        _stripOffsets = ReadUnsignedArray(273, "StripOffsets");
        _stripByteCounts = ReadUnsignedArray(279, "StripByteCounts");
        if (_stripOffsets.Length == 0 || _stripOffsets.Length != _stripByteCounts.Length)
            throw new InvalidDataException("GeoTIFF StripOffsets / StripByteCounts 无效。 ");

        double[] pixelScale = ReadDoubleArray(33550, "ModelPixelScaleTag");
        double[] tiePoints = ReadDoubleArray(33922, "ModelTiepointTag");
        if (pixelScale.Length < 2 || tiePoints.Length < 6)
            throw new InvalidDataException("GeoTIFF 缺少有效的 ModelPixelScale / ModelTiepoint。 ");

        _pixelSizeLongitude = Math.Abs(pixelScale[0]);
        _pixelSizeLatitude = Math.Abs(pixelScale[1]);
        if (_pixelSizeLongitude <= 0 || _pixelSizeLatitude <= 0)
            throw new InvalidDataException("GeoTIFF 像元大小无效。 ");

        _tieRasterX = tiePoints[0];
        _tieRasterY = tiePoints[1];
        _tieLongitude = tiePoints[3];
        _tieLatitude = tiePoints[4];

        (int epsg, int rasterType) = ReadGeoKeys();
        if (epsg != 4326)
            throw new InvalidDataException(
                $"当前航线 KML 地形采样要求 WGS84 / EPSG:4326 GeoTIFF；当前 EPSG={epsg}。 ");
        _rasterType = rasterType;
        _noData = TryReadNoData();

        double minLon = RasterToLongitude(0);
        double maxLon = RasterToLongitude(_width);
        double maxLat = RasterToLatitude(0);
        double minLat = RasterToLatitude(_height);
        Info = new GeoTiffDemInfo(
            _width,
            _height,
            epsg,
            Math.Min(minLon, maxLon),
            Math.Min(minLat, maxLat),
            Math.Max(minLon, maxLon),
            Math.Max(minLat, maxLat),
            _pixelSizeLongitude,
            _pixelSizeLatitude,
            _noData);
    }

    public string VerticalDatum { get; private set; } = "未声明";
    public string VerticalUnit { get; private set; } = "未声明";
    public GeoTiffDemInfo Info { get; }
    public string ContentSha256 { get; }

    /// <summary>Stricter opt-in contract for quantitative flight-height derivation.</summary>
    public void ValidateForHeightCalculation()
    {
        if (VerticalUnit != "未声明" && VerticalUnit != "m")
            throw new InvalidDataException("DEM声明的垂直单位不是米，不能静默计算，请先转换。");
        if (_entries.ContainsKey(34264))
            throw new InvalidDataException("离地高度暂不支持含 ModelTransformation 的旋转/仿射 DEM。");
        if (_rasterType is not (1 or 2))
            throw new InvalidDataException("DEM 像元定位类型未知，不能计算离地高度。");
        double[] scale = ReadDoubleArray(33550, "ModelPixelScaleTag");
        double[] tie = ReadDoubleArray(33922, "ModelTiepointTag");
        if (_width < 2 || _height < 2 || tie.Length != 6 || !tie.All(double.IsFinite) ||
            !scale.All(double.IsFinite) || scale[0] <= 0 || scale[1] <= 0 ||
            ReadSingleUnsignedOrDefault(274, 1) != 1)
            throw new InvalidDataException("DEM 的尺寸、定位点、像元方向或比例不符合离地高度采样要求。");
        if (_entries.ContainsKey(42112))
        {
            var metadata = System.Text.Encoding.UTF8.GetString(GetEntryBytes(_entries[42112]));
            if (metadata.Contains("scale", StringComparison.OrdinalIgnoreCase) || metadata.Contains("offset", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DEM 含未支持的比例/偏移元数据，请先转换为米制实际高程。");
        }
    }

    public bool CanSampleBilinear(double longitude, double latitude)
    {
        if (!double.IsFinite(longitude) || !double.IsFinite(latitude))
            return false;

        double rasterX = (longitude - _tieLongitude) / _pixelSizeLongitude + _tieRasterX;
        double rasterY = (_tieLatitude - latitude) / _pixelSizeLatitude + _tieRasterY;
        double x = _rasterType == 2 ? rasterX : rasterX - 0.5;
        double y = _rasterType == 2 ? rasterY : rasterY - 0.5;

        int c0 = (int)Math.Floor(x);
        int r0 = (int)Math.Floor(y);
        int c1 = c0 + 1;
        int r1 = r0 + 1;
        return c0 >= 0 && r0 >= 0 && c1 < _width && r1 < _height;
    }

    public double SampleBilinear(double longitude, double latitude)
    {
        if (!double.IsFinite(longitude) || !double.IsFinite(latitude))
            throw new ArgumentOutOfRangeException(nameof(longitude), "经纬度必须是有限数字。 ");

        double rasterX = (longitude - _tieLongitude) / _pixelSizeLongitude + _tieRasterX;
        double rasterY = (_tieLatitude - latitude) / _pixelSizeLatitude + _tieRasterY;

        // PixelIsArea tie points describe pixel corners; PixelIsPoint describes pixel centres.
        double x = _rasterType == 2 ? rasterX : rasterX - 0.5;
        double y = _rasterType == 2 ? rasterY : rasterY - 0.5;

        int c0 = (int)Math.Floor(x);
        int r0 = (int)Math.Floor(y);
        int c1 = c0 + 1;
        int r1 = r0 + 1;
        if (c0 < 0 || r0 < 0 || c1 >= _width || r1 >= _height)
        {
            throw new InvalidOperationException(
                $"航线点 ({longitude:F6}, {latitude:F6}) 超出 DEM 可双线性采样范围；" +
                $"DEM 覆盖约 {Info.MinLongitude:F6}–{Info.MaxLongitude:F6}°E / " +
                $"{Info.MinLatitude:F6}–{Info.MaxLatitude:F6}°N。 ");
        }

        double dx = x - c0;
        double dy = y - r0;
        double v00 = ReadPixel(c0, r0);
        double v10 = ReadPixel(c1, r0);
        double v01 = ReadPixel(c0, r1);
        double v11 = ReadPixel(c1, r1);

        EnsureValidElevation(v00, longitude, latitude);
        EnsureValidElevation(v10, longitude, latitude);
        EnsureValidElevation(v01, longitude, latitude);
        EnsureValidElevation(v11, longitude, latitude);

        return
            v00 * (1 - dx) * (1 - dy) +
            v10 * dx * (1 - dy) +
            v01 * (1 - dx) * dy +
            v11 * dx * dy;
    }

    internal (double Lon, double Lat) PixelCenter(int column, int row) =>
        (RasterToLongitude(column + (_rasterType == 2 ? 0 : 0.5)), RasterToLatitude(row + (_rasterType == 2 ? 0 : 0.5)));
    internal bool HasPixelCenter(double lon, double lat)
    {
        var origin = PixelCenter(0, 0);
        double col = (lon-origin.Lon)/_pixelSizeLongitude, row = (origin.Lat-lat)/_pixelSizeLatitude;
        return double.IsFinite(col) && double.IsFinite(row) && Math.Abs(col-Math.Round(col))<1e-6 &&
            Math.Abs(row-Math.Round(row))<1e-6 && Math.Round(col)>=0 && Math.Round(col)<_width && Math.Round(row)>=0 && Math.Round(row)<_height;
    }
    internal double ReadPixelCenter(double lon, double lat)
    {
        if(!HasPixelCenter(lon,lat))throw new InvalidOperationException("目标不是当前瓦片内的像元中心。");
        var origin=PixelCenter(0,0);
        double value=ReadPixel((int)Math.Round((lon-origin.Lon)/_pixelSizeLongitude),(int)Math.Round((origin.Lat-lat)/_pixelSizeLatitude));
        EnsureValidElevation(value,lon,lat);return value;
    }

    private double RasterToLongitude(double rasterX) =>
        _tieLongitude + (rasterX - _tieRasterX) * _pixelSizeLongitude;

    private double RasterToLatitude(double rasterY) =>
        _tieLatitude - (rasterY - _tieRasterY) * _pixelSizeLatitude;

    private void EnsureValidElevation(double value, double longitude, double latitude)
    {
        if (!double.IsFinite(value) || (_noData.HasValue && Math.Abs(value - _noData.Value) < 1e-9))
        {
            throw new DemNoDataException(
                $"航线点 ({longitude:F6}, {latitude:F6}) 命中 DEM NoData。 ");
        }
    }

    private double ReadPixel(int column, int row)
    {
        int stripIndex = row / _rowsPerStrip;
        int rowWithinStrip = row % _rowsPerStrip;
        if (stripIndex < 0 || stripIndex >= _stripOffsets.Length)
            throw new InvalidDataException("GeoTIFF strip 索引越界。 ");

        int bytesPerSample = _bitsPerSample / 8;
        long rowStride = checked((long)_width * _samplesPerPixel * bytesPerSample);
        long offset = checked((long)_stripOffsets[stripIndex] + rowWithinStrip * rowStride + (long)column * bytesPerSample);
        long stripEnd = checked((long)_stripOffsets[stripIndex] + (long)_stripByteCounts[stripIndex]);
        if (offset < 0 || offset + bytesPerSample > stripEnd || offset + bytesPerSample > _bytes.LongLength)
            throw new InvalidDataException("GeoTIFF 像元偏移越界。 ");

        int i = checked((int)offset);
        return (_bitsPerSample, _sampleFormat) switch
        {
            (16, 1) => ReadUInt16(i),
            (16, 2) => ReadInt16(i),
            (32, 3) => ReadSingle(i),
            _ => throw new InvalidDataException("不支持的 GeoTIFF 像元类型。 ")
        };
    }

    private void ReadIfd(uint ifdOffset)
    {
        int offset = checked((int)ifdOffset);
        EnsureRange(offset, 2);
        int count = ReadUInt16(offset);
        int entryOffset = offset + 2;
        for (int i = 0; i < count; i++)
        {
            int p = checked(entryOffset + i * 12);
            EnsureRange(p, 12);
            ushort tag = ReadUInt16(p);
            ushort type = ReadUInt16(p + 2);
            uint valueCount = ReadUInt32(p + 4);
            uint valueOrOffset = ReadUInt32(p + 8);
            _entries[tag] = new TiffEntry(tag, type, valueCount, valueOrOffset, p + 8);
        }
    }

    private ulong ReadSingleUnsigned(ushort tag, string name)
    {
        ulong[] values = ReadUnsignedArray(tag, name);
        if (values.Length == 0)
            throw new InvalidDataException($"GeoTIFF {name} 为空。 ");
        return values[0];
    }

    private ulong ReadSingleUnsignedOrDefault(ushort tag, ulong fallback) =>
        _entries.ContainsKey(tag) ? ReadSingleUnsigned(tag, tag.ToString(CultureInfo.InvariantCulture)) : fallback;

    private ulong[] ReadUnsignedArray(ushort tag, string name)
    {
        if (!_entries.TryGetValue(tag, out TiffEntry? entry) || entry is null)
            throw new InvalidDataException($"GeoTIFF 缺少 {name} (tag {tag})。 ");
        ReadOnlySpan<byte> raw = GetEntryBytes(entry);
        int count = checked((int)entry.Count);
        var result = new ulong[count];
        switch (entry.Type)
        {
            case 3: // SHORT
                for (int i = 0; i < count; i++) result[i] = ReadUInt16(raw, i * 2);
                break;
            case 4: // LONG
                for (int i = 0; i < count; i++) result[i] = ReadUInt32(raw, i * 4);
                break;
            default:
                throw new InvalidDataException($"GeoTIFF {name} 使用了不支持的 TIFF 类型 {entry.Type}。 ");
        }
        return result;
    }

    private double[] ReadDoubleArray(ushort tag, string name)
    {
        if (!_entries.TryGetValue(tag, out TiffEntry? entry) || entry is null || entry.Type != 12)
            throw new InvalidDataException($"GeoTIFF 缺少 DOUBLE 类型的 {name}。 ");
        ReadOnlySpan<byte> raw = GetEntryBytes(entry);
        int count = checked((int)entry.Count);
        var result = new double[count];
        for (int i = 0; i < count; i++) result[i] = ReadDouble(raw, i * 8);
        return result;
    }

    private (int Epsg, int RasterType) ReadGeoKeys()
    {
        if (!_entries.TryGetValue(34735, out TiffEntry? entry) || entry is null || entry.Type != 3)
            throw new InvalidDataException("GeoTIFF 缺少 GeoKeyDirectoryTag。 ");

        ReadOnlySpan<byte> raw = GetEntryBytes(entry);
        int count = checked((int)entry.Count);
        var values = new ushort[count];
        for (int i = 0; i < count; i++) values[i] = ReadUInt16(raw, i * 2);
        if (values.Length < 4)
            throw new InvalidDataException("GeoTIFF GeoKeyDirectoryTag 无效。 ");

        int keyCount = values[3];
        int epsg = 0;
        int rasterType = 1;
        for (int i = 0; i < keyCount; i++)
        {
            int p = 4 + i * 4;
            if (p + 3 >= values.Length) break;
            ushort keyId = values[p];
            ushort location = values[p + 1];
            ushort valueCount = values[p + 2];
            ushort valueOffset = values[p + 3];
            if (location != 0 || valueCount != 1) continue;
            if (keyId == 4096) VerticalDatum = "EPSG:" + valueOffset;
            if (keyId == 4099) VerticalUnit = valueOffset == 9001 ? "m" : "EPSG-unit:" + valueOffset;
            if (keyId == 2048) epsg = valueOffset;       // GeographicTypeGeoKey
            if (keyId == 1025) rasterType = valueOffset; // GTRasterTypeGeoKey
        }
        if (epsg == 0)
            throw new InvalidDataException("GeoTIFF 未声明 GeographicTypeGeoKey / EPSG。 ");
        return (epsg, rasterType);
    }

    private double? TryReadNoData()
    {
        if (!_entries.TryGetValue(42113, out TiffEntry? entry) || entry is null || entry.Type != 2)
            return null;
        string text = ReadAscii(entry).Trim().TrimEnd('\0');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private string ReadAscii(TiffEntry entry)
    {
        ReadOnlySpan<byte> raw = GetEntryBytes(entry);
        return System.Text.Encoding.ASCII.GetString(raw);
    }

    private ReadOnlySpan<byte> GetEntryBytes(TiffEntry entry)
    {
        int typeSize = entry.Type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => throw new InvalidDataException($"不支持的 TIFF field type：{entry.Type}。 ")
        };
        int length = checked((int)entry.Count * typeSize);
        int offset = length <= 4
            ? entry.InlineOffset
            : checked((int)entry.ValueOrOffset);
        EnsureRange(offset, length);
        return _bytes.AsSpan(offset, length);
    }

    private ushort ReadUInt16(int offset)
    {
        EnsureRange(offset, 2);
        return ReadUInt16(_bytes.AsSpan(offset, 2), 0);
    }

    private short ReadInt16(int offset)
    {
        EnsureRange(offset, 2);
        ReadOnlySpan<byte> span = _bytes.AsSpan(offset, 2);
        return _littleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(span)
            : BinaryPrimitives.ReadInt16BigEndian(span);
    }

    private uint ReadUInt32(int offset)
    {
        EnsureRange(offset, 4);
        return ReadUInt32(_bytes.AsSpan(offset, 4), 0);
    }

    private float ReadSingle(int offset)
    {
        EnsureRange(offset, 4);
        uint bits = ReadUInt32(offset);
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    private ushort ReadUInt16(ReadOnlySpan<byte> span, int offset)
    {
        ReadOnlySpan<byte> s = span.Slice(offset, 2);
        return _littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(s)
            : BinaryPrimitives.ReadUInt16BigEndian(s);
    }

    private uint ReadUInt32(ReadOnlySpan<byte> span, int offset)
    {
        ReadOnlySpan<byte> s = span.Slice(offset, 4);
        return _littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(s)
            : BinaryPrimitives.ReadUInt32BigEndian(s);
    }

    private double ReadDouble(ReadOnlySpan<byte> span, int offset)
    {
        ReadOnlySpan<byte> s = span.Slice(offset, 8);
        long bits = _littleEndian
            ? BinaryPrimitives.ReadInt64LittleEndian(s)
            : BinaryPrimitives.ReadInt64BigEndian(s);
        return BitConverter.Int64BitsToDouble(bits);
    }

    private void EnsureRange(int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > _bytes.LongLength)
            throw new InvalidDataException("GeoTIFF 字段偏移越界。 ");
    }

    private sealed record TiffEntry(
        ushort Tag,
        ushort Type,
        uint Count,
        uint ValueOrOffset,
        int InlineOffset);
}
