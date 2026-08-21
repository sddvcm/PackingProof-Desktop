using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PackingPipTool;

/// <summary>
/// 独立的画中画合并工具。
/// 扫描根目录下所有订单号子目录，对缺 PIP 的（同时存在 *_发货.{mp4,mkv} + *_scan.{mp4,mkv}，
/// 且无 *.pip.mp4）依次调用 ffmpeg 合成。
/// 照搬主项目 MainViewModel.CompositePipVideo 的 filter_complex 写法。
/// </summary>
public class MainForm : Form
{
    private readonly TextBox _txtRoot = new();
    private readonly Button _btnBrowseRoot = new();
    private readonly TextBox _txtFfmpeg = new();
    private readonly Button _btnBrowseFfmpeg = new();
    private readonly Button _btnScan = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly ComboBox _cmbPosition = new();
    private readonly TextBox _txtPipWidth = new();
    private readonly ListView _lv = new();
    private readonly TextBox _txtLog = new();
    private readonly ProgressBar _pb = new();
    private readonly Label _lblStatus = new();

    private readonly List<CompositeItem> _items = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public MainForm()
    {
        Text = "合并画中画工具 v1.0";
        MinimumSize = new Size(900, 600);
        Size = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        BuildUi();
        WireEvents();

        // 默认根目录 = exe 所在目录（用户常把 exe 放在订单号父目录下双击运行）
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        _txtRoot.Text = exeDir;
        AutoDetectFfmpeg();
    }

    // ============= UI =============

    private void BuildUi()
    {
        // 顶部：根目录
        var pnlRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 60,
            ColumnCount = 4,
            Padding = new Padding(8),
            AutoSize = false,
        };
        pnlRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        pnlRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pnlRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        pnlRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        pnlRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        pnlRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var lblRoot = new Label { Text = "根目录", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        _txtRoot.Dock = DockStyle.Fill;
        _btnBrowseRoot.Text = "浏览...";
        _btnBrowseRoot.Dock = DockStyle.Fill;

        // 第二行：ffmpeg 路径
        var pnlFfmpegRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        pnlFfmpegRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        pnlFfmpegRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pnlFfmpegRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        pnlFfmpegRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var lblFfmpeg = new Label { Text = "ffmpeg", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        _txtFfmpeg.Dock = DockStyle.Fill;
        _btnBrowseFfmpeg.Text = "浏览...";
        _btnBrowseFfmpeg.Dock = DockStyle.Fill;
        pnlFfmpegRow.Controls.Add(lblFfmpeg, 0, 0);
        pnlFfmpegRow.Controls.Add(_txtFfmpeg, 1, 0);
        pnlFfmpegRow.SetColumnSpan(_txtFfmpeg, 2); // 与浏览按钮各占一列
        pnlFfmpegRow.Controls.Add(_btnBrowseFfmpeg, 3, 0);

        pnlRoot.Controls.Add(lblRoot, 0, 0);
        pnlRoot.Controls.Add(_txtRoot, 1, 0);
        pnlRoot.SetColumnSpan(_txtRoot, 2);
        pnlRoot.Controls.Add(_btnBrowseRoot, 3, 0);
        pnlRoot.Controls.Add(pnlFfmpegRow, 0, 1);
        pnlRoot.SetColumnSpan(pnlFfmpegRow, 4);

        // 第二块：控制区
        var pnlCtrl = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            ColumnCount = 8,
            Padding = new Padding(8, 4, 8, 4),
        };
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); // 扫描
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); // 开始合并
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); // 停止
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); // 位置
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); // 位置选择
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); // 宽度
        pnlCtrl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _btnScan.Text = "扫描";
        _btnScan.Dock = DockStyle.Fill;
        _btnStart.Text = "开始合并";
        _btnStart.Dock = DockStyle.Fill;
        _btnStop.Text = "停止";
        _btnStop.Dock = DockStyle.Fill;
        _btnStop.Enabled = false;

        var lblPos = new Label { Text = "PIP 位置", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        _cmbPosition.Dock = DockStyle.Fill;
        _cmbPosition.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbPosition.Items.AddRange(new object[]
        {
            new ComboItem("左上", "TopLeft"),
            new ComboItem("右上", "TopRight"),
            new ComboItem("左下", "BottomLeft"),
            new ComboItem("右下", "BottomRight"),
        });
        _cmbPosition.SelectedIndex = 1; // 默认右上

        var lblW = new Label { Text = "PIP 宽度(像素)", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
        _txtPipWidth.Dock = DockStyle.Fill;
        _txtPipWidth.Text = "320";

        pnlCtrl.Controls.Add(_btnScan, 0, 0);
        pnlCtrl.Controls.Add(_btnStart, 1, 0);
        pnlCtrl.Controls.Add(_btnStop, 2, 0);
        pnlCtrl.Controls.Add(lblPos, 4, 0);
        pnlCtrl.Controls.Add(_cmbPosition, 5, 0);
        pnlCtrl.Controls.Add(lblW, 6, 0);
        pnlCtrl.Controls.Add(_txtPipWidth, 7, 0);

        // 底部：进度条 + 状态
        var pnlBottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8, 4, 8, 4),
        };
        pnlBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        pnlBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _lblStatus.Text = "就绪";
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _pb.Dock = DockStyle.Fill;
        pnlBottom.Controls.Add(_lblStatus, 0, 0);
        pnlBottom.Controls.Add(_pb, 0, 1);

        // 中间：ListView + 日志
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 680,
            FixedPanel = FixedPanel.Panel1,
        };

        _lv.View = View.Details;
        _lv.FullRowSelect = true;
        _lv.GridLines = true;
        _lv.Dock = DockStyle.Fill;
        _lv.Columns.Add("订单目录", 160);
        _lv.Columns.Add("主视频", 240);
        _lv.Columns.Add("扫描视频", 220);
        _lv.Columns.Add("PIP 输出", 240);
        _lv.Columns.Add("状态", 120);
        split.Panel1.Controls.Add(_lv);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.Font = new Font("Consolas", 9);
        _txtLog.BackColor = Color.FromArgb(245, 245, 245);
        split.Panel2.Controls.Add(_txtLog);

        // 组装：Fill 在前，Top 在后（dock 顺序）
        Controls.Add(split);
        Controls.Add(pnlCtrl);
        Controls.Add(pnlRoot);
        Controls.Add(pnlBottom);
    }

    private void WireEvents()
    {
        _btnBrowseRoot.Click += (_, _) => BrowseRoot();
        _btnBrowseFfmpeg.Click += (_, _) => BrowseFfmpeg();
        _btnScan.Click += async (_, _) => await ScanAsync();
        _btnStart.Click += async (_, _) => await StartAsync();
        _btnStop.Click += (_, _) => Stop();
        FormClosing += (_, e) =>
        {
            if (_running)
            {
                e.Cancel = true;
                MessageBox.Show("正在合并中，请先点「停止」或等待完成后再关闭。",
                    "合并画中画工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
    }

    private record ComboItem(string Text, string Value)
    {
        public override string ToString() => Text;
    }

    // ============= ffmpeg 自动探测 =============

    private void AutoDetectFfmpeg()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string[] candidates =
        {
            Path.Combine(exeDir, "tools", "ffmpeg.exe"),
            Path.Combine(exeDir, "ffmpeg.exe"),
        };
        foreach (string p in candidates)
        {
            if (File.Exists(p))
            {
                _txtFfmpeg.Text = p;
                return;
            }
        }
        // PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p))
                    {
                        _txtFfmpeg.Text = p;
                        return;
                    }
                }
                catch { }
            }
        }
    }

    private void BrowseRoot()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择订单号父目录（其下是一堆订单号子目录）",
            SelectedPath = Directory.Exists(_txtRoot.Text) ? _txtRoot.Text : AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtRoot.Text = dlg.SelectedPath;
    }

    private void BrowseFfmpeg()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|所有文件|*.*",
        };
        if (!string.IsNullOrEmpty(_txtFfmpeg.Text) && File.Exists(_txtFfmpeg.Text))
            dlg.FileName = _txtFfmpeg.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtFfmpeg.Text = dlg.FileName;
    }

    // ============= 扫描 =============

    private async Task ScanAsync()
    {
        if (_running)
        {
            MessageBox.Show("正在合并中，无法扫描", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string root = _txtRoot.Text.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            MessageBox.Show("根目录无效：" + root, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _btnScan.Enabled = false;
        try
        {
            _lblStatus.Text = "扫描中...";
            var result = await Task.Run(() => ScanRoot(root));
            _items.Clear();
            _items.AddRange(result);
            RefreshList();
            _lblStatus.Text = $"扫描完成：待合并 {_items.Count} 个";
            Log($"扫描完成：根目录={root}，待合并 {_items.Count} 个");
        }
        catch (Exception ex)
        {
            Log("扫描异常：" + ex.Message);
            MessageBox.Show("扫描失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnScan.Enabled = true;
        }
    }

    private List<CompositeItem> ScanRoot(string root)
    {
        var found = new List<CompositeItem>();
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                IEnumerable<string> mainCandidates = Directory.EnumerateFiles(dir, "*_发货.mp4", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(dir, "*_发货.mkv", SearchOption.TopDirectoryOnly));
                foreach (string main in mainCandidates)
                {
                    string prefix = ExtractPrefix(main, "_发货");
                    if (string.IsNullOrEmpty(prefix)) continue;
                    string? scan = FindScan(dir, prefix);
                    if (string.IsNullOrEmpty(scan)) continue;
                    string pip = Path.Combine(dir, Path.GetFileNameWithoutExtension(main) + ".pip.mp4");
                    if (File.Exists(pip)) continue;
                    found.Add(new CompositeItem
                    {
                        OrderDir = Path.GetFileName(dir),
                        MainFile = main,
                        ScanFile = scan,
                        PipFile = pip,
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"扫描目录 {dir} 失败：{ex.Message}");
            }
        }
        return found;
    }

    private static string ExtractPrefix(string file, string mode)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        int idx = name.LastIndexOf("_" + mode, StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? name.Substring(0, idx) : "";
    }

    private static string? FindScan(string dir, string prefix)
    {
        foreach (string ext in new[] { ".mp4", ".mkv" })
        {
            string cand = Path.Combine(dir, prefix + "_scan" + ext);
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    private void RefreshList()
    {
        _lv.BeginUpdate();
        try
        {
            _lv.Items.Clear();
            foreach (var it in _items)
            {
                var li = new ListViewItem(it.OrderDir);
                li.SubItems.Add(Path.GetFileName(it.MainFile));
                li.SubItems.Add(Path.GetFileName(it.ScanFile));
                li.SubItems.Add(Path.GetFileName(it.PipFile));
                li.SubItems.Add(it.Status);
                li.Tag = it;
                _lv.Items.Add(li);
            }
        }
        finally { _lv.EndUpdate(); }
    }

    private void UpdateItemRow(CompositeItem it)
    {
        foreach (ListViewItem li in _lv.Items)
        {
            if (li.Tag == it)
            {
                li.SubItems[4].Text = it.Status;
                return;
            }
        }
    }

    // ============= 合并 =============

    private async Task StartAsync()
    {
        if (_running) return;
        if (_items.Count == 0)
        {
            MessageBox.Show("请先点「扫描」加载待合并项", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string ffmpeg = _txtFfmpeg.Text.Trim();
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
        {
            MessageBox.Show("ffmpeg.exe 无效：" + ffmpeg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!int.TryParse(_txtPipWidth.Text.Trim(), out int pipWidth) || pipWidth < 80 || pipWidth > 7680)
        {
            MessageBox.Show("PIP 宽度需为 80~7680 之间的整数", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string position = ((ComboItem)_cmbPosition.SelectedItem!).Value;
        _running = true;
        _btnStart.Enabled = false;
        _btnScan.Enabled = false;
        _btnStop.Enabled = true;
        _cts = new CancellationTokenSource();

        int total = _items.Count;
        int ok = 0, fail = 0, idx = 0;
        _pb.Minimum = 0;
        _pb.Maximum = total;
        _pb.Value = 0;
        Log($"开始合并：{total} 个，位置={position}，宽度={pipWidth}px");

        try
        {
            foreach (var it in _items)
            {
                if (_cts.IsCancellationRequested) break;
                idx++;
                it.Status = "合并中...";
                UpdateItemRow(it);
                _lblStatus.Text = $"正在合并 ({idx}/{total})：{Path.GetFileName(it.PipFile)}";
                Log($"[{idx}/{total}] {Path.GetFileName(it.MainFile)} + {Path.GetFileName(it.ScanFile)}");

                string? err = null;
                bool success = await Task.Run(() =>
                    CompositePip(ffmpeg, it.MainFile, it.ScanFile, it.PipFile, position, pipWidth, out err),
                    _cts.Token);

                if (success)
                {
                    it.Status = "成功";
                    ok++;
                }
                else
                {
                    it.Status = "失败";
                    it.Error = err;
                    fail++;
                }
                UpdateItemRow(it);
                _pb.Value = idx;
                _lblStatus.Text = $"已合并 {idx}/{total}（成功 {ok}，失败 {fail}）";
            }
        }
        catch (OperationCanceledException)
        {
            Log("用户已停止");
        }
        catch (Exception ex)
        {
            Log("合并异常：" + ex.Message);
        }
        finally
        {
            _running = false;
            _btnStart.Enabled = true;
            _btnScan.Enabled = true;
            _btnStop.Enabled = false;
            _cts?.Dispose();
            _cts = null;
            _lblStatus.Text = $"完成：共 {total}，成功 {ok}，失败 {fail}";
            Log($"合并完成：成功 {ok}，失败 {fail}");
            if (fail > 0)
                MessageBox.Show($"合并完成，但有 {fail} 个失败，详见日志。",
                    "合并画中画工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Stop()
    {
        if (_running) _cts?.Cancel();
    }

    /// <summary>
    /// 照搬主项目 MainViewModel.CompositePipVideo 的 ffmpeg 调用。
    /// filter_complex：[1:v]scale={scaleExpr}[bg];[0:v][bg]overlay={overlay}
    /// scaleExpr 必须纯 args（不带 "scale=" 前缀），filter_complex 模板自带前缀。
    /// </summary>
    private static bool CompositePip(string ffmpegPath, string mainVideo, string scanVideo, string pipFile,
        string position, int pipWidth, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(mainVideo))
            {
                error = $"主视频不存在: {mainVideo}";
                return false;
            }
            if (!File.Exists(scanVideo))
            {
                error = $"扫描视频不存在: {scanVideo}";
                return false;
            }

            int w = Math.Clamp(pipWidth, 80, 7680);
            string scaleExpr = $"{w}:-1"; // 宽度=w，高度按扫描视频宽高比自动
            string overlay = position switch
            {
                "TopLeft" => "x=20:y=20",
                "BottomLeft" => "x=20:y=H-h-20",
                "BottomRight" => "x=W-w-20:y=H-h-20",
                _ => "x=W-w-20:y=20", // TopRight
            };

            string args = $"-hide_banner -loglevel error -y -i \"{mainVideo}\" -i \"{scanVideo}\" " +
                $"-filter_complex \"[1:v]scale={scaleExpr}[bg];[0:v][bg]overlay={overlay}\" " +
                $"-c:v libx264 -preset fast -crf 25 -an \"{pipFile}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                error = "无法启动 ffmpeg 进程";
                return false;
            }
            string stderr = proc.StandardError.ReadToEnd() ?? "";
            bool exited = proc.WaitForExit(300000); // 5min
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                error = "ffmpeg 超时（>5min），已终止";
                return false;
            }
            if (proc.ExitCode != 0 || !File.Exists(pipFile) || new FileInfo(pipFile).Length == 0)
            {
                error = stderr.Trim();
                if (error.Length > 800) error = error[..800] + "…";
                try { if (File.Exists(pipFile)) File.Delete(pipFile); } catch { }
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ============= 日志 =============

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(msg))); return; }
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
        _txtLog.AppendText(line);
    }
}

internal class CompositeItem
{
    public string OrderDir { get; set; } = "";
    public string MainFile { get; set; } = "";
    public string ScanFile { get; set; } = "";
    public string PipFile { get; set; } = "";
    public string Status { get; set; } = "待合并";
    public string? Error { get; set; }
}