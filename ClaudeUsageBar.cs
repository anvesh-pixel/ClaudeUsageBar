// Claude Usage Bar — shows Claude 5-hour and weekly usage in the Windows taskbar.
// Single file, C# 5, builds with the csc.exe that ships with Windows (.NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Claude Usage Bar")]
[assembly: System.Reflection.AssemblyProduct("Claude Usage Bar")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace ClaudeUsageBar
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\ClaudeUsageBar.SingleInstance", out created))
            {
                if (!created) return;
                try { Native.SetProcessDPIAware(); } catch { }
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
                catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new BarForm());
            }
        }
    }

    // ---------------------------------------------------------------- data

    class Usage
    {
        public double FiveHour = -1, Weekly = -1;
        public DateTime? FiveReset, WeekReset;
        public List<string> Extra = new List<string>();
    }

    class UsageException : Exception
    {
        public bool RateLimited;
        public UsageException(string msg) : base(msg) { }
        public UsageException(string msg, bool rateLimited) : base(msg) { RateLimited = rateLimited; }
    }

    static class UsageClient
    {
        const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

        public static string ConfigDir()
        {
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return dir;
        }

        public static string DataDir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageBar");
            Directory.CreateDirectory(d);
            return d;
        }

        // Reads the OAuth access token Claude Code keeps on disk. Never refreshes it:
        // Claude Code owns the refresh token, and rotating it from here could log Claude Code out.
        static string ReadToken(JavaScriptSerializer js)
        {
            string path = Path.Combine(ConfigDir(), ".credentials.json");
            if (!File.Exists(path)) throw new UsageException("Not signed in: run `claude` and log in with your Claude account.");
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
            var root = js.DeserializeObject(text) as Dictionary<string, object>;
            object o;
            Dictionary<string, object> oauth = null;
            if (root != null && root.TryGetValue("claudeAiOauth", out o)) oauth = o as Dictionary<string, object>;
            if (oauth == null || !oauth.ContainsKey("accessToken"))
                throw new UsageException("No Claude.ai login in Claude Code (API-key logins have no plan usage).");
            if (oauth.TryGetValue("expiresAt", out o) && o != null)
            {
                long ms = Convert.ToInt64(o);
                if (DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.UtcNow)
                    throw new UsageException("Login token expired: open Claude Code once and it will renew it.");
            }
            return Convert.ToString(oauth["accessToken"]);
        }

        public static Usage Fetch()
        {
            var js = new JavaScriptSerializer();
            string token = ReadToken(js);

            var req = (HttpWebRequest)WebRequest.Create(Endpoint);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.Accept = "application/json";
            req.UserAgent = "claude-usage-bar/1.0";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";

            string body;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    body = r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null)
                {
                    int code = (int)hr.StatusCode;
                    if (code == 401) throw new UsageException("Login rejected (401): open Claude Code to renew it.");
                    if (code == 429) throw new UsageException("Rate limited (429): backing off.", true);
                    throw new UsageException("Usage endpoint returned HTTP " + code + ".");
                }
                throw new UsageException("Network error: " + ex.Message);
            }

            // Keep the last response (usage numbers only, no credentials) for troubleshooting.
            try { File.WriteAllText(Path.Combine(DataDir(), "last-response.json"), body); } catch { }

            var data = js.DeserializeObject(body) as Dictionary<string, object>;
            if (data == null) throw new UsageException("Unexpected response from usage endpoint.");
            var u = new Usage();
            ReadWindow(data, "five_hour", out u.FiveHour, out u.FiveReset);
            ReadWindow(data, "seven_day", out u.Weekly, out u.WeekReset);
            if (u.FiveHour < 0 && u.Weekly < 0) throw new UsageException("Response had no five_hour / seven_day data.");

            string[] keys = { "seven_day_opus", "seven_day_sonnet" };
            string[] names = { "Weekly (Opus)", "Weekly (Sonnet)" };
            for (int i = 0; i < keys.Length; i++)
            {
                double p; DateTime? t;
                ReadWindow(data, keys[i], out p, out t);
                if (p >= 0) u.Extra.Add(names[i] + ": " + Fmt.Pct(p) + " used" + (t.HasValue ? " · resets " + Fmt.When(t.Value) : ""));
            }
            return u;
        }

        static void ReadWindow(Dictionary<string, object> data, string key, out double pct, out DateTime? reset)
        {
            pct = -1; reset = null;
            object o;
            if (!data.TryGetValue(key, out o)) return;
            var w = o as Dictionary<string, object>;
            if (w == null) return;
            if (w.TryGetValue("utilization", out o) && o != null) pct = Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);
            if (w.TryGetValue("resets_at", out o) && o is string)
            {
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse((string)o, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out dto))
                    reset = dto.LocalDateTime;
            }
        }
    }

    static class Fmt
    {
        public static string Pct(double p) { return Math.Round(p).ToString("0") + "%"; }

        public static string Until(DateTime t)
        {
            TimeSpan d = t - DateTime.Now;
            if (d.TotalSeconds <= 0) return "now";
            if (d.TotalDays >= 1) return string.Format("{0}d {1}h", (int)d.TotalDays, d.Hours);
            if (d.TotalHours >= 1) return string.Format("{0}h {1}m", (int)d.TotalHours, d.Minutes);
            return string.Format("{0}m", Math.Max(1, (int)Math.Ceiling(d.TotalMinutes)));
        }

        public static string When(DateTime t)
        {
            string time = t.ToShortTimeString();
            string day = t.Date == DateTime.Today ? "" : t.ToString("ddd ");
            return day + time + " (in " + Until(t) + ")";
        }

        public static string Short(DateTime t)
        {
            string day = t.Date == DateTime.Today ? "" : t.ToString("ddd ");
            return day + t.ToShortTimeString() + " · " + Until(t);
        }
    }

    // ---------------------------------------------------------------- Claude Code session status
    // Written by ~/.claude/hooks/usage-bar-status.mjs from Claude Code hooks.

    enum Status { Idle = 0, Working = 1, Done = 2, Input = 3 }

    class StatusInfo
    {
        public Status State;
        public string Project = "";
    }

    static class SessionStatus
    {
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

        public static string Dir() { return Path.Combine(UsageClient.DataDir(), "sessions"); }

        // Highest-priority state across all sessions: needs input > output ready > working.
        public static StatusInfo Read()
        {
            var best = new StatusInfo();
            string dir = Dir();
            if (!Directory.Exists(dir)) return best;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var d = Js.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (d == null) continue;
                    string s = Convert.ToString(d["state"]);
                    long age = now - Convert.ToInt64(d["t"]);
                    Status st = s == "working" ? Status.Working : s == "input" ? Status.Input : s == "done" ? Status.Done : Status.Idle;
                    // Stale entries: crashed/closed sessions that never sent SessionEnd.
                    if (age > 24L * 3600 * 1000) { File.Delete(f); continue; }
                    if (st == Status.Working && age > 30L * 60 * 1000) continue;
                    if (st > best.State)
                    {
                        best.State = st;
                        object p;
                        best.Project = d.TryGetValue("project", out p) ? Convert.ToString(p) : "";
                    }
                }
                catch { }
            }
            return best;
        }

        // "Seen it": clears output-ready markers. Sessions write a new state on their next prompt.
        public static void AcknowledgeDone()
        {
            string dir = Dir();
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var d = Js.DeserializeObject(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (d != null && Convert.ToString(d["state"]) == "done") File.Delete(f);
                }
                catch { }
            }
        }
    }

    // ---------------------------------------------------------------- settings

    static class Settings
    {
        const string Key = @"Software\ClaudeUsageBar";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ClaudeUsageBar";

        public static int Get(string name, int def)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (k == null) return def;
                object v = k.GetValue(name);
                return v is int ? (int)v : def;
            }
        }

        public static void Set(string name, int value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.DWord);
        }

        public static bool StartWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
        }

        public static bool LightTaskbar()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (k == null) return false;
                object v = k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }
    }

    // ---------------------------------------------------------------- menu styling (Windows 11 look)

    class MenuTheme
    {
        public Color Back, Hover, Text, Dim, Sep, Track, Bar, Orange, Red;

        public static MenuTheme For(bool light)
        {
            var t = new MenuTheme();
            if (light)
            {
                t.Back = Color.FromArgb(249, 249, 249); t.Hover = Color.FromArgb(234, 234, 234);
                t.Text = Color.FromArgb(26, 26, 26); t.Dim = Color.FromArgb(110, 110, 110);
                t.Sep = Color.FromArgb(226, 226, 226); t.Track = Color.FromArgb(222, 222, 222);
                t.Bar = Color.FromArgb(0, 103, 192); t.Orange = Color.FromArgb(214, 108, 0); t.Red = Color.FromArgb(205, 32, 38);
            }
            else
            {
                t.Back = Color.FromArgb(44, 44, 44); t.Hover = Color.FromArgb(61, 61, 61);
                t.Text = Color.FromArgb(240, 240, 240); t.Dim = Color.FromArgb(160, 160, 160);
                t.Sep = Color.FromArgb(66, 66, 66); t.Track = Color.FromArgb(72, 72, 72);
                t.Bar = Color.FromArgb(76, 160, 255); t.Orange = Color.FromArgb(255, 164, 72); t.Red = Color.FromArgb(255, 96, 96);
            }
            return t;
        }

        public Color For(double used) { return used >= 95 ? Red : used >= 80 ? Orange : Bar; }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    class ModernRenderer : ToolStripProfessionalRenderer
    {
        public MenuTheme T;
        public UsageHeader Header;
        public ToolStrip HeaderStrip;
        public ToolStripItem Spacer;
        public ModernRenderer(MenuTheme t) { T = t; RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(T.Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            if (Header != null && e.ToolStrip == HeaderStrip && Spacer != null)
            {
                var st = e.Graphics.Save();
                e.Graphics.TranslateTransform(0, Spacer.Bounds.Top);
                Header.Paint(e.Graphics, e.ToolStrip.Width, e.ToolStrip.Font);
                e.Graphics.Restore(st);
            }
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { } // DWM draws the rounded border
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (var path = MenuTheme.Round(r, 4))
            using (var b = new SolidBrush(T.Hover)) e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? T.Text : T.Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(T.Sep)) e.Graphics.DrawLine(p, 0, y, e.Item.Width, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, k = r.Height / 16f;
            using (var p = new Pen(T.Text, 1.6f * k))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                e.Graphics.DrawLines(p, new[] { new PointF(cx - 4.5f * k, cy), new PointF(cx - 1.5f * k, cy + 3 * k), new PointF(cx + 4.5f * k, cy - 3.5f * k) });
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = T.Dim;
            base.OnRenderArrow(e);
        }
    }

    // Non-clickable header: plan usage with progress bars, like the Claude app's usage panel.
    // Painted into a reserved top area of the menu (menu.Padding.Top), not a ToolStrip item,
    // so the menu's item layout (check margins etc.) can't squeeze it.
    class UsageHeader
    {
        public Usage U;
        public bool Remaining;
        public MenuTheme T = MenuTheme.For(false);
        public float S = 1f;

        public int Height { get { return (int)(124 * S); } }

        public void Paint(Graphics g, int width, Font f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float pad = 16 * S, w = width - pad * 2;
            using (var sep = new Pen(T.Sep)) g.DrawLine(sep, 0, Height - 1, width, Height - 1);
            using (var small = new Font(f.FontFamily, f.Size * 0.92f))
            using (var dim = new SolidBrush(T.Dim))
            using (var txt = new SolidBrush(T.Text))
            {
                g.DrawString("Plan usage", small, dim, pad - 2 * S, 12 * S);
                Row(g, f, small, txt, dim, "5-hour limit", U == null ? -1 : U.FiveHour, U == null ? null : U.FiveReset, pad, 40 * S, w);
                Row(g, f, small, txt, dim, "Weekly", U == null ? -1 : U.Weekly, U == null ? null : U.WeekReset, pad, 82 * S, w);
            }
        }

        void Row(Graphics g, Font f, Font small, Brush txt, Brush dim, string name, double used, DateTime? reset,
                 float x, float y, float w)
        {
            g.DrawString(name, f, txt, x - 2 * S, y);
            string pct = used < 0 ? "–" : Remaining ? Fmt.Pct(Math.Max(0, 100 - used)) + " left" : Fmt.Pct(used);
            string right = (reset.HasValue ? Fmt.Short(reset.Value) + "   " : "") + pct;
            SizeF sz = g.MeasureString(right, small);
            g.DrawString(right, small, dim, x + w - sz.Width + 2 * S, y + 1 * S);

            var track = new RectangleF(x, y + 24 * S, w, 4 * S);
            using (var tp = MenuTheme.Round(track, 2 * S))
            using (var tb = new SolidBrush(T.Track)) g.FillPath(tb, tp);
            if (used > 0)
            {
                var fill = new RectangleF(x, track.Y, Math.Max(4 * S, w * (float)Math.Min(100, used) / 100f), track.Height);
                using (var fp = MenuTheme.Round(fill, 2 * S))
                using (var fb = new SolidBrush(T.For(used))) g.FillPath(fb, fp);
            }
        }
    }

    // ---------------------------------------------------------------- widget

    class BarForm : Form
    {
        static readonly int[] IntervalOpts = { 30, 60, 120, 300, 600, 1800 };
        static readonly string[] IntervalNames = { "30 seconds", "1 minute", "2 minutes", "5 minutes", "10 minutes", "30 minutes" };

        readonly ToolTip tip = new ToolTip();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer layoutTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        ToolStripMenuItem everyItem, remainingItem, startupItem;
        Native.WinEventProc foregroundProc;
        IntPtr foregroundHook;

        Usage usage;
        StatusInfo status = new StatusInfo();
        readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer();
        string error;
        DateTime lastUpdate;
        bool fetching, hidden;
        int intervalSec, backoff = 1;
        bool showRemaining;
        string lastRenderKey = "", lastTip = "";

        public BarForm()
        {
            Text = "Claude Usage Bar";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);

            intervalSec = Settings.Get("IntervalSec", 60);
            if (Array.IndexOf(IntervalOpts, intervalSec) < 0) intervalSec = 60;
            showRemaining = Settings.Get("ShowRemaining", 0) == 1;

            tip.ShowAlways = true;
            tip.InitialDelay = 250;
            tip.AutoPopDelay = 30000;
            tip.ReshowDelay = 100;

            BuildMenu();

            layoutTimer.Interval = 500;
            layoutTimer.Tick += delegate
            {
                status = SessionStatus.Read();
                animTimer.Enabled = status.State == Status.Working && !hidden;
                KeepOnTop(); Render(false); UpdateTip();
            };
            animTimer.Interval = 60;
            animTimer.Tick += delegate { Render(false); };
            pollTimer.Tick += delegate { FetchAsync(); };
            ApplyInterval();

            MouseUp += OnMouseUp;
            SystemEvents.UserPreferenceChanged += OnPrefsChanged;
            SystemEvents.DisplaySettingsChanged += OnPrefsChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE
                cp.ExStyle |= 0x80000 | 0x80 | 0x8 | 0x08000000;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            foregroundProc = OnForegroundChanged;
            foregroundHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, foregroundProc, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
            Render(true);
            layoutTimer.Start();
            FetchAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnPrefsChanged;
            SystemEvents.DisplaySettingsChanged -= OnPrefsChanged;
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            base.OnFormClosed(e);
        }

        void OnPrefsChanged(object sender, EventArgs e)
        {
            if (IsHandleCreated) BeginInvoke((Action)(() => Render(true)));
        }

        void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            KeepOnTop();
            Render(false);
        }

        // ------------------------------------------------ menu / input

        readonly UsageHeader header = new UsageHeader();
        ToolStripLabel spacer;

        static void RoundCorners(IntPtr hwnd)
        {
            int pref = 2; // DWMWCP_ROUND (Windows 11; ignored on Windows 10)
            try { Native.DwmSetWindowAttribute(hwnd, 33, ref pref, 4); } catch { }
        }

        static void StyleDropDown(ToolStripDropDownMenu dd, Font font)
        {
            dd.ShowImageMargin = false;
            dd.ShowCheckMargin = true;
            dd.Font = font;
            dd.Padding = new Padding(0, 4, 0, 4);
            dd.DropShadowEnabled = true;
            dd.HandleCreated += delegate { RoundCorners(dd.Handle); };
        }

        void BuildMenu()
        {
            Font font;
            try { font = new Font("Segoe UI Variable Text", 9.75f); if (font.Name != "Segoe UI Variable Text") throw new Exception(); }
            catch { font = new Font("Segoe UI", 9.75f); }

            using (var sg = Graphics.FromHwnd(IntPtr.Zero)) header.S = sg.DpiX / 96f;
            spacer = new ToolStripLabel("");
            spacer.AutoSize = false;
            spacer.Margin = Padding.Empty;
            spacer.Size = new Size(10, header.Height);
            menu.Items.Add(spacer);
            menu.Items.Add(new ToolStripMenuItem("Refresh now", null, delegate { FetchAsync(); }));
            everyItem = new ToolStripMenuItem("Update every");
            for (int i = 0; i < IntervalOpts.Length; i++)
            {
                int secs = IntervalOpts[i];
                var it = new ToolStripMenuItem(IntervalNames[i]);
                it.Tag = secs;
                it.Click += delegate
                {
                    intervalSec = secs;
                    Settings.Set("IntervalSec", secs);
                    ApplyInterval();
                    UpdateTip();
                };
                everyItem.DropDownItems.Add(it);
            }
            menu.Items.Add(everyItem);

            remainingItem = new ToolStripMenuItem("Show what's left instead of used");
            remainingItem.Click += delegate
            {
                showRemaining = !showRemaining;
                Settings.Set("ShowRemaining", showRemaining ? 1 : 0);
                Render(true);
                UpdateTip();
            };
            menu.Items.Add(remainingItem);

            startupItem = new ToolStripMenuItem("Start with Windows");
            startupItem.Click += delegate { Settings.StartWithWindows = !Settings.StartWithWindows; };
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open usage page", null, delegate
            {
                try { Process.Start("https://claude.ai/settings/usage"); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Close(); }));

            StyleDropDown(menu, font);
            StyleDropDown((ToolStripDropDownMenu)everyItem.DropDown, font);
            foreach (ToolStripItem it in menu.Items)
                if (it is ToolStripMenuItem) it.Padding = new Padding(2, 5, 8, 5);
            foreach (ToolStripItem it in everyItem.DropDownItems) it.Padding = new Padding(2, 5, 8, 5);

            menu.Opening += delegate
            {
                foreach (ToolStripMenuItem it in everyItem.DropDownItems) it.Checked = (int)it.Tag == intervalSec;
                remainingItem.Checked = showRemaining;
                startupItem.Checked = Settings.StartWithWindows;

                var theme = MenuTheme.For(Settings.LightTaskbar());
                header.T = theme;
                header.U = usage;
                header.Remaining = showRemaining;
                var r = new ModernRenderer(theme);
                r.Header = header;
                r.HeaderStrip = menu;
                r.Spacer = spacer;
                menu.Renderer = r;
                menu.MinimumSize = new Size((int)(340 * header.S), 0);
                everyItem.DropDown.Renderer = new ModernRenderer(theme);
            };
        }

        void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                SessionStatus.AcknowledgeDone();
                status = SessionStatus.Read();
                Render(true);
                FetchAsync();
            }
            else if (e.Button == MouseButtons.Right)
            {
                Native.SetForegroundWindow(Handle); // so the menu closes when clicking elsewhere
                menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
            }
        }

        void ApplyInterval()
        {
            pollTimer.Stop();
            pollTimer.Interval = Math.Min(intervalSec * backoff, 1800) * 1000;
            pollTimer.Start();
        }

        // ------------------------------------------------ fetching

        void FetchAsync()
        {
            if (fetching) return;
            fetching = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Usage u = null; string err = null; bool rate = false;
                try { u = UsageClient.Fetch(); }
                catch (UsageException ex) { err = ex.Message; rate = ex.RateLimited; }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)(() => OnFetched(u, err, rate))); } catch { }
            });
        }

        void OnFetched(Usage u, string err, bool rateLimited)
        {
            fetching = false;
            if (u != null) { usage = u; error = null; lastUpdate = DateTime.Now; backoff = 1; }
            else { error = err; backoff = rateLimited ? Math.Min(backoff * 2, 16) : 1; }
            ApplyInterval();
            Render(true);
            UpdateTip();
        }

        // ------------------------------------------------ tooltip

        void UpdateTip()
        {
            var lines = new List<string>();
            if (usage != null)
            {
                if (usage.FiveReset.HasValue) lines.Add("5h  " + Fmt.Short(usage.FiveReset.Value));
                if (usage.WeekReset.HasValue) lines.Add("W   " + Fmt.Short(usage.WeekReset.Value));
            }
            string st = status.State == Status.Working ? "Working" : status.State == Status.Input ? "Needs you"
                      : status.State == Status.Done ? "Done" : null;
            if (st != null) lines.Add(st + (status.Project.Length > 0 ? " · " + status.Project : ""));
            if (error != null) lines.Add(error);
            string t = string.Join("\n", lines.ToArray());
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t.Length > 0 ? t : null); }
        }

        // ------------------------------------------------ placement

        void KeepOnTop()
        {
            if (!IsHandleCreated || hidden) return;
            Native.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        }

        // True when something (a fullscreen game/video) covers the taskbar. Probes what is actually
        // on top at the clock: shell flags and foreground-window size both misfire with big
        // transparent overlay windows and taskbar mods.
        bool TaskbarCovered(IntPtr tray, Point probe)
        {
            var p = new Native.POINT { x = probe.X, y = probe.Y };
            IntPtr h = Native.WindowFromPoint(p);
            if (h == IntPtr.Zero) return false;
            IntPtr root = Native.GetAncestor(h, 2); // GA_ROOT
            return root != tray && root != Handle;
        }

        void SetHidden(bool h)
        {
            if (h == hidden) return;
            hidden = h;
            Native.ShowWindow(Handle, h ? 0 : 4); // SW_HIDE / SW_SHOWNOACTIVATE
            if (!h) KeepOnTop();
        }

        // ------------------------------------------------ drawing

        struct Palette { public Color Text, Dim, Sep, Orange, Red, Logo, Working, Input, Done; }

        static Palette GetPalette(bool light)
        {
            var p = new Palette();
            p.Logo = Color.FromArgb(217, 119, 87);
            p.Working = Color.FromArgb(217, 119, 87);                                    // coral, pulsing
            p.Input = light ? Color.FromArgb(222, 160, 0) : Color.FromArgb(255, 200, 40); // amber
            p.Done = light ? Color.FromArgb(22, 163, 74) : Color.FromArgb(74, 222, 128);  // green
            if (light)
            {
                p.Text = Color.FromArgb(26, 26, 26); p.Dim = Color.FromArgb(96, 96, 96); p.Sep = Color.FromArgb(60, 0, 0, 0);
                p.Orange = Color.FromArgb(214, 108, 0); p.Red = Color.FromArgb(205, 32, 38);
            }
            else
            {
                p.Text = Color.FromArgb(245, 245, 245); p.Dim = Color.FromArgb(190, 190, 190); p.Sep = Color.FromArgb(70, 255, 255, 255);
                p.Orange = Color.FromArgb(255, 164, 72); p.Red = Color.FromArgb(255, 96, 96);
            }
            return p;
        }

        Color ValueColor(double used, Palette p)
        {
            if (used < 0) return p.Dim;
            if (used >= 95) return p.Red;
            if (used >= 80) return p.Orange;
            return p.Text;
        }

        string ValueText(double used)
        {
            if (used < 0) return "–";
            return Fmt.Pct(showRemaining ? Math.Max(0, 100 - used) : used);
        }

        static string PickFamily(params string[] names)
        {
            using (var fonts = new InstalledFontCollection())
                foreach (string n in names)
                    foreach (FontFamily f in fonts.Families)
                        if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            return SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        static string labelFamily, valueFamily;

        void Render(bool force)
        {
            if (!IsHandleCreated) return;
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { SetHidden(true); return; }

            Native.RECT tr;
            Native.GetWindowRect(tray, out tr);
            Rectangle tb = Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom);
            Rectangle scr = Screen.FromHandle(tray).Bounds;

            // Right edge = left edge of the notification area (chevron, tray icons, clock).
            int rightEdge = tb.Right - tb.Height * 6;
            Point probe = new Point(tb.Right - tb.Height * 2, tb.Top + tb.Height / 2);
            IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                Native.RECT nr;
                if (Native.GetWindowRect(notify, out nr) && nr.Right > nr.Left && nr.Left > tb.Left + tb.Width / 2)
                {
                    rightEdge = nr.Left;
                    probe = new Point((nr.Left + nr.Right) / 2, (nr.Top + nr.Bottom) / 2);
                }
            }

            bool show = tb.Width > tb.Height
                        && Rectangle.Intersect(tb, scr).Height >= tb.Height - 2   // auto-hidden taskbar
                        && !TaskbarCovered(tray, probe);
            SetHidden(!show);
            if (!show) return;

            bool light = Settings.LightTaskbar();
            // Working pulses: alpha follows a ~1.6 s sine, quantized so idle ticks skip redraws.
            int pulse = 255;
            if (status.State == Status.Working)
            {
                double ph = (Environment.TickCount % 1600) / 1600.0 * Math.PI * 2;
                pulse = (int)(130 + 125 * (0.5 + 0.5 * Math.Sin(ph))) / 8 * 8;
            }
            string key = string.Join("|", tb, rightEdge, light, showRemaining,
                usage == null ? "-" : usage.FiveHour + "/" + usage.Weekly, status.State, pulse);
            if (!force && key == lastRenderKey) return;
            lastRenderKey = key;

            float s;
            using (var sg = Graphics.FromHwnd(IntPtr.Zero)) s = sg.DpiX / 96f;
            Palette p = GetPalette(light);

            if (labelFamily == null)
            {
                labelFamily = PickFamily("Segoe UI Variable Text", "Segoe UI");
                valueFamily = PickFamily("Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");
            }

            double five = usage == null ? -1 : usage.FiveHour;
            double week = usage == null ? -1 : usage.Weekly;
            string l1 = "5h", v1 = ValueText(five), l2 = "W", v2 = ValueText(week);

            using (var fLabel = new Font(labelFamily, 14.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fValue = new Font(valueFamily, 14.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                SizeF sl1, sv1, sl2, sv2;
                using (var mb = new Bitmap(1, 1))
                using (var mg = Graphics.FromImage(mb))
                {
                    mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    sl1 = mg.MeasureString(l1, fLabel, 1000, fmt);
                    sv1 = mg.MeasureString(v1, fValue, 1000, fmt);
                    sl2 = mg.MeasureString(l2, fLabel, 1000, fmt);
                    sv2 = mg.MeasureString(v2, fValue, 1000, fmt);
                }

                float pad = 8 * s, logo = 16 * s, gapLogo = 7 * s, gap = 4 * s, sepGap = 11 * s;
                int H = tb.Height;
                int W = (int)Math.Ceiling(pad + logo + gapLogo + sl1.Width + gap + sv1.Width
                                          + sepGap + 1 * s + sepGap + sl2.Width + gap + sv2.Width + pad);

                using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    // Alpha 1 instead of 0 so the whole strip receives mouse input.
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    float cy = H / 2f;
                    float x = pad;
                    DrawSpark(g, x + logo / 2, cy, logo / 2, p.Logo);
                    x += logo + gapLogo;

                    float ty = cy - sv1.Height / 2f;
                    using (var bDim = new SolidBrush(p.Dim))
                    using (var b1 = new SolidBrush(ValueColor(five, p)))
                    using (var b2 = new SolidBrush(ValueColor(week, p)))
                    using (var sep = new Pen(p.Sep, Math.Max(1f, s)))
                    {
                        g.DrawString(l1, fLabel, bDim, x, ty, fmt); x += sl1.Width + gap;
                        g.DrawString(v1, fValue, b1, x, ty, fmt); x += sv1.Width + sepGap;
                        if (status.State == Status.Idle)
                            g.DrawLine(sep, x, cy - 10 * s, x, cy + 10 * s);
                        else
                        {
                            Color c = status.State == Status.Working ? p.Working
                                    : status.State == Status.Input ? p.Input : p.Done;
                            using (var bar = new Pen(Color.FromArgb(pulse, c), 3.5f * s))
                            {
                                bar.StartCap = LineCap.Round; bar.EndCap = LineCap.Round;
                                g.DrawLine(bar, x, cy - 9 * s, x, cy + 9 * s);
                            }
                        }
                        x += 1 * s + sepGap;
                        g.DrawString(l2, fLabel, bDim, x, ty, fmt); x += sl2.Width + gap;
                        g.DrawString(v2, fValue, b2, x, ty, fmt);
                    }

                    int left = rightEdge - W - (int)(2 * s);
                    Push(bmp, left, tb.Top);
                }
            }
        }

        // Generic eight-ray spark glyph.
        static void DrawSpark(Graphics g, float cx, float cy, float r, Color c)
        {
            float[] len = { 1f, 0.72f, 0.95f, 0.78f, 1f, 0.7f, 0.92f, 0.8f, 0.97f, 0.74f };
            using (var pen = new Pen(c, Math.Max(1.6f, r * 0.24f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                for (int i = 0; i < len.Length; i++)
                {
                    double a = Math.PI * 2 * i / len.Length - Math.PI / 2;
                    float ex = cx + (float)Math.Cos(a) * r * len[i];
                    float ey = cy + (float)Math.Sin(a) * r * len[i];
                    g.DrawLine(pen, cx, cy, ex, ey);
                }
            }
        }

        void Push(Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBmp);
            try
            {
                var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new Native.POINT { x = 0, y = 0 };
                var dst = new Native.POINT { x = x, y = y };
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    // ---------------------------------------------------------------- Win32

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pDst, ref SIZE size,
            IntPtr src, ref POINT pSrc, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    }
}
