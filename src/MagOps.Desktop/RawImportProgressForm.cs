using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class RawImportProgressForm : Form
{
    private readonly Label _summary = new();
    private readonly Label _file = new();
    private readonly Label _detail = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _cancel = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _allowClose;

    public CancellationToken CancellationToken => _cts.Token;

    public RawImportProgressForm()
    {
        Text = "原始资料归档";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(620, 230);
        Size = new Size(760, 270);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _summary.Text = "正在准备导入…";
        _summary.AutoSize = true;
        _summary.Font = new Font(Font, FontStyle.Bold);
        _summary.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(_summary, 0, 0);

        _file.AutoSize = false;
        _file.Dock = DockStyle.Fill;
        _file.AutoEllipsis = true;
        _file.Height = 28;
        _file.Margin = new Padding(0, 0, 0, 5);
        layout.Controls.Add(_file, 0, 1);

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 24;
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        layout.Controls.Add(_progress, 0, 2);

        _detail.AutoSize = true;
        _detail.ForeColor = SystemColors.GrayText;
        _detail.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_detail, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoScroll = false
        };
        _cancel.Text = "取消";
        _cancel.AutoSize = true;
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _cancel.Text = "正在取消…";
            _cts.Cancel();
        };
        buttons.Controls.Add(_cancel);
        layout.Controls.Add(buttons, 0, 4);

        Controls.Add(layout);
        FormClosing += (_, e) =>
        {
            if (_allowClose)
                return;
            e.Cancel = true;
            if (!_cts.IsCancellationRequested && _cancel.Enabled)
            {
                _cts.Cancel();
                _cancel.Enabled = false;
                _cancel.Text = "正在取消…";
            }
        };
        DpiChanged += (_, _) => BeginInvoke((Action)(() => UiLayout.Normalize(this)));
        Shown += (_, _) => UiLayout.Normalize(this);
    }

    public void Report(RawImportProgress value)
    {
        if (IsDisposed || Disposing || _allowClose) return;
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => Report(value)));
            return;
        }

        int total = Math.Max(1, value.TotalFiles);
        _summary.Text = value.TotalFiles > 0
            ? $"文件 {Math.Min(value.CurrentIndex, total)} / {total}　{PhaseText(value.Phase)}"
            : PhaseText(value.Phase);
        _file.Text = value.CurrentFile;
        _detail.Text = value.Message;

        if (value.CurrentTotalBytes > 0)
        {
            double ratio = Math.Clamp(
                value.CurrentBytes / (double)value.CurrentTotalBytes,
                0,
                1);
            _progress.Value = (int)Math.Round(ratio * _progress.Maximum);
        }
        else if (value.TotalFiles > 0)
        {
            double ratio = Math.Clamp(
                Math.Max(0, value.CurrentIndex - 1) / (double)value.TotalFiles,
                0,
                1);
            _progress.Value = (int)Math.Round(ratio * _progress.Maximum);
        }
        else
        {
            _progress.Value = 0;
        }
    }

    public void CloseAfterOperation()
    {
        _allowClose = true;
        Close();
    }

    public void MarkFinished(string summary)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => MarkFinished(summary)));
            return;
        }
        _summary.Text = summary;
        _progress.Value = _progress.Maximum;
        _cancel.Enabled = false;
    }

    private static string PhaseText(string phase) => phase switch
    {
        "PREPARING" => "准备",
        "HASHING" => "计算 SHA-256",
        "COPYING" => "复制",
        "VERIFIED" => "校验完成",
        "SKIPPED_DUPLICATE" => "重复文件已跳过",
        "FAILED" => "文件失败",
        "CANCELLED" => "已取消",
        "PARTIAL" => "部分完成",
        "COMPLETED" => "完成",
        _ => phase
    };
}
