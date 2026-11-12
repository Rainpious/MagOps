using MagOps.Infrastructure.Services;
namespace MagOps.Desktop;
internal sealed class TerrainTileSelectionDialog : Form
{
    private readonly ListBox _list=new(){Dock=DockStyle.Fill,SelectionMode=SelectionMode.MultiExtended};
    private readonly List<TerrainAsset> _assets;
    public IReadOnlyList<TerrainAsset> SelectedAssets=>_assets;
    public TerrainTileSelectionDialog(IReadOnlyList<TerrainAsset> assets)
    {
        Text="确认候选瓦片及优先顺序";Width=850;Height=450;StartPosition=FormStartPosition.CenterParent;
        _assets=assets.ToList();
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(10)};
        layout.RowStyles.Add(new(SizeType.AutoSize));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label{AutoSize=true,Text="列表从上到下优先；移除不需要的重叠来源。NoData尝试下一项。确认不代表高程基准已核验。"},0,0);
        layout.Controls.Add(_list,0,1);
        var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
        void Add(string text,Action action){var button=new Button{Text=text,AutoSize=true};button.Click+=(_,_)=>action();buttons.Controls.Add(button);}
        Add("上移",()=>MoveSelected(-1));Add("下移",()=>MoveSelected(1));
        Add("移除",()=>{foreach(int i in _list.SelectedIndices.Cast<int>().OrderDescending().ToArray())_assets.RemoveAt(i);RefreshList();});
        Add("确认使用",()=>{if(_assets.Count==0)return;DialogResult=DialogResult.OK;Close();});
        Add("取消",()=>{DialogResult=DialogResult.Cancel;Close();});layout.Controls.Add(buttons,0,2);Controls.Add(layout);RefreshList();
    }
    private void RefreshList(){_list.Items.Clear();foreach(var a in _assets)_list.Items.Add($"{a.OriginalName} | {a.Sha256[..12]} | {a.Info.MinLongitude:F4}–{a.Info.MaxLongitude:F4} E / {a.Info.MinLatitude:F4}–{a.Info.MaxLatitude:F4} N | {a.HeightDatum}");}
    private void MoveSelected(int delta){int i=_list.SelectedIndex,j=i+delta;if(i<0||j<0||j>=_assets.Count)return;(_assets[i],_assets[j])=(_assets[j],_assets[i]);RefreshList();_list.SelectedIndex=j;}
}
