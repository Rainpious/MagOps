using System.Security.Cryptography;
using System.Text;
namespace MagOps.Infrastructure.Services;

/// <summary>A pinned tile set. Library additions never change its priority or fingerprint.</summary>
public sealed class TerrainSetSampler
{
    private readonly List<GeoTiffDemSampler> _tiles;
    public GeoTiffDemInfo Info { get; }
    public string ContentSha256 { get; }
    public TerrainSetSampler(string path)
    {
        if(!path.EndsWith(".terrain.json",StringComparison.OrdinalIgnoreCase)) {
            var single=new GeoTiffDemSampler(path);_tiles=[single];Info=single.Info;ContentSha256=single.ContentSha256;return;
        }
        var selection=TerrainLibraryService.Read<TerrainSelection>(path);
        if(selection.Schema!="magops-terrain-selection-1"||selection.Tiles.Count==0)throw new InvalidDataException("地形瓦片集合为空或版本无效。");
        _tiles=ResolveFiles(path,selection).Select(p=>new GeoTiffDemSampler(p)).ToList();
        foreach(var tile in _tiles)tile.ValidateForHeightCalculation();
        if(_tiles.Select(t=>t.VerticalDatum).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)
            throw new InvalidDataException("瓦片声明的垂直基准冲突，不能静默混合。");
        var first=_tiles[0].Info;
        // One interpolation grid cannot combine different resolution, pixel convention or vertical datum.
        if(_tiles.Any(t=>Math.Abs(t.Info.PixelSizeLongitude-first.PixelSizeLongitude)>1e-10||Math.Abs(t.Info.PixelSizeLatitude-first.PixelSizeLatitude)>1e-10))
            throw new InvalidDataException("瓦片分辨率不一致，暂不能跨瓦片插值；请先统一网格。");
        var origins=_tiles.Select(t=>t.PixelCenter(0,0)).ToArray();
        static bool Integral(double value)=>Math.Abs(value-Math.Round(value))<1e-6;
        if(origins.Any(p=>!Integral((p.Lon-origins[0].Lon)/first.PixelSizeLongitude)||!Integral((p.Lat-origins[0].Lat)/first.PixelSizeLatitude)))
            throw new InvalidDataException("瓦片像元网格未对齐，不能静默拼接。");
        if(selection.Tiles.Select(t=>t.HeightDatum).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)throw new InvalidDataException("瓦片高程基准不同，不能静默混合。");
        for(int i=0;i<_tiles.Count;i++)for(int j=i+1;j<_tiles.Count;j++) {
            var a=_tiles[i].Info;var b=_tiles[j].Info;
            if(Math.Min(a.MaxLongitude,b.MaxLongitude)-Math.Max(a.MinLongitude,b.MinLongitude)>first.PixelSizeLongitude*1.1 &&
               Math.Min(a.MaxLatitude,b.MaxLatitude)-Math.Max(a.MinLatitude,b.MinLatitude)>first.PixelSizeLatitude*1.1 && !selection.OverlapOrderConfirmed)
                throw new InvalidDataException("存在重叠地形来源，请明确确认列表优先顺序后再使用。");
        }
        Info=first with{Width=0,Height=0,MinLongitude=_tiles.Min(t=>t.Info.MinLongitude),MaxLongitude=_tiles.Max(t=>t.Info.MaxLongitude),MinLatitude=_tiles.Min(t=>t.Info.MinLatitude),MaxLatitude=_tiles.Max(t=>t.Info.MaxLatitude)};
        ContentSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",selection.Tiles.Select(t=>t.Sha256+":"+t.HeightDatum))+"|"+selection.OverlapOrderConfirmed)));
    }
    internal static IReadOnlyList<string> ResolveFiles(string manifest,TerrainSelection selection)
    {
        if(string.IsNullOrWhiteSpace(selection.PortableDirectory))return selection.Tiles.Select(t=>new TerrainLibraryService().Resolve(t)).ToArray();
        string root=TerrainLibraryService.SafePath(Path.GetDirectoryName(Path.GetFullPath(manifest))!,selection.PortableDirectory);
        return selection.Tiles.Select(t=>{
            string path=TerrainLibraryService.SafePath(root,t.Sha256+Path.GetExtension(t.RelativePath));
            if(!File.Exists(path)||GeneratedArtifactIdentity.Hash(path)!=t.Sha256)throw new InvalidDataException("打包瓦片缺失或内容变化。");return path;
        }).ToArray();
    }
    public void ValidateForHeightCalculation(){foreach(var tile in _tiles)tile.ValidateForHeightCalculation();}
    public bool CanSampleBilinear(double lon,double lat)
    {
        if(_tiles.Count==1)return _tiles[0].CanSampleBilinear(lon,lat);
        return Corners(lon,lat).All(p=>_tiles.Any(t=>t.HasPixelCenter(p.Lon,p.Lat)));
    }
    private (double Lon,double Lat)[] Corners(double lon,double lat)
    {
        var origin=_tiles[0].PixelCenter(0,0);double dx=Info.PixelSizeLongitude,dy=Info.PixelSizeLatitude;
        double x=origin.Lon+Math.Floor((lon-origin.Lon)/dx)*dx;
        double y=origin.Lat+Math.Floor((lat-origin.Lat)/dy)*dy;
        return [(x,y),(x+dx,y),(x,y+dy),(x+dx,y+dy)];
    }
    public double SampleBilinear(double lon,double lat)
    {
        if(_tiles.Count==1)return _tiles[0].SampleBilinear(lon,lat);
        if(!double.IsFinite(lon)||!double.IsFinite(lat))throw new ArgumentException("经纬度必须有限。");
        var corners=Corners(lon,lat);
        double Pixel((double Lon,double Lat) p) {
            bool noData=false;
            foreach(var tile in _tiles)if(tile.HasPixelCenter(p.Lon,p.Lat)) {
                try{return tile.ReadPixelCenter(p.Lon,p.Lat);}catch(DemNoDataException){noData=true;}
            }
            if(noData)throw new DemNoDataException("瓦片集合在采样邻域内均为NoData。");
            throw new InvalidOperationException("瓦片集合存在覆盖缺口，请补充对应瓦片。");
        }
        double x=(lon-corners[0].Lon)/Info.PixelSizeLongitude,y=(lat-corners[0].Lat)/Info.PixelSizeLatitude;
        return Pixel(corners[0])*(1-x)*(1-y)+Pixel(corners[1])*x*(1-y)+Pixel(corners[2])*(1-x)*y+Pixel(corners[3])*x*y;
    }
}
