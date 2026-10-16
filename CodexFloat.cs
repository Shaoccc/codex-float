using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class WindowQuota
{
    public double Remaining;
    public long Reset;
    public int Minutes;
}

class Quota
{
    public WindowQuota Five, Week;
    public string Plan = "";
    static Dictionary<string, object> Obj(object v) { return v as Dictionary<string, object>; }
    public static object Get(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v : null; }
    public static Quota Parse(string json)
    {
        var root = Obj(new JavaScriptSerializer().DeserializeObject(json));
        var result = Obj(Get(root, "result")) ?? root;
        var buckets = Obj(Get(result, "rateLimitsByLimitId"));
        var bucket = Obj(Get(buckets, "codex"));
        if (bucket == null) {
            bucket = Obj(Get(result, "rateLimits"));
            var id = Get(bucket, "limitId") as string;
            if (id != null && id != "codex") bucket = null;
        }
        if (bucket == null) throw new Exception("未返回 Codex 账号额度");
        var q = new Quota();
        q.Plan = Convert.ToString(Get(bucket, "planType"));
        foreach (var key in new[] { "primary", "secondary" }) {
            var w = Obj(Get(bucket, key));
            if (w == null || Get(w, "usedPercent") == null || Get(w, "windowDurationMins") == null) continue;
            double used = Convert.ToDouble(w["usedPercent"]);
            if (double.IsNaN(used) || double.IsInfinity(used)) continue;
            var item = new WindowQuota {
                Remaining = Math.Max(0, Math.Min(100, 100 - used)),
                Minutes = Convert.ToInt32(w["windowDurationMins"]),
                Reset = Get(w, "resetsAt") == null ? 0 : Convert.ToInt64(w["resetsAt"])
            };
            if (item.Minutes == 300) q.Five = item;
            if (item.Minutes == 10080) q.Week = item;
        }
        return q;
    }
}

class CodexReader
{
    public static string FindCodex()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
            try { var file = Path.Combine(folder.Trim('"'), "codex.exe"); if (File.Exists(file)) return file; } catch (ArgumentException) { }
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root)) {
            var paths = Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories);
            if (paths.Length > 0) return paths.OrderByDescending(File.GetLastWriteTimeUtc).First();
        }
        throw new Exception("未找到 Codex，请先安装并登录 Codex 桌面版或 CLI");
    }
    static string ReadResponse(Process p, int id)
    {
        string line;
        while ((line = p.StandardOutput.ReadLine()) != null) {
            Dictionary<string, object> msg;
            try { msg = new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>; } catch (ArgumentException) { continue; }
            if (Quota.Get(msg, "id") == null || Convert.ToString(msg["id"]) != id.ToString()) continue;
            if (Quota.Get(msg, "error") != null) throw new Exception("额度查询失败，请检查 Codex 登录或网络后重试");
            return line;
        }
        throw new Exception("Codex 连接已结束，请检查安装与登录状态");
    }
    public static async Task<Quota> Read(CancellationToken token)
    {
        using (var p = new Process()) {
            p.StartInfo = new ProcessStartInfo(FindCodex(), "app-server --listen stdio://") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath()
            };
            p.ErrorDataReceived += delegate { }; // Drain stderr without storing account or diagnostic data.
            p.Start(); p.BeginErrorReadLine();
            try {
                var work = Task.Run(() => {
                    p.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex_float\",\"title\":\"Codex Float\",\"version\":\"1.1.0\"}}}");
                    p.StandardInput.Flush(); ReadResponse(p, 1);
                    p.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                    p.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                    p.StandardInput.Flush();
                    return Quota.Parse(ReadResponse(p, 2));
                });
                var timeout = Task.Delay(25000, token);
                if (await Task.WhenAny(work, timeout) != work) {
                    if (!p.HasExited) p.Kill();
                    try { await work; } catch { }
                    token.ThrowIfCancellationRequested();
                    throw new Exception("查询超时，请检查网络；稍后自动重试");
                }
                return await work;
            } finally {
                if (!p.HasExited) { p.StandardInput.Close(); if (!p.WaitForExit(500)) p.Kill(); }
            }
        }
    }
}

class FloatForm : Form
{
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly CancellationTokenSource cancel = new CancellationTokenSource();
    readonly ToolTip tip = new ToolTip();
    readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexFloat", "settings.json");
    readonly Label status = new Label();
    readonly Button refresh = new Button();
    readonly Button simplify = new Button();
    readonly ToolStripMenuItem compactMenu = new ToolStripMenuItem("简化为圆球");
    readonly ToolStripMenuItem pin = new ToolStripMenuItem("始终置顶");
    Quota quota;
    DateTime lastRead;
    DateTime nextRead;
    bool busy, quitting;
    bool compact, savedCompact;
    Size expandedSize;
    string error = "";
    public bool Smoke;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    public FloatForm()
    {
        Text = "Codex 额度悬浮窗"; FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(310, 226);
        BackColor = Color.FromArgb(23, 28, 37); ForeColor = Color.FromArgb(229, 235, 244);
        Font = new Font("Microsoft YaHei UI", 9); DoubleBuffered = true;
        TopMost = true; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - 28, area.Top + 90);
        LoadSettings();
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示悬浮窗", null, delegate { Show(); Activate(); });
        menu.Items.Add("立即刷新", null, async delegate { await RefreshQuota(); });
        compactMenu.Click += delegate { SetCompact(!compact); }; menu.Items.Add(compactMenu);
        pin.Checked = TopMost; pin.CheckOnClick = true;
        pin.CheckedChanged += delegate { TopMost = pin.Checked; SaveSettings(); }; menu.Items.Add(pin);
        var opacity = new ToolStripMenuItem("透明度");
        foreach (int percent in new[] { 100, 90, 80, 70 }) {
            int value = percent;
            opacity.DropDownItems.Add(percent + "%", null, delegate { Opacity = value / 100.0; SaveSettings(); });
        }
        menu.Items.Add(opacity);
        menu.Items.Add("收起到托盘", null, delegate { Hide(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { quitting = true; Close(); });
        ContextMenuStrip = menu;
        tray.Icon = SystemIcons.Application; tray.Text = "Codex 额度：等待查询";
        tray.ContextMenuStrip = menu; tray.Visible = true;
        tray.DoubleClick += delegate { Show(); Activate(); };
        refresh.Text = "刷新"; refresh.SetBounds(211, 12, 49, 26); StyleButton(refresh);
        refresh.Click += async delegate { await RefreshQuota(); }; Controls.Add(refresh);
        simplify.Text = "简化"; simplify.SetBounds(154, 12, 49, 26); StyleButton(simplify);
        simplify.Click += delegate { SetCompact(true); }; Controls.Add(simplify);
        var hide = new Button(); hide.Text = "－"; hide.SetBounds(266, 12, 29, 26); StyleButton(hide);
        hide.Click += delegate { Hide(); }; tip.SetToolTip(hide, "收起到托盘；右键菜单可退出"); Controls.Add(hide);
        status.SetBounds(18, 193, 276, 23); status.Font = new Font("Microsoft YaHei UI", 8);
        status.ForeColor = Color.FromArgb(142, 154, 173); status.Text = "正在连接本机 Codex…"; Controls.Add(status);
        MouseDown += delegate(object s, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left) {
                var before = Cursor.Position;
                ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
                var after = Cursor.Position;
                if (compact && Math.Abs(after.X - before.X) < SystemInformation.DragSize.Width && Math.Abs(after.Y - before.Y) < SystemInformation.DragSize.Height)
                    SetCompact(false);
                SaveSettings();
            }
        };
        tip.SetToolTip(this, "拖动空白处移动；右键设置置顶、透明度或退出");
        timer.Interval = 1000; timer.Tick += async delegate {
            Invalidate();
            if (!Smoke && !busy && DateTime.UtcNow >= nextRead) await RefreshQuota();
        };
        Shown += async delegate { SetCompact(savedCompact); timer.Start(); if (!Smoke) await RefreshQuota(); };
        FormClosing += delegate(object s, FormClosingEventArgs e) {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            cancel.Cancel(); timer.Stop(); SaveSettings(); tray.Visible = false; tray.Dispose(); tip.Dispose();
        };
    }
    void SetCompact(bool value)
    {
        if (compact != value) {
            if (value) expandedSize = ClientSize;
            compact = value;
            foreach (Control control in Controls) control.Visible = !compact;
            int diameter = (int)Math.Round(68 * DeviceDpi / 96.0);
            ClientSize = compact ? new Size(diameter, diameter) : expandedSize;
            UpdateShape();
            var area = Screen.FromPoint(Location).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }
        compactMenu.Text = compact ? "展开完整窗口" : "简化为圆球";
        compactMenu.Checked = compact;
        UpdateBallTip(); Invalidate(); SaveSettings();
    }
    void UpdateShape()
    {
        Region old = Region;
        if (compact) {
            using (var path = new GraphicsPath()) {
                path.AddEllipse(0, 0, ClientSize.Width, ClientSize.Height);
                Region = new Region(path);
            }
        } else Region = null;
        if (old != null) old.Dispose();
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (compact) UpdateShape();
    }
    void UpdateBallTip()
    {
        tip.SetToolTip(this, compact ? "5 小时剩余：" + Percent(quota == null ? null : quota.Five) + "\n每周剩余：" + Percent(quota == null ? null : quota.Week)
            + "\n" + (error.Length > 0 ? "刷新失败，当前为上次读数" : quota == null ? "正在查询额度" : "更新于 " + lastRead.ToString("HH:mm:ss"))
            + "\n点击展开，拖动移动，右键设置" : "拖动空白处移动；右键设置置顶、透明度或退出");
    }
    void StyleButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
        b.BackColor = Color.FromArgb(36, 44, 57); b.ForeColor = ForeColor; b.Cursor = Cursors.Hand;
        b.TabStop = false;
    }
    public async Task RefreshQuota()
    {
        if (busy) return;
        busy = true; refresh.Enabled = false; status.Text = "正在查询…";
        try {
            var fresh = await CodexReader.Read(cancel.Token);
            if (IsDisposed || cancel.IsCancellationRequested) return;
            quota = fresh; lastRead = DateTime.Now; error = "";
            tray.Text = ("Codex 剩余 | 5h " + Percent(quota.Five) + " | 周 " + Percent(quota.Week));
        } catch (OperationCanceledException) { return; }
        catch (Exception ex) { if (!IsDisposed) error = ex.Message; }
        finally {
            busy = false; nextRead = DateTime.UtcNow.AddMinutes(1);
            if (!IsDisposed && !cancel.IsCancellationRequested) {
                refresh.Enabled = true;
                status.Text = error.Length > 0 ? (quota == null ? "查询失败 · 右键可重试" : "刷新失败 · 保留上次数据 " + lastRead.ToString("HH:mm")) : "更新于 " + lastRead.ToString("HH:mm:ss") + " · 每分钟刷新";
                tip.SetToolTip(status, error.Length > 0 ? error : "数据来自本机 Codex 账号额度接口");
                UpdateBallTip();
                Invalidate();
            }
        }
    }
    static string Percent(WindowQuota w) { return w == null ? "--" : w.Remaining.ToString("0.#") + "%"; }
    static string ResetText(WindowQuota w)
    {
        if (w == null) return "账号未提供此窗口";
        if (w.Reset <= 0) return "未提供重置时间";
        var span = DateTimeOffset.FromUnixTimeSeconds(w.Reset) - DateTimeOffset.UtcNow;
        if (span.TotalSeconds <= 0) return "已到重置时间 · 等待核实";
        return (span.TotalDays >= 1 ? (int)span.TotalDays + "天 " : "") + span.Hours + "小时 " + span.Minutes + "分后重置";
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics;
        float scale = DeviceDpi / 96f; g.ScaleTransform(scale, scale);
        if (compact) { DrawBall(g); return; }
        using (var title = new Font("Segoe UI", 11, FontStyle.Bold))
        using (var white = new SolidBrush(ForeColor)) g.DrawString("CODEX", title, white, 18, 14);
        using (var brush = new SolidBrush(Color.FromArgb(132, 148, 170)))
        using (var small = new Font("Segoe UI", 8)) g.DrawString(quota == null ? "QUOTA" : quota.Plan.ToUpperInvariant(), small, brush, 93, 19);
        DrawRow(g, "5 小时剩余", quota == null ? null : quota.Five, 59);
        DrawRow(g, "每周剩余", quota == null ? null : quota.Week, 125);
    }
    void DrawBall(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawHalf(g, true, quota == null ? null : quota.Five, Color.FromArgb(44, 183, 173), Color.FromArgb(23, 63, 66));
        DrawHalf(g, false, quota == null ? null : quota.Week, Color.FromArgb(139, 109, 213), Color.FromArgb(47, 35, 72));
        using (var line = new Pen(Color.FromArgb(165, 209, 220), 1)) {
            g.DrawLine(line, 1, 34, 67, 34);
            g.DrawEllipse(line, 0.7f, 0.7f, 66.6f, 66.6f);
        }
        if (error.Length > 0) {
            using (var warning = new SolidBrush(Color.FromArgb(255, 191, 76))) g.FillEllipse(warning, 56, 27, 8, 8);
        }
    }
    void DrawHalf(Graphics g, bool upper, WindowQuota w, Color accent, Color background)
    {
        int y = upper ? 0 : 34;
        var state = g.Save();
        using (var circle = new GraphicsPath()) {
            circle.AddEllipse(0.5f, 0.5f, 67, 67);
            g.SetClip(circle);
            g.SetClip(new Rectangle(0, y, 68, 34), CombineMode.Intersect);
            using (var dark = new SolidBrush(background)) g.FillRectangle(dark, 0, y, 68, 34);
            if (w != null) {
                using (var fill = new SolidBrush(accent)) g.FillRectangle(fill, 0, y, (float)(68 * w.Remaining / 100), 34);
            }
        }
        g.Restore(state);
        using (var label = new Font("Segoe UI", 7, FontStyle.Bold))
        using (var value = new Font("Segoe UI", 9, FontStyle.Bold))
        using (var white = new SolidBrush(Color.White))
        using (var shadow = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
        using (var format = new StringFormat { Alignment = StringAlignment.Center }) {
            float labelY = upper ? 4 : 35;
            string text = upper ? "5h" : "周";
            g.DrawString(text, label, shadow, new RectangleF(1, labelY + 1, 68, 13), format);
            g.DrawString(text, label, white, new RectangleF(0, labelY, 68, 13), format);
            string percent = w == null ? "--" : w.Remaining.ToString("0") + "%";
            g.DrawString(percent, value, shadow, new RectangleF(1, labelY + 12, 68, 18), format);
            g.DrawString(percent, value, white, new RectangleF(0, labelY + 11, 68, 18), format);
        }
    }
    void DrawRow(Graphics g, string label, WindowQuota w, int y)
    {
        var color = w == null ? Color.FromArgb(112, 127, 148) : w.Remaining <= 10 ? Color.FromArgb(251, 113, 133) : w.Remaining <= 30 ? Color.FromArgb(251, 191, 76) : Color.FromArgb(71, 213, 173);
        using (var body = new Font("Microsoft YaHei UI", 9))
        using (var number = new Font("Segoe UI", 14, FontStyle.Bold))
        using (var small = new Font("Microsoft YaHei UI", 8))
        using (var text = new SolidBrush(ForeColor))
        using (var muted = new SolidBrush(Color.FromArgb(142, 154, 173)))
        using (var bar = new SolidBrush(color))
        using (var track = new SolidBrush(Color.FromArgb(44, 53, 68))) {
            g.DrawString(label, body, text, 18, y);
            var value = Percent(w); var size = g.MeasureString(value, number);
            g.DrawString(value, number, bar, 293 - size.Width, y - 5);
            g.FillRectangle(track, 19, y + 25, 273, 5);
            if (w != null) g.FillRectangle(bar, 19, y + 25, (float)(273 * w.Remaining / 100), 5);
            g.DrawString(ResetText(w), small, muted, 17, y + 34);
        }
    }
    void LoadSettings()
    {
        try {
            if (!File.Exists(settingsFile)) return;
            var s = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsFile));
            var pos = new Point(Convert.ToInt32(s["x"]), Convert.ToInt32(s["y"]));
            if (Screen.AllScreens.Any(screen => screen.WorkingArea.Contains(new Rectangle(pos, Size)))) Location = pos;
            TopMost = Convert.ToBoolean(s["topmost"]); Opacity = Math.Max(0.7, Math.Min(1, Convert.ToDouble(s["opacity"])));
            savedCompact = s.ContainsKey("compact") && Convert.ToBoolean(s["compact"]);
        } catch { /* An invalid preference file must not prevent startup. */ }
    }
    void SaveSettings()
    {
        if (Smoke) return;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
            File.WriteAllText(settingsFile, new JavaScriptSerializer().Serialize(new { x = Left, y = Top, topmost = TopMost, opacity = Opacity, compact = compact }));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void SmokeCheck(string path)
    {
        Smoke = true;
        savedCompact = false;
        quota = Quota.Parse("{\"rateLimits\":{\"planType\":\"pro\",\"primary\":{\"usedPercent\":27,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":48,\"windowDurationMins\":10080}}}");
        quota.Five.Reset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        quota.Week.Reset = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds();
        status.Text = "界面测试 · 示例数据";
        Show(); Application.DoEvents();
        using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, ClientRectangle); bitmap.Save(path); }
        var fullSize = ClientSize;
        simplify.PerformClick();
        if (!compact || refresh.Visible || Region == null || !Region.IsVisible(ClientSize.Width / 2, ClientSize.Height / 2) || Region.IsVisible(0, 0) || ClientSize.Width != ClientSize.Height) throw new Exception("Compact mode failed");
        using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, ClientRectangle); bitmap.Save(Path.Combine(Path.GetDirectoryName(path), "preview-ball.png")); }
        compactMenu.PerformClick();
        if (compact || !refresh.Visible || Region != null || ClientSize != fullSize) throw new Exception("Expand failed");
        SetCompact(true);
        quota.Five = null; error = "test offline"; Invalidate();
        using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, ClientRectangle); bitmap.Save(Path.Combine(Path.GetDirectoryName(path), "preview-ball-offline.png")); }
        SetCompact(false);
        Hide(); if (Visible) throw new Exception("Hide failed");
        Show(); if (!Visible) throw new Exception("Show failed");
        pin.Checked = false; if (TopMost) throw new Exception("Pin failed");
        pin.Checked = true; if (!TopMost) throw new Exception("Pin failed");
        quitting = true; Close();
    }
}

class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args.Contains("--probe")) {
            try { var q = CodexReader.Read(CancellationToken.None).GetAwaiter().GetResult(); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe.txt"), "5h=" + (q.Five == null ? "unavailable" : q.Five.Remaining.ToString()) + "; weekly=" + (q.Week == null ? "unavailable" : q.Week.Remaining.ToString()) + "; plan=" + q.Plan); return 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe.txt"), ex.Message); return 1; }
        }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--smoke")) { using (var f = new FloatForm()) f.SmokeCheck(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png")); return 0; }
        bool created;
        using (var mutex = new Mutex(true, "Local\\CodexFloat-v1", out created)) {
            if (!created) { MessageBox.Show("悬浮窗已运行，请双击托盘图标显示。", "Codex Float"); return 0; }
            Application.Run(new FloatForm());
        }
        return 0;
    }
}
