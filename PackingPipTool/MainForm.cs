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
/// 扫描根目录下所有订单号子目录，对缺 PIP 的（同时存在 *_scan.{mp4,mkv} + 同前缀非 scan 视频，
/// 且无 *.pip.mp4）依次调用 ffmpeg 合成。
/// 照搬主项目 MainViewModel.CompositePipVideo 的 filter_complex 写法。
/// </summary>
public class MainForm : Form
{
    private readonly TextBox _txtRoot = new();
    private readonly Button _btnBrowseRoot = new();
    private readonly TextBox _txtFfmpeg = new();
    private readonly Button _btnBrowseFfmpeg = new();
    private readonly FlatButton _btnScan = new();
    private readonly FlatButton _btnStart = new();
    private readonly FlatButton _btnStop = new();
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
        MinimumSize = new Size(960, 640);
        Size = new Size(1100, 720);
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
        // 用 WinForms 默认字体（Control.DefaultFont = Segoe UI 9pt），不要在 self-contained 单文件下用 SystemFonts.*，
        // 压缩模式解出的 native DLL 会破坏 GDI+ 字体句柄，导致 Label/Button 文字渲染异常。
        Font = Control.DefaultFont;

        // ===== 顶部：根目录 / ffmpeg / 控制区 三行 =====
        var pnlTop = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 140,
            ColumnCount = 1,
            RowCount = 3,
        };
        pnlTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); // 根目录
        pnlTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); // ffmpeg
        pnlTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); // 控制按钮（加大避免文字被裁）
        pnlTop.Controls.Add(MakePathRow("根目录", _txtRoot, _btnBrowseRoot, "选择订单号父目录（其下是一堆订单号子目录）"), 0, 0);
        pnlTop.Controls.Add(MakePathRow("ffmpeg", _txtFfmpeg, _btnBrowseFfmpeg, "ffmpeg.exe 完整路径（自动探测失败时手选）"), 0, 1);
        pnlTop.Controls.Add(MakeControlRow(), 0, 2);

        // ===== 底部：进度条 + 状态 =====
        var pnlBottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 56,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8, 4, 8, 6),
        };
        pnlBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        pnlBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _lblStatus.Text = "就绪";
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.Padding = new Padding(0, 2, 0, 2);
        _pb.Dock = DockStyle.Fill;
        pnlBottom.Controls.Add(_lblStatus, 0, 0);
        pnlBottom.Controls.Add(_pb, 0, 1);

        // ===== 中间：SplitContainer（左 ListView，右 日志）=====
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
        _lv.Columns.Add("主视频", 260);
        _lv.Columns.Add("扫描视频", 220);
        _lv.Columns.Add("PIP 输出", 240);
        _lv.Columns.Add("状态", 100);
        split.Panel1.Controls.Add(_lv);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.Font = new Font("Consolas", 9);
        _txtLog.BackColor = Color.FromArgb(245, 245, 245);
        split.Panel2.Controls.Add(_txtLog);

        // ===== 组装（Fill 先添加占满，Top/Bottom 压在上面/下面）=====
        Controls.Add(split);
        Controls.Add(pnlTop);
        Controls.Add(pnlBottom);
    }

    /// <summary>构造一行：[标签] [文本框（自动填剩余宽度）] [浏览按钮]</summary>
    private static TableLayoutPanel MakePathRow(string label, TextBox tb, Button btn, string tooltip)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(8, 4, 8, 0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var lbl = new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
        };
        tb.Dock = DockStyle.Fill;
        btn.Text = "浏览...";
        btn.Dock = DockStyle.Fill;
        var tip = new ToolTip();
        tip.SetToolTip(tb, tooltip);

        row.Controls.Add(lbl, 0, 0);
        row.Controls.Add(tb, 1, 0);
        row.Controls.Add(btn, 2, 0);
        return row;
    }

    /// <summary>构造控制行：放弃 TableLayoutPanel + Dock=Fill（与 Label PreferredSize 冲突文字会画到控件外），
    /// 改用普通 Panel + 手动 Location/Size 定位，ClientRectangle 与控件矩形一一对应，文字保证画在按钮内。</summary>
    private TableLayoutPanel MakeControlRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
        };

        const int btnH = 30;
        int y = (56 - btnH) / 2; // pnlTop 第三行高度 56
        int x = 12;
        const int gap = 6;

        // 按钮 1：扫描 80 宽
        _btnScan.Text = "扫描";
        StyleButton(_btnScan);
        _btnScan.Bounds = new Rectangle(x, y, 80, btnH);
        panel.Controls.Add(_btnScan);
        x += 80 + gap;

        // 按钮 2：开始合并 100 宽
        _btnStart.Text = "开始合并";
        StyleButton(_btnStart);
        _btnStart.Bounds = new Rectangle(x, y, 100, btnH);
        panel.Controls.Add(_btnStart);
        x += 100 + gap;

        // 按钮 3：停止 70 宽
        _btnStop.Text = "停止";
        StyleButton(_btnStop);
        _btnStop.Bounds = new Rectangle(x, y, 70, btnH);
        _btnStop.Enabled = false;
        panel.Controls.Add(_btnStop);
        x += 70 + gap * 3;

        // PIP 位置标签 + 下拉
        var lblPos = new Label
        {
            Text = "PIP 位置",
            TextAlign = ContentAlignment.MiddleRight,
            Bounds = new Rectangle(x, y, 70, btnH),
        };
        panel.Controls.Add(lblPos);
        x += 70;

        _cmbPosition.DropDownStyle = ComboBoxStyle.DropDownList;
        if (_cmbPosition.Items.Count == 0)
        {
            _cmbPosition.Items.Add(new ComboItem("左上", "TopLeft"));
            _cmbPosition.Items.Add(new ComboItem("右上", "TopRight"));
            _cmbPosition.Items.Add(new ComboItem("左下", "BottomLeft"));
            _cmbPosition.Items.Add(new ComboItem("右下", "BottomRight"));
        }
        if (_cmbPosition.SelectedIndex < 0) _cmbPosition.SelectedIndex = 1;
        _cmbPosition.Bounds = new Rectangle(x, y, 100, btnH);
        panel.Controls.Add(_cmbPosition);
        x += 100 + gap * 3;

        // PIP 宽度标签 + 输入
        var lblW = new Label
        {
            Text = "PIP 宽度(px)",
            TextAlign = ContentAlignment.MiddleRight,
            Bounds = new Rectangle(x, y, 90, btnH),
        };
        panel.Controls.Add(lblW);
        x += 90;

        if (string.IsNullOrEmpty(_txtPipWidth.Text)) _txtPipWidth.Text = "320";
        _txtPipWidth.Bounds = new Rectangle(x, y, 100, btnH);
        panel.Controls.Add(_txtPipWidth);

        row.Controls.Add(panel, 0, 0);
        return row;
    }

    /// <summary>Label-based FlatButton：只设字体 + 文字居中，Label 没 FlatStyle/AutoSizeMode 属性</summary>
    private static void StyleButton(Control btn)
    {
        btn.Font = Control.DefaultFont;
        if (btn is Label lbl) lbl.TextAlign = ContentAlignment.MiddleCenter;
    }

    /// <summary>
    /// 标签式按钮：继承 Label（Label 的 base.OnPaint 用 GDI TextRenderer 画文字，已验证在
    /// self-contained 单文件下能正常显示），手动画背景/边框/hover/按下效果。
    /// 完全绕开 Button 控件的 GDI+ Graphics.DrawString 路径（self-contained 下字体缓存损坏导致空白）。
    /// </summary>
    private sealed class FlatButton : Label
    {
        private bool _hover;
        private bool _down;

        public FlatButton()
        {
            Font = Control.DefaultFont;
            // Label 默认 AutoSize=true（按文字大小自调），与 Dock=Fill 冲突会导致文字画到控件外。
            AutoSize = false;
            TextAlign = ContentAlignment.MiddleCenter;
            BackColor = SystemColors.Control;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { _down = false; Invalidate(); } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 背景（按下深一档，hover 浅一档，禁用灰色）
            Color bg;
            if (!Enabled) bg = SystemColors.Control;
            else if (_down) bg = SystemColors.ControlDark;
            else if (_hover) bg = SystemColors.ControlLight;
            else bg = SystemColors.Control;
            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, ClientRectangle);

            // Label 的 base.OnPaint 用 GDI TextRenderer 画文字（正常显示）
            base.OnPaint(e);

            // 边框
            var border = Enabled ? SystemColors.ControlDark : SystemColors.ControlLight;
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, border, ButtonBorderStyle.Solid);
        }
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
        int dirsScanned = 0, dirsWithVideo = 0;
        // 在子线程里 log：要先抓到 InvokeRequired 状态。这里只把消息通过回调回主线程。
        // 简化：ScanAsync 直接在 UI 线程外跑，主线程 poll 不阻塞（用 Task.Run 后 log 通过 BeginInvoke）
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            dirsScanned++;
            try
            {
                // 1. 枚举所有 mp4/mkv，按是否含 "_scan" 分类
                var scanFiles = new List<string>();
                var mainFiles = new List<string>();
                foreach (string ext in new[] { ".mp4", ".mkv" })
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*" + ext, SearchOption.TopDirectoryOnly))
                    {
                        string n = Path.GetFileName(f);
                        if (n.Contains("_scan", StringComparison.OrdinalIgnoreCase))
                            scanFiles.Add(f);
                        else
                            mainFiles.Add(f);
                    }
                }

                if (scanFiles.Count == 0 && mainFiles.Count == 0)
                    continue; // 静默跳过无视频目录
                dirsWithVideo++;
                Log($"[{Path.GetFileName(dir)}] scan={scanFiles.Count} main={mainFiles.Count}");

                // 2. 每个 scan 驱动，找同 prefix 的非 scan 视频配对
                foreach (string scan in scanFiles)
                {
                    string scanName = Path.GetFileNameWithoutExtension(scan);
                    int idx = scanName.LastIndexOf("_scan", StringComparison.OrdinalIgnoreCase);
                    if (idx <= 0) continue;
                    string prefix = scanName.Substring(0, idx);

                    string? main = null;
                    foreach (string m in mainFiles)
                    {
                        string mName = Path.GetFileNameWithoutExtension(m);
                        // 主视频名 = "{prefix}_..."（如 "{prefix}_发货"、"{prefix}_退货"）
                        if (mName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) ||
                            mName.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            main = m;
                            break;
                        }
                    }
                    if (main == null)
                    {
                        Log($"  无配对主视频：{Path.GetFileName(scan)} (prefix={prefix})");
                        continue;
                    }

                    string pip = Path.Combine(dir, Path.GetFileNameWithoutExtension(main) + ".pip.mp4");
                    if (File.Exists(pip))
                    {
                        Log($"  已存在 PIP，跳过：{Path.GetFileName(pip)}");
                        continue;
                    }

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
        Log($"扫描统计：共扫描 {dirsScanned} 个子目录，含视频的 {dirsWithVideo} 个，待合并 {found.Count} 个");
        return found;
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

            // 与主项目略有差异：加 setpts=PTS-STARTPTS 让两个流都从 PTS=0 开始对齐，
            // 避免历史文件录制起点不一致导致 overlay 时间轴错位（主项目 RebuildMissingPipVideosAsync
            // 也有这个问题，但用户场景大多在主程序刚录完时调用，差异不显）。
            string args = $"-hide_banner -loglevel error -y -i \"{mainVideo}\" -i \"{scanVideo}\" " +
                $"-filter_complex \"[0:v]setpts=PTS-STARTPTS[main];[1:v]setpts=PTS-STARTPTS,scale={scaleExpr}[bg];[main][bg]overlay={overlay}\" " +
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