// Browser Mini: Wialon əsaslı GPS monitorinq portalları üçün brauzer əvəzi - az RAM tutan masaüstü proqram.
// Heç bir veb mühərriki istifadə etmir - birbaşa Wialon Remote API ilə danışır.
//  - maşınların siyahısı: vəziyyət, sürət, son məlumat, ünvan (canlı yenilənir)
//  - bildirişlər: siyahıya düşür, səs kompüter səsinin 5%-i ilə çalınır (volume.txt)
//  - saat yanında ikon, kiçildəndə ora gizlənir
// server.txt - server ünvanı (ilk girişdə soruşulur), sound.wav - öz bildiriş səsiniz (istəyə görə)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// ---------------------------------------------------------------- Wialon API
class WialonException : Exception
{
    public readonly int Code;
    public WialonException(int code, string reason) : base(Describe(code, reason)) { Code = code; }

    static string Describe(int code, string reason)
    {
        switch (code)
        {
            case 1: return "Sessiya bitib.";
            case 4: return "Yanlış sorğu" + (reason != null ? ": " + reason : ".");
            case 7: return "Bu əməliyyata icazə yoxdur.";
            case 8: return "İstifadəçi adı və ya şifrə yanlışdır.";
            case 1003: return "Çox sayda sorğu, bir az gözləyin.";
            default: return "Server xətası " + code + (reason != null ? ": " + reason : "");
        }
    }
}

class Wialon
{
    public string Host;        // giriş pəncərəsində dəyişə bilər
    public string Sid;
    public long UserId;
    public string UserName;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public Wialon(string host) { Host = host.TrimEnd('/'); }

    public static string ToJson(object o) { return Json.Serialize(o); }

    public object Call(string svc, object prms)
    {
        string body = "svc=" + Uri.EscapeDataString(svc) + "&params=" + Uri.EscapeDataString(Json.Serialize(prms));
        if (Sid != null) body += "&sid=" + Uri.EscapeDataString(Sid);
        return Post(Host + "/wialon/ajax.html", body);
    }

    public object Events()
    {
        return Post(Host + "/avl_evts", "sid=" + Uri.EscapeDataString(Sid));
    }

    // Server şifrə ilə API girişini qadağan edir, ona görə token istifadə olunur
    public void Login(string token)
    {
        Sid = null;
        var r = (Dictionary<string, object>)Call("token/login",
            new Dictionary<string, object> { { "token", token } });
        Sid = Convert.ToString(r["eid"]);
        var u = (Dictionary<string, object>)r["user"];
        UserId = Convert.ToInt64(u["id"]);
        UserName = Convert.ToString(u["nm"]);

        // Xəritə servisi: wialon.js-dəki getBaseGisUrl("render") ilə eyni məntiq
        GisSid = r.ContainsKey("gis_sid") ? Convert.ToString(r["gis_sid"]) : null;
        string baseUrl = r.ContainsKey("base_url") && r["base_url"] is string ? (string)r["base_url"] : Host;
        string render = r.ContainsKey("gis_render") ? r["gis_render"] as string : null;
        GisRender = Host;
        if (!string.IsNullOrEmpty(render))
        {
            int p = baseUrl.IndexOf("//");
            GisRender = render.TrimEnd('/') + "/" + (p >= 0 ? baseUrl.Substring(p + 2) : baseUrl).TrimEnd('/');
        }
        Log.Write("giriş: açarlar=" + string.Join(",", new List<string>(r.Keys).ToArray()) +
                  " gis_render=" + (render ?? "-") + " base_url=" + baseUrl + " gis_sid=" + (GisSid != null ? "var" : "yox"));
    }

    public string GisRender;    // xəritə kafellərinin əsas ünvanı
    public string GisSid;

    // Hərəkət tarixçəsi: müddət ərzində mövqeli mesajlar (enlik, uzunluq, sürət, vaxt)
    public List<TrackPoint> LoadTrack(long unitId, long from, long to)
    {
        var r = (Dictionary<string, object>)Call("messages/load_interval", new Dictionary<string, object>
        {
            { "itemId", unitId }, { "timeFrom", from }, { "timeTo", to },
            { "flags", 0x1 }, { "flagsMask", 0xFF01 },      // yalnız mövqe məlumatı olan mesajlar
            { "loadCount", 100000 }
        });
        var list = new List<TrackPoint>();
        var msgs = r.ContainsKey("messages") ? r["messages"] as object[] : null;
        if (msgs != null)
            foreach (object o in msgs)
            {
                var m = o as Dictionary<string, object>;
                var pos = m != null && m.ContainsKey("pos") ? m["pos"] as Dictionary<string, object> : null;
                if (pos == null) continue;
                list.Add(new TrackPoint
                {
                    Lat = Convert.ToDouble(pos["y"]),
                    Lon = Convert.ToDouble(pos["x"]),
                    Speed = pos.ContainsKey("s") ? Convert.ToInt32(pos["s"]) : 0,
                    Time = m.ContainsKey("t") ? Convert.ToInt64(m["t"]) : 0
                });
            }
        try { Call("messages/unload", new Dictionary<string, object>()); } catch { }   // serverdə yaddaşı boşalt
        return list;
    }

    public void Logout()
    {
        try { Call("core/logout", new Dictionary<string, object>()); } catch { }
        Sid = null;
    }

    // Ünvan (Wialon-un öz GIS servisi). Alınmasa null qaytarır.
    public string Geocode(double lat, double lon)
    {
        string coords = "[{\"lon\":" + lon.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ",\"lat\":" + lat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}]";
        string url = Host + "/gis_geocode?coords=" + Uri.EscapeDataString(coords) + "&flags=1255211008&uid=" + UserId;
        using (var wc = new WebClient { Encoding = Encoding.UTF8 })
        {
            var arr = Json.DeserializeObject(wc.DownloadString(url)) as object[];
            if (arr != null && arr.Length > 0 && arr[0] is string && ((string)arr[0]).Length > 0) return (string)arr[0];
        }
        return null;
    }

    static object Post(string url, string body)
    {
        using (var wc = new WebClient { Encoding = Encoding.UTF8 })
        {
            wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
            object r = Json.DeserializeObject(wc.UploadString(url, body));
            var d = r as Dictionary<string, object>;
            if (d != null && d.ContainsKey("error") && Convert.ToInt32(d["error"]) != 0)
                throw new WialonException(Convert.ToInt32(d["error"]), d.ContainsKey("reason") ? Convert.ToString(d["reason"]) : null);
            return r;
        }
    }
}

// ---------------------------------------------------------------- Diaqnostika jurnalı (browsermini.log, proqramın qovluğunda)
static class Log
{
    static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browsermini.log");

    public static void Write(string line)
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200000) File.Delete(FilePath);
            File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}

// ---------------------------------------------------------------- Yadda saxlanan giriş (Windows DPAPI ilə şifrələnir)
static class Credentials
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrowserMini");
    static readonly string FilePath = Path.Combine(Dir, "login.dat");

    public static bool Load(out string token)
    {
        token = null;
        try
        {
            if (!File.Exists(FilePath)) return false;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            token = Encoding.UTF8.GetString(plain);
            return token.Length > 0;
        }
        catch { return false; }
    }

    public static void Save(string token)
    {
        Directory.CreateDirectory(Dir);
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, data);
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }
}

// ---------------------------------------------------------------- Dizayn ("Lighter" üslubu): rənglər və öz çəkdiyimiz komponentlər
static class Theme
{
    public static readonly Color Surface = Color.FromArgb(244, 244, 245);     // açıq boz fon
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(228, 228, 231);
    public static readonly Color Text = Color.FromArgb(9, 9, 11);             // demək olar qara
    public static readonly Color Muted = Color.FromArgb(113, 113, 122);
    public static readonly Color Soft = Color.FromArgb(244, 244, 245);        // seçilmiş / axtarış fonu
    public static readonly Color Hover = Color.FromArgb(250, 250, 250);
    public static readonly Color Dark = Color.FromArgb(9, 9, 11);
    public static readonly Color Dots = Color.FromArgb(222, 222, 226);
    public static readonly Color Moving = Color.FromArgb(22, 163, 74);
    public static readonly Color Stopped = Color.FromArgb(161, 161, 170);
    public static readonly Color Offline = Color.FromArgb(220, 38, 38);

    public static readonly Font Regular = new Font("Segoe UI", 9.5f);
    public static readonly Font Small = new Font("Segoe UI", 8.5f);
    public static readonly Font SmallBold = new Font("Segoe UI Semibold", 8.5f);
    public static readonly Font Bold = new Font("Segoe UI Semibold", 10.5f);
    public static readonly Font Title = new Font("Segoe UI Semibold", 12f);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var p = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using (var p = Round(r, radius)) using (var pen = new Pen(c)) g.DrawPath(pen, p);
    }

    // Yumşaq kölgə (bir neçə şəffaf qat)
    public static void Shadow(Graphics g, RectangleF r, float radius)
    {
        for (int i = 4; i >= 1; i--)
        {
            var s = new RectangleF(r.X - i + 1, r.Y - i + 3, r.Width + 2 * i - 2, r.Height + 2 * i - 2);
            FillRound(g, Color.FromArgb(5, 0, 0, 0), s, radius + i);
        }
    }

    public static void DrawDots(Graphics g, Rectangle area)
    {
        using (var b = new SolidBrush(Dots))
            for (int y = 10; y < area.Height; y += 22)
                for (int x = 10; x < area.Width; x += 22)
                    g.FillRectangle(b, x, y, 2, 2);
    }

    public static void DrawText(Graphics g, string text, Font f, Color c, Rectangle r, TextFormatFlags extra)
    {
        TextRenderer.DrawText(g, text, f, r, c, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | extra);
    }

    // Kontekst menyuları üçün rənglər
    public class MenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Soft; } }
        public override Color MenuItemBorder { get { return Border; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color ToolStripDropDownBackground { get { return Card; } }
        public override Color ImageMarginGradientBegin { get { return Card; } }
        public override Color ImageMarginGradientMiddle { get { return Card; } }
        public override Color ImageMarginGradientEnd { get { return Card; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color CheckBackground { get { return Soft; } }
        public override Color CheckSelectedBackground { get { return Soft; } }
    }
}

// Nöqtəli naxışlı fon paneli
class DotPanel : Panel
{
    public DotPanel()
    {
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        Theme.DrawDots(e.Graphics, ClientRectangle);
    }
}

// İri yuvarlaq künclü ağ kart (kölgə və nazik çərçivə ilə). İçindəki komponentlər Padding daxilində qalır.
class CardPanel : Panel
{
    public int Radius = 18;

    public CardPanel()
    {
        BackColor = Theme.Surface;
        Padding = new Padding(10);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(4, 2, Width - 9, Height - 9);
        Theme.Shadow(g, r, Radius);
        Theme.FillRound(g, Theme.Card, r, Radius);
        Theme.StrokeRound(g, Theme.Border, r, Radius);
    }
}

// Sürüşdürülə bilən siyahı (maşınlar və bildirişlər üçün), titrəmədən çəkilir.
// ItemWidth > 0 olanda şəbəkə: elementlər bir neçə sütunda düzülür.
class CardList : Control
{
    public readonly List<object> Items = new List<object>();
    public int ItemHeight = 62;
    public int ItemWidth;                       // 0 = tam en (siyahı), > 0 = minimum plitə eni (şəbəkə)
    public string EmptyText = "";
    public Action<Graphics, object, Rectangle, bool, bool> DrawItem;
    public event Action SelectionChanged;
    public event Action ItemActivated;
    readonly VScrollBar bar = new VScrollBar { Dock = DockStyle.Right, Visible = false };
    int selected = -1, hover = -1, cols = 1;

    public CardList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Card;
        Controls.Add(bar);
        bar.ValueChanged += (s, e) => Invalidate();
    }

    public object SelectedItem
    {
        get { return selected >= 0 && selected < Items.Count ? Items[selected] : null; }
        set { selected = value == null ? -1 : Items.IndexOf(value); EnsureVisible(selected); Invalidate(); }
    }

    public void SetItems(IEnumerable<object> items)
    {
        object sel = SelectedItem;
        Items.Clear();
        Items.AddRange(items);
        selected = sel != null ? Items.IndexOf(sel) : -1;
        hover = -1;
        UpdateScroll();
        Invalidate();
    }

    public void SetLayout(int itemHeight, int itemWidth)
    {
        ItemHeight = itemHeight;
        ItemWidth = itemWidth;
        if (bar.Visible) bar.Value = 0;
        UpdateScroll();
        EnsureVisible(selected);
        Invalidate();
    }

    int Offset { get { return bar.Visible ? bar.Value : 0; } }
    int Rows { get { return (Items.Count + cols - 1) / cols; } }
    int MaxOffset { get { return Math.Max(0, Rows * ItemHeight - Height); } }
    int AvailWidth { get { return Width - (bar.Visible ? bar.Width : 0); } }
    int CellWidth { get { return AvailWidth / cols; } }

    int ColsFor(int width) { return ItemWidth > 0 ? Math.Max(1, width / ItemWidth) : 1; }

    void UpdateScroll()
    {
        cols = ColsFor(Width);
        bool need = Rows * ItemHeight > Height;
        if (need) cols = ColsFor(Width - bar.Width);
        int total = Rows * ItemHeight;
        bar.Visible = total > Height;
        bar.Minimum = 0;
        bar.Maximum = Math.Max(0, total - 1);
        bar.LargeChange = Math.Max(1, Height);
        bar.SmallChange = ItemHeight;
        if (bar.Value > MaxOffset) bar.Value = MaxOffset;
    }

    void ScrollTo(int value) { if (bar.Visible) bar.Value = Math.Max(0, Math.Min(MaxOffset, value)); }

    void EnsureVisible(int i)
    {
        if (i < 0) return;
        int y = i / cols * ItemHeight;
        if (y < Offset) ScrollTo(y);
        else if (y + ItemHeight > Offset + Height) ScrollTo(y + ItemHeight - Height);
    }

    int IndexAt(int x, int y)
    {
        int row = (y + Offset) / ItemHeight;
        int col = Math.Min(cols - 1, Math.Max(0, x / Math.Max(1, CellWidth)));
        if (x >= AvailWidth) return -1;
        int i = row * cols + col;
        return y >= 0 && i >= 0 && i < Items.Count ? i : -1;
    }

    protected override void OnResize(EventArgs e) { UpdateScroll(); base.OnResize(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Items.Count == 0)
        {
            Theme.DrawText(g, EmptyText, Theme.Regular, Theme.Muted, new Rectangle(0, 0, AvailWidth, Math.Min(Height, 120)),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        int cw = CellWidth;
        for (int row = Offset / ItemHeight; row < Rows; row++)
        {
            int y = row * ItemHeight - Offset;
            if (y > Height) break;
            for (int c = 0; c < cols; c++)
            {
                int i = row * cols + c;
                if (i >= Items.Count) break;
                var r = new Rectangle(c * cw, y, c == cols - 1 ? AvailWidth - c * cw : cw, ItemHeight);
                if (DrawItem != null) DrawItem(g, Items[i], r, i == selected, i == hover);
            }
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(Offset - e.Delta / 120 * ItemHeight); base.OnMouseWheel(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.X, e.Y);
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        int i = IndexAt(e.X, e.Y);
        if (i >= 0 && i != selected) { selected = i; Invalidate(); if (SelectionChanged != null) SelectionChanged(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (IndexAt(e.X, e.Y) >= 0 && ItemActivated != null) ItemActivated();
        base.OnMouseDoubleClick(e);
    }

    protected override bool IsInputKey(Keys k)
    {
        return k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int i = selected;
        if (e.KeyCode == Keys.Down) i = selected < 0 ? 0 : Math.Min(Items.Count - 1, selected + cols);
        if (e.KeyCode == Keys.Up) i = Math.Max(0, selected - cols);
        if (e.KeyCode == Keys.Right) i = Math.Min(Items.Count - 1, selected + 1);
        if (e.KeyCode == Keys.Left) i = Math.Max(0, selected - 1);
        if (e.KeyCode == Keys.Enter && ItemActivated != null) ItemActivated();
        if (i != selected && i >= 0)
        {
            selected = i;
            EnsureVisible(i);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }
        base.OnKeyDown(e);
    }
}

// Görünüş rejimi seçicisi: boz həb içində 3 ikon (kart / kompakt / şəbəkə)
class ViewSwitch : Control
{
    public int Mode;                    // 0 kart, 1 kompakt, 2 şəbəkə
    public event Action<int> ModeChanged;
    static readonly string[] Tips = { "Kartlar", "Kompakt siyahı", "Şəbəkə" };
    readonly ToolTip tip = new ToolTip();
    int hover = -1;

    public ViewSwitch()
    {
        Size = new Size(108, 32);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
    }

    Rectangle Seg(int i) { int w = (Width - 6) / 3; return new Rectangle(3 + i * w, 3, w, Height - 6); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, Theme.Soft, new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
        for (int i = 0; i < 3; i++)
        {
            Rectangle r = Seg(i);
            if (i == Mode)
            {
                Theme.FillRound(g, Color.FromArgb(14, 0, 0, 0), new RectangleF(r.X, r.Y + 1, r.Width, r.Height), r.Height / 2f);
                Theme.FillRound(g, Color.White, r, r.Height / 2f);
            }
            Color c = i == Mode ? Theme.Text : hover == i ? Theme.Text : Theme.Muted;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var b = new SolidBrush(c))
            using (var p = new Pen(c, 1.6f))
            {
                if (i == 0)       // kart: iki yuvarlaq düzbucaqlı
                {
                    Theme.StrokeRound(g, c, new RectangleF(cx - 7, cy - 7, 14, 6), 2);
                    Theme.StrokeRound(g, c, new RectangleF(cx - 7, cy + 1, 14, 6), 2);
                }
                else if (i == 1)  // kompakt: üç xətt
                {
                    for (int k = -1; k <= 1; k++) g.DrawLine(p, cx - 7, cy + k * 4.5f, cx + 7, cy + k * 4.5f);
                }
                else              // şəbəkə: dörd kvadrat
                {
                    for (int a = 0; a < 2; a++)
                        for (int d = 0; d < 2; d++)
                            Theme.FillRound(g, c, new RectangleF(cx - 7 + a * 8, cy - 7 + d * 8, 6, 6), 1.5f);
                }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < 3; i++) if (Seg(i).Contains(e.Location)) h = i;
        if (h != hover) { hover = h; Invalidate(); tip.SetToolTip(this, h >= 0 ? Tips[h] : ""); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        for (int i = 0; i < 3; i++)
            if (Seg(i).Contains(e.Location) && i != Mode)
            {
                Mode = i;
                Invalidate();
                if (ModeChanged != null) ModeChanged(i);
            }
        base.OnMouseClick(e);
    }
}

// Yuxarı zolaq: loqo | ağ həb içində süzgəclər (sayğaclarla) | qara "Menyu" düyməsi
class HeaderBar : Control
{
    public string UserName = "";
    public string ActiveFilter;                         // null = hamısı
    public event Action<string> FilterClicked;
    public event Action<Point> MenuClicked;
    int total, moving, stopped, offline;
    readonly List<KeyValuePair<Rectangle, string>> hitAreas = new List<KeyValuePair<Rectangle, string>>();
    Rectangle menuRect;
    string hoverKey;

    public HeaderBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 72;
    }

    public void SetCounts(int total, int moving, int stopped, int offline)
    {
        if (this.total == total && this.moving == moving && this.stopped == stopped && this.offline == offline) return;
        this.total = total; this.moving = moving; this.stopped = stopped; this.offline = offline;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Surface);
        Theme.DrawDots(g, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        hitAreas.Clear();

        // Loqo: qara yuvarlaq kvadrat + ad
        var logo = new RectangleF(20, 20, 32, 32);
        Theme.FillRound(g, Theme.Dark, logo, 9);
        using (var pin = new Pen(Color.White, 2.4f))
        {
            g.DrawEllipse(pin, logo.X + 10, logo.Y + 8, 12, 12);
            g.DrawLine(pin, logo.X + 16, logo.Y + 20, logo.X + 16, logo.Y + 25);
        }
        using (var f = new Font("Segoe UI Semibold", 15f))
        {
            TextRenderer.DrawText(g, "Browser", f, new Point(60, 21), Theme.Text, TextFormatFlags.NoPadding);
            int w = TextRenderer.MeasureText(g, "Browser", f, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, "mini", Theme.Regular, new Point(60 + w + 4, 28), Theme.Muted, TextFormatFlags.NoPadding);
        }

        // Ortada ağ həb: süzgəclər
        var segs = new[]
        {
            new { Key = (string)null, Label = "Hamısı", Count = total, Dot = Color.Empty },
            new { Key = "move", Label = "Hərəkətdə", Count = moving, Dot = Theme.Moving },
            new { Key = "stop", Label = "Dayanıb", Count = stopped, Dot = Theme.Stopped },
            new { Key = "off", Label = "Əlaqə yoxdur", Count = offline, Dot = Theme.Offline }
        };
        var widths = new int[segs.Length];
        int sum = 0;
        for (int i = 0; i < segs.Length; i++)
        {
            widths[i] = TextRenderer.MeasureText(g, segs[i].Label + "  " + segs[i].Count, Theme.SmallBold).Width + (segs[i].Dot.IsEmpty ? 24 : 38);
            sum += widths[i];
        }
        int pillW = sum + 12, pillX = Math.Max(230, (Width - pillW) / 2);
        var pill = new RectangleF(pillX, 16, pillW, 40);
        Theme.Shadow(g, pill, 20);
        Theme.FillRound(g, Theme.Card, pill, 20);
        Theme.StrokeRound(g, Theme.Border, pill, 20);
        int x = pillX + 6;
        for (int i = 0; i < segs.Length; i++)
        {
            var r = new Rectangle(x, 22, widths[i], 28);
            string key = segs[i].Key ?? "all";
            bool active = ActiveFilter == segs[i].Key;
            if (active) Theme.FillRound(g, Theme.Dark, r, 14);
            else if (hoverKey == key) Theme.FillRound(g, Theme.Soft, r, 14);
            int tx = r.X + 12;
            if (!segs[i].Dot.IsEmpty)
            {
                using (var b = new SolidBrush(segs[i].Dot)) g.FillEllipse(b, tx, r.Y + 10, 8, 8);
                tx += 14;
            }
            Theme.DrawText(g, segs[i].Label + "  " + segs[i].Count, Theme.SmallBold, active ? Color.White : Theme.Text,
                new Rectangle(tx, r.Y, r.Right - tx, r.Height), TextFormatFlags.VerticalCenter);
            hitAreas.Add(new KeyValuePair<Rectangle, string>(r, key));
            x += widths[i];
        }

        // Sağda qara "Menyu" həbi
        string label = "☰   " + (UserName.Length > 18 ? UserName.Substring(0, 18) + "…" : UserName);
        int mw = TextRenderer.MeasureText(g, label, Theme.SmallBold).Width + 32;
        menuRect = new Rectangle(Width - mw - 20, 17, mw, 38);
        Theme.Shadow(g, menuRect, 19);
        Theme.FillRound(g, hoverKey == "menu" ? Color.FromArgb(39, 39, 42) : Theme.Dark, menuRect, 19);
        Theme.DrawText(g, label, Theme.SmallBold, Color.White, menuRect, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    string KeyAt(Point p)
    {
        if (menuRect.Contains(p)) return "menu";
        foreach (var a in hitAreas) if (a.Key.Contains(p)) return a.Value;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        string k = KeyAt(e.Location);
        if (k != hoverKey) { hoverKey = k; Cursor = k != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hoverKey = null; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        string k = KeyAt(e.Location);
        if (k == "menu") { if (MenuClicked != null) MenuClicked(new Point(menuRect.Right, menuRect.Bottom + 4)); }
        else if (k != null && FilterClicked != null) FilterClicked(k == "all" ? null : k);
        base.OnMouseClick(e);
    }
}

// Həb formalı düymə: ağ (xəritə üstündə) və ya qara (əsas əməliyyat)
class PillButton : Control
{
    public bool Primary;
    bool hover;

    public PillButton(string text, int width, int height)
    {
        Text = text;
        Size = new Size(width, height);
        Font = Theme.SmallBold;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnResize(EventArgs e)
    {
        using (var p = Theme.Round(new RectangleF(0, 0, Width, Height), Height / 2f)) Region = new Region(p);
        base.OnResize(e);
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Color bg = Primary ? (hover ? Color.FromArgb(39, 39, 42) : Theme.Dark) : (hover ? Theme.Soft : Color.White);
        g.Clear(bg);
        if (!Primary) Theme.StrokeRound(g, Theme.Border, r, Height / 2f);
        Theme.DrawText(g, Text, Font, Primary ? Color.White : Theme.Text, ClientRectangle,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
}

// Boz həb içində axtarış qutusu
class SearchBox : Control
{
    public readonly TextBox Input = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Soft, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10f) };
    public bool ShowSearchIcon = true;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

    public SearchBox(string placeholder)
    {
        Height = 38;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Controls.Add(Input);
        Input.HandleCreated += (s, e) => SendMessage(Input.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, placeholder);
        Click += (s, e) => Input.Focus();
    }

    protected override void OnResize(EventArgs e)
    {
        int left = ShowSearchIcon ? 38 : 18;
        Input.SetBounds(left, (Height - Input.PreferredHeight) / 2 + 1, Width - left - 14, Input.PreferredHeight);
        base.OnResize(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, Theme.Soft, new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
        if (ShowSearchIcon)
        using (var p = new Pen(Theme.Muted, 1.6f))
        {
            g.DrawEllipse(p, 15, Height / 2 - 7, 11, 11);
            g.DrawLine(p, 24, Height / 2 + 3, 28, Height / 2 + 7);
        }
    }
}

// ---------------------------------------------------------------- Giriş pəncərəsi
// Saytın öz giriş səhifəsi brauzerdə bir dəfə açılır, sayt proqrama token qaytarır
// (redirect_uri = bu kompüterdə müvəqqəti dinlənən 127.0.0.1 ünvanı).
class LoginForm : Form
{
    // 0x100 onlayn izləmə + 0x200 məlumatlara baxış - yalnız oxumaq, heç nəyi dəyişə bilmir
    const int AccessType = 0x100 | 0x200;

    public readonly CheckBox Remember = new CheckBox { Text = "Yadda saxla", Checked = true, AutoSize = true, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Regular };
    readonly Label error = new Label { ForeColor = Theme.Offline, AutoSize = false, BackColor = Theme.Card, Font = Theme.Small, TextAlign = ContentAlignment.MiddleCenter };
    readonly PillButton ok = new PillButton("Serverdə daxil ol", 316, 46) { Primary = true, Font = Theme.Bold };
    readonly SearchBox serverBox = new SearchBox("Server ünvanı, məs. https://gps.example.com") { ShowSearchIcon = false };
    readonly Func<string, string> tryLogin;   // token -> xəta mətni və ya null
    TcpListener listener;
    readonly Rectangle card = new Rectangle(28, 28, 380, 430);

    // Yazılmış server ünvanı (https:// əlavə olunur, sondakı / silinir)
    public string ServerUrl
    {
        get
        {
            string s = serverBox.Input.Text.Trim().TrimEnd('/');
            if (s.Length > 0 && !s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "https://" + s;
            return s;
        }
    }

    public LoginForm(string server, Func<string, string> tryLogin)
    {
        this.tryLogin = tryLogin;
        Text = "Browser Mini - giriş";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(436, 486);
        Font = Theme.Regular;
        BackColor = Theme.Surface;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        DoubleBuffered = true;

        serverBox.Input.Text = server ?? "";
        serverBox.Bounds = new Rectangle(card.X + 32, card.Y + 148, card.Width - 64, 38);
        var info = new Label
        {
            Text = "Düymə serverin öz giriş səhifəsini brauzerdə açacaq. " +
                   "Orada daxil olun — bu yalnız bir dəfə lazımdır. Proqram şifrənizi görmür.",
            Bounds = new Rectangle(card.X + 32, card.Y + 200, card.Width - 64, 56),
            ForeColor = Theme.Muted, BackColor = Theme.Card, TextAlign = ContentAlignment.TopCenter
        };
        ok.Location = new Point(card.X + (card.Width - ok.Width) / 2, card.Y + 270);
        ok.BackColor = Theme.Card;
        Remember.Location = new Point(card.X + (card.Width - 110) / 2, card.Y + 330);
        error.Bounds = new Rectangle(card.X + 20, card.Y + 362, card.Width - 40, 52);
        Controls.Add(serverBox);
        Controls.Add(info);
        Controls.Add(ok);
        Controls.Add(Remember);
        Controls.Add(error);

        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) StartLogin(); };
        ok.Click += (s, e) => StartLogin();
        FormClosed += (s, e) => StopListener();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawDots(g, ClientRectangle);
        Theme.Shadow(g, card, 24);
        Theme.FillRound(g, Theme.Card, card, 24);
        Theme.StrokeRound(g, Theme.Border, card, 24);

        // Loqo
        var logo = new RectangleF(card.X + card.Width / 2 - 26, card.Y + 36, 52, 52);
        Theme.FillRound(g, Theme.Dark, logo, 14);
        using (var pin = new Pen(Color.White, 3.4f))
        {
            g.DrawEllipse(pin, logo.X + 16, logo.Y + 12, 20, 20);
            g.DrawLine(pin, logo.X + 26, logo.Y + 32, logo.X + 26, logo.Y + 41);
        }
        using (var f = new Font("Segoe UI Semibold", 19f))
            Theme.DrawText(g, "Xoş gəlmisiniz", f, Theme.Text, new Rectangle(card.X, card.Y + 100, card.Width, 40), TextFormatFlags.HorizontalCenter);
    }

    public void ShowError(string message) { error.ForeColor = Theme.Offline; error.Text = message; }

    void StartLogin()
    {
        string server = ServerUrl;
        if (server.Length == 0) { ShowError("Server ünvanını yazın."); serverBox.Input.Focus(); return; }
        StopListener();
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirect = "http://127.0.0.1:" + port + "/";
        string url = server + "/login.html?client_id=BrowserMini&access_type=" + AccessType +
                     "&activation_time=0&duration=0&flags=1&redirect_uri=" + Uri.EscapeDataString(redirect);
        error.ForeColor = Theme.Muted;
        error.Text = "Brauzerdə daxil olun, cavab gözlənilir...";
        TcpListener l = listener;
        new Thread(() => WaitForToken(l)) { IsBackground = true }.Start();
        Process.Start(url);
    }

    void StopListener()
    {
        if (listener != null) { try { listener.Stop(); } catch { } listener = null; }
    }

    void WaitForToken(TcpListener l)
    {
        try
        {
            while (true)
            {
                using (TcpClient client = l.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 5000;
                    string requestLine = new StreamReader(stream, Encoding.ASCII).ReadLine() ?? "";
                    string[] parts = requestLine.Split(' ');
                    string path = parts.Length > 1 ? parts[1] : "/";
                    string token = QueryValue(path, "access_token");
                    string svcError = QueryValue(path, "svc_error");

                    if (token == null && (svcError == null || svcError == "0"))
                    {
                        // Token "#" hissəsində gəlibsə, brauzer onu göndərmir - JS ilə ötürürük
                        Respond(stream, "<script>if(location.hash.length>1)location.replace('/?'+location.hash.substring(1));</script>Gözləyin...");
                        continue;
                    }
                    Respond(stream, token != null
                        ? "<h2>Giriş uğurlu oldu.</h2>Bu səhifəni bağlayıb Browser Mini-yə qayıda bilərsiniz."
                        : "<h2>Giriş alınmadı.</h2>Browser Mini-yə qayıdıb yenidən cəhd edin.");
                    string t = token, err = svcError;
                    BeginInvoke((Action)(() => OnToken(t, err)));
                    return;
                }
            }
        }
        catch { }   // dinləyici bağlandı
    }

    void OnToken(string token, string svcError)
    {
        StopListener();
        Activate();
        if (token == null) { ShowError("Giriş alınmadı (xəta " + svcError + "). Yenidən cəhd edin."); return; }
        error.ForeColor = Theme.Muted;
        error.Text = "Qoşulur...";
        Refresh();
        string err = tryLogin(token);
        if (err == null) { DialogResult = DialogResult.OK; Close(); }
        else ShowError(err);
    }

    static void Respond(NetworkStream stream, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>Browser Mini</title>" +
            "<body style=\"font-family:Segoe UI,sans-serif;padding:40px\">" + body);
        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
            "Connection: close\r\nContent-Length: " + content.Length + "\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(content, 0, content.Length);
    }

    static string QueryValue(string path, string key)
    {
        int q = path.IndexOf('?');
        if (q < 0) return null;
        foreach (string pair in path.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair.Substring(0, eq) == key) return Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        return null;
    }
}

// ---------------------------------------------------------------- Ana pəncərə
class Unit
{
    public long Id;
    public string Name = "";
    public double Lat, Lon;
    public bool HasPos;
    public int Speed;
    public float Course;       // hərəkət istiqaməti (dərəcə)
    public long Time;          // son mesajın vaxtı (unix)
    public string Address;
}

// ---------------------------------------------------------------- Xəritə
// Kafel (tile) mənbəyi. {x} {y} {z} şablonda əvəz olunur.
class TileSource
{
    public string Name, Template, Attribution;
    public bool InvertZoom;     // Wialon kafellərində zoom tərsinədir (17 - z)
    public int MaxZoom = 18;
    public bool NoDiskCache;    // Google/Esri/Mapbox şərtləri: kafellər diskdə saxlanmamalıdır
    public string ViewportUrl;  // Google: görünən sahənin müəllif hüququ mətni üçün
    public string[] Overlays;   // hibrid: peykin üstünə qoyulan şəffaf qatlar (yollar, adlar)

    public string Url(int x, int y, int z) { return Format(Template, x, y, z); }

    public string Format(string template, int x, int y, int z)
    {
        return template.Replace("{x}", x.ToString()).Replace("{y}", y.ToString())
                       .Replace("{z}", (InvertZoom ? 17 - z : z).ToString());
    }

    // ---- Açarsız pulsuz mənbələr
    public static readonly TileSource OpenTopo = new TileSource
    {
        Name = "opentopo",
        Template = "https://tile.opentopomap.org/{z}/{x}/{y}.png",
        Attribution = "© OpenTopoMap (CC-BY-SA) © OpenStreetMap",
        MaxZoom = 17
    };

    public static readonly TileSource Sentinel = new TileSource
    {
        Name = "sentinel2016",
        Template = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{z}/{y}/{x}.jpg",
        Attribution = "Sentinel-2 cloudless 2016 © EOX IT Services (CC BY 4.0)",
        MaxZoom = 15
    };

    // ---- Pulsuz açarla işləyən hibridlər
    public static TileSource CreateEsriHybrid(string key)
    {
        string b = "https://ibasemaps-api.arcgis.com/arcgis/rest/services/";
        string t = "/MapServer/tile/{z}/{y}/{x}?token=" + Uri.EscapeDataString(key);
        return new TileSource
        {
            Name = "esri",
            Template = b + "World_Imagery" + t,
            Overlays = new[] { b + "Reference/World_Transportation" + t, b + "Reference/World_Boundaries_and_Places" + t },
            Attribution = "Powered by Esri • Esri, Maxar, Earthstar Geographics",
            MaxZoom = 19,
            NoDiskCache = true
        };
    }

    public static TileSource CreateMapboxHybrid(string token)
    {
        return new TileSource
        {
            Name = "mapbox",
            Template = "https://api.mapbox.com/styles/v1/mapbox/satellite-streets-v12/tiles/256/{z}/{x}/{y}?access_token=" + Uri.EscapeDataString(token),
            Attribution = "© Mapbox © OpenStreetMap © Maxar",
            MaxZoom = 20,
            NoDiskCache = true
        };
    }

    public static readonly TileSource Osm = new TileSource
    {
        Name = "osm",
        Template = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        Attribution = "© OpenStreetMap",
        MaxZoom = 19
    };

    static TileSource() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

    // Google Hybrid (peyk + yollar və adlar) - rəsmi Map Tiles API, öz API açarınızla
    public static TileSource CreateGoogleHybrid(string key, out string error)
    {
        error = null;
        try
        {
            var req = (HttpWebRequest)WebRequest.Create("https://tile.googleapis.com/v1/createSession?key=" + Uri.EscapeDataString(key));
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 15000;
            byte[] body = Encoding.UTF8.GetBytes("{\"mapType\":\"satellite\",\"language\":\"az\",\"region\":\"AZ\",\"layerTypes\":[\"layerRoadmap\"]}");
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            string json;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = r.ReadToEnd();
            var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            string session = d != null && d.ContainsKey("session") ? Convert.ToString(d["session"]) : null;
            if (string.IsNullOrEmpty(session)) { error = "Google cavabında sessiya yoxdur."; return null; }
            string auth = "session=" + Uri.EscapeDataString(session) + "&key=" + Uri.EscapeDataString(key);
            Log.Write("xəritə: Google Hybrid sessiyası yaradıldı");
            return new TileSource
            {
                Name = "google",
                Template = "https://tile.googleapis.com/v1/2dtiles/{z}/{x}/{y}?" + auth,
                Attribution = "Google",
                MaxZoom = 20,
                NoDiskCache = true,
                ViewportUrl = "https://tile.googleapis.com/tile/v1/viewport?" + auth
            };
        }
        catch (WebException ex)
        {
            string detail = "";
            if (ex.Response != null)
                using (var r = new StreamReader(ex.Response.GetResponseStream())) detail = r.ReadToEnd();
            error = "Google xətası: " + ex.Message + (detail.Length > 0 ? "\n" + (detail.Length > 400 ? detail.Substring(0, 400) : detail) : "");
            Log.Write("xəritə: Google sessiyası alınmadı: " + ex.Message);
            return null;
        }
    }

    public static byte[] Download(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "BrowserMini/1.0 (Windows desktop)";
        req.Timeout = 15000;
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    public static bool IsImage(byte[] b)
    {
        return b != null && b.Length > 8 &&
               ((b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) || (b[0] == 0xFF && b[1] == 0xD8));
    }

    // Wialon serverinin öz xəritəsini tapmağa çalışır (bir neçə məlum ünvan formatı yoxlanılır)
    public static TileSource DetectServer(Wialon api, double lat, double lon)
    {
        const int z = 12;
        int x = (int)(MapView.LonToX(lon, z) / 256), y = (int)(MapView.LatToY(lat, z) / 256);
        string b = api.GisRender + "/gis_render/{x}_{y}_{z}/";
        var candidates = new List<KeyValuePair<string, string>>();
        if (api.GisSid != null) candidates.Add(new KeyValuePair<string, string>("uid+gis_sid", b + api.UserId + "/tile.png?gis_sid=" + Uri.EscapeDataString(api.GisSid)));
        candidates.Add(new KeyValuePair<string, string>("uid+sid", b + api.UserId + "/tile.png?sid=" + Uri.EscapeDataString(api.Sid)));
        candidates.Add(new KeyValuePair<string, string>("uid", b + api.UserId + "/tile.png"));
        candidates.Add(new KeyValuePair<string, string>("sid", b + "tile.png?sid=" + Uri.EscapeDataString(api.Sid)));

        foreach (var c in candidates)
        {
            var src = new TileSource { Name = "server", Template = c.Value, InvertZoom = true, Attribution = "© Gurtam Maps", MaxZoom = 18 };
            string result;
            try
            {
                byte[] data = Download(src.Url(x, y, z));
                if (IsImage(data)) { Log.Write("xəritə: server kafeli işləyir (" + c.Key + ")"); return src; }
                result = "şəkil deyil, " + data.Length + " bayt";
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                result = r != null ? "HTTP " + (int)r.StatusCode : ex.Status.ToString();
            }
            catch (Exception ex) { result = ex.GetType().Name; }
            Log.Write("xəritə: " + c.Key + " -> " + result);
        }
        return null;
    }
}

class TrackPoint
{
    public double Lat, Lon;
    public int Speed;
    public long Time;
}

class MapView : Control
{
    // ---- Hərəkət tarixçəsi (marşrut xətti)
    List<TrackPoint> track;
    public string TrackInfo;            // xəritənin sol yuxarısında göstərilən xülasə

    public void SetTrack(List<TrackPoint> points, bool fit)
    {
        track = points;
        if (fit && points != null && points.Count > 1)
        {
            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            foreach (var p in points)
            {
                minLat = Math.Min(minLat, p.Lat); maxLat = Math.Max(maxLat, p.Lat);
                minLon = Math.Min(minLon, p.Lon); maxLon = Math.Max(maxLon, p.Lon);
            }
            FitBounds(minLat, maxLat, minLon, maxLon);
        }
        Invalidate();
    }

    public void AppendTrack(TrackPoint p)
    {
        if (track == null) return;
        var last = track.Count > 0 ? track[track.Count - 1] : null;
        if (last != null && Math.Abs(last.Lat - p.Lat) < 1e-6 && Math.Abs(last.Lon - p.Lon) < 1e-6) return;
        track.Add(p);
        Invalidate();
    }

    static Color SpeedColor(int s)
    {
        if (s > 90) return Theme.Offline;
        if (s > 60) return Color.FromArgb(245, 158, 11);
        return Theme.Moving;
    }

    void DrawTrack(Graphics g, double left, double top)
    {
        if (track == null || track.Count < 2) return;
        var pts = new PointF[track.Count];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new PointF((float)(LonToX(track[i].Lon, zoom) - left), (float)(LatToY(track[i].Lat, zoom) - top));
        using (var outline = new Pen(Color.FromArgb(230, 255, 255, 255), 7) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(outline, pts);
        // Sürətə görə rənglənmiş seqmentlər (eyni rəngli ardıcıl hissələr bir xətt kimi)
        int start = 0;
        Color cur = SpeedColor(track[1].Speed);
        for (int i = 2; i <= pts.Length; i++)
        {
            Color c = i < pts.Length ? SpeedColor(track[i].Speed) : Color.Empty;
            if (c != cur)
            {
                var seg = new PointF[i - start];
                Array.Copy(pts, start, seg, 0, seg.Length);
                if (seg.Length > 1)
                    using (var p = new Pen(cur, 3.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(p, seg);
                start = i - 1;
                cur = c;
            }
        }
        // Başlanğıc nöqtəsi
        PointF s0 = pts[0];
        g.FillEllipse(Brushes.White, s0.X - 6, s0.Y - 6, 12, 12);
        using (var p = new Pen(Theme.Dark, 2.5f)) g.DrawEllipse(p, s0.X - 6, s0.Y - 6, 12, 12);
    }

    void DrawTrackInfo(Graphics g)
    {
        if (TrackInfo == null) return;
        int w = TextRenderer.MeasureText(g, TrackInfo, Theme.SmallBold).Width + 28;
        var r = new Rectangle(14, 56, w, 30);
        Theme.Shadow(g, r, 15);
        Theme.FillRound(g, Theme.Dark, r, 15);
        Theme.DrawText(g, TrackInfo, Theme.SmallBold, Color.White, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    const int TileSize = 256, MaxCachedTiles = 90;

    TileSource source = TileSource.Osm;
    int zoom = 12;
    double cx, cy;                      // mərkəz, cari zoom-da dünya pikseli
    readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
    readonly LinkedList<string> lru = new LinkedList<string>();
    readonly List<string> queue = new List<string>();
    readonly HashSet<string> pending = new HashSet<string>(), failed = new HashSet<string>();
    readonly object sync = new object();
    readonly string diskRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BrowserMini\tiles");
    readonly Font labelFont = new Font("Segoe UI", 8f, FontStyle.Bold);
    Point dragStart, lastMouse;
    bool dragging, moved;
    int tileErrors;
    string dynamicAttribution, viewportKey;
    readonly System.Windows.Forms.Timer viewportTimer = new System.Windows.Forms.Timer { Interval = 800 };

    // Google: xəritə dayananda görünən sahənin müəllif hüququ mətnini alır
    void FetchViewportAttribution()
    {
        viewportTimer.Stop();
        TileSource src = source;
        if (src.ViewportUrl == null) return;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        double left = cx - Width / 2.0, top = cy - Height / 2.0;
        string url = src.ViewportUrl + "&zoom=" + zoom +
            "&north=" + YToLat(top, zoom).ToString(ic) + "&south=" + YToLat(top + Height, zoom).ToString(ic) +
            "&west=" + XToLon(left, zoom).ToString(ic) + "&east=" + XToLon(left + Width, zoom).ToString(ic);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string json = Encoding.UTF8.GetString(TileSource.Download(url));
                var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                string text = d != null && d.ContainsKey("copyright") ? Convert.ToString(d["copyright"]) : null;
                BeginInvoke((Action)(() => { if (source == src) { dynamicAttribution = text; Invalidate(); } }));
            }
            catch { }
        });
    }

    public Func<IEnumerable<Unit>> Units;
    public Func<Unit, Color> UnitColor;
    public Unit Selected;
    public event Action<Unit> UnitClicked;

    public MapView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        CenterOn(40.4093, 49.8671, 11);   // Bakı
        viewportTimer.Tick += (s, e) => FetchViewportAttribution();
        for (int i = 0; i < 3; i++) new Thread(TileWorker) { IsBackground = true }.Start();
    }

    public TileSource Source
    {
        get { return source; }
        set
        {
            lock (sync)
            {
                source = value;
                dynamicAttribution = null;
                viewportKey = null;
                foreach (var b in cache.Values) b.Dispose();
                cache.Clear(); lru.Clear(); queue.Clear(); pending.Clear(); failed.Clear();
            }
            if (zoom > source.MaxZoom) SetZoom(source.MaxZoom, Width / 2, Height / 2);
            Invalidate();
        }
    }

    // ---- Proyeksiya (Web Mercator)
    public static double LonToX(double lon, int z) { return (lon + 180) / 360 * TileSize * (1 << z); }
    public static double LatToY(double lat, int z)
    {
        double s = Math.Sin(Math.Max(-85, Math.Min(85, lat)) * Math.PI / 180);
        return (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * TileSize * (1 << z);
    }
    static double XToLon(double x, int z) { return x / (TileSize * (double)(1 << z)) * 360 - 180; }
    static double YToLat(double y, int z)
    {
        double n = Math.PI - 2 * Math.PI * y / (TileSize * (double)(1 << z));
        return 180 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    public void CenterOn(double lat, double lon, int z)
    {
        zoom = Math.Max(3, Math.Min(source.MaxZoom, z));
        cx = LonToX(lon, zoom);
        cy = LatToY(lat, zoom);
        Invalidate();
    }

    public void CenterOn(Unit u, int minZoom)
    {
        if (u != null && u.HasPos) CenterOn(u.Lat, u.Lon, Math.Max(zoom, minZoom));
    }

    public void FitAll()
    {
        double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
        int n = 0;
        foreach (var u in Units())
        {
            if (!u.HasPos) continue;
            minLat = Math.Min(minLat, u.Lat); maxLat = Math.Max(maxLat, u.Lat);
            minLon = Math.Min(minLon, u.Lon); maxLon = Math.Max(maxLon, u.Lon);
            n++;
        }
        if (n == 0) return;
        FitBounds(minLat, maxLat, minLon, maxLon);
    }

    void FitBounds(double minLat, double maxLat, double minLon, double maxLon)
    {
        int z = source.MaxZoom > 16 ? 16 : source.MaxZoom;
        while (z > 3 && (LonToX(maxLon, z) - LonToX(minLon, z) > Width - 80 || LatToY(minLat, z) - LatToY(maxLat, z) > Height - 120)) z--;
        CenterOn((minLat + maxLat) / 2, (minLon + maxLon) / 2, z);
    }

    public void ZoomBy(int delta) { SetZoom(zoom + delta, Width / 2, Height / 2); }

    void SetZoom(int z, int px, int py)
    {
        z = Math.Max(3, Math.Min(source.MaxZoom, z));
        if (z == zoom) return;
        // Kursorun altındakı nöqtə yerində qalsın
        double wx = cx - Width / 2.0 + px, wy = cy - Height / 2.0 + py;
        double lon = XToLon(wx, zoom), lat = YToLat(wy, zoom);
        zoom = z;
        cx = LonToX(lon, zoom) - px + Width / 2.0;
        cy = LatToY(lat, zoom) - py + Height / 2.0;
        Invalidate();
    }

    // ---- Çəkmə
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Color.FromArgb(232, 232, 228));
        double left = cx - Width / 2.0, top = cy - Height / 2.0;
        int n = 1 << zoom;
        int tx0 = (int)Math.Floor(left / TileSize), ty0 = (int)Math.Floor(top / TileSize);
        int tx1 = (int)Math.Floor((left + Width) / TileSize), ty1 = (int)Math.Floor((top + Height) / TileSize);

        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int ty = ty0; ty <= ty1; ty++)
        {
            if (ty < 0 || ty >= n) continue;
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int x = ((tx % n) + n) % n;
                Bitmap tile = GetTile(zoom, x, ty);
                if (tile != null) g.DrawImage(tile, (float)(tx * TileSize - left), (float)(ty * TileSize - top), TileSize, TileSize);
            }
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Default;
        DrawTrack(g, left, top);
        if (Units != null)
        {
            foreach (var u in Units())
            {
                if (!u.HasPos || u == Selected) continue;
                DrawUnit(g, u, left, top, false);
            }
            if (Selected != null && Selected.HasPos) DrawUnit(g, Selected, left, top, true);
        }
        DrawTrackInfo(g);

        string attr = source.Attribution;
        if (source.ViewportUrl != null)
        {
            if (dynamicAttribution != null) attr = "Google • " + dynamicAttribution;
            string key = zoom + "/" + (int)(cx / 64) + "/" + (int)(cy / 64) + "/" + Width + "x" + Height;
            if (key != viewportKey) { viewportKey = key; viewportTimer.Stop(); viewportTimer.Start(); }
        }
        SizeF sz = g.MeasureString(attr, Font);
        using (var bg = new SolidBrush(Color.FromArgb(180, Color.White)))
            g.FillRectangle(bg, Width - sz.Width - 6, Height - sz.Height - 2, sz.Width + 6, sz.Height + 2);
        g.DrawString(attr, Font, Brushes.DimGray, Width - sz.Width - 3, Height - sz.Height - 1);
    }

    void DrawUnit(Graphics g, Unit u, double left, double top, bool selected)
    {
        float x = (float)(LonToX(u.Lon, zoom) - left), y = (float)(LatToY(u.Lat, zoom) - top);
        if (x < -100 || y < -30 || x > Width + 30 || y > Height + 30) return;
        Color c = UnitColor != null ? UnitColor(u) : Color.SteelBlue;
        float r = selected ? 9 : 7;
        if (selected)
            using (var ring = new Pen(Color.Gold, 4)) g.DrawEllipse(ring, x - r - 3, y - r - 3, 2 * r + 6, 2 * r + 6);

        if (u.Speed > 3)
        {
            // Hərəkət istiqaməti: dairənin üstündə ox
            var state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(u.Course);
            using (var b = new SolidBrush(c))
                g.FillPolygon(b, new[] { new PointF(0, -r - 8), new PointF(-5, -r + 1), new PointF(5, -r + 1) });
            g.Restore(state);
        }
        using (var b = new SolidBrush(c)) g.FillEllipse(b, x - r, y - r, 2 * r, 2 * r);
        using (var p = new Pen(Color.White, 2)) g.DrawEllipse(p, x - r, y - r, 2 * r, 2 * r);

        SizeF sz = g.MeasureString(u.Name, labelFont);
        float lx = x + r + 3, ly = y - sz.Height / 2;
        using (var bg = new SolidBrush(Color.FromArgb(selected ? 240 : 200, Color.White)))
            g.FillRectangle(bg, lx - 2, ly, sz.Width + 2, sz.Height);
        using (var tb = new SolidBrush(selected ? Color.Black : Color.FromArgb(40, 40, 40)))
            g.DrawString(u.Name, labelFont, tb, lx - 1, ly);
    }

    // ---- Kafellər: yaddaş -> disk -> internet
    Bitmap GetTile(int z, int x, int y)
    {
        string key = z + "/" + x + "/" + y;
        lock (sync)
        {
            Bitmap b;
            if (cache.TryGetValue(key, out b))
            {
                lru.Remove(key);
                lru.AddFirst(key);
                return b;
            }
            if (!pending.Contains(key) && !failed.Contains(key))
            {
                pending.Add(key);
                queue.Add(key);
                Monitor.PulseAll(sync);
            }
        }
        return null;
    }

    void TileWorker()
    {
        while (true)
        {
            string key;
            TileSource src;
            lock (sync)
            {
                while (queue.Count == 0) Monitor.Wait(sync);
                key = queue[queue.Count - 1];          // ən son istənən birinci
                queue.RemoveAt(queue.Count - 1);
                src = source;
            }
            Bitmap bmp = null;
            try
            {
                string[] p = key.Split('/');
                int z = int.Parse(p[0]), x = int.Parse(p[1]), y = int.Parse(p[2]);
                if (!IsVisible(z, x, y)) { lock (sync) pending.Remove(key); continue; }
                string file = Path.Combine(diskRoot, src.Name + "\\" + z + "\\" + x + "_" + y + ".png");
                byte[] data = !src.NoDiskCache && File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays < 30 ? File.ReadAllBytes(file) : null;
                if (data == null)
                {
                    data = TileSource.Download(src.Url(x, y, z));
                    if (src.NoDiskCache) { }
                    else if (TileSource.IsImage(data))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllBytes(file, data);
                    }
                    else if (tileErrors++ < 5) Log.Write("xəritə kafeli (" + src.Name + " " + key + "): şəkil deyil, " + data.Length + " bayt");
                }
                if (TileSource.IsImage(data))
                    using (var ms = new MemoryStream(data))
                    using (var img = Image.FromStream(ms))
                        bmp = new Bitmap(img);

                // Hibrid: yollar və adlar qatını peykin üstünə çək
                if (bmp != null && src.Overlays != null)
                    using (var g = Graphics.FromImage(bmp))
                        foreach (string ov in src.Overlays)
                        {
                            try
                            {
                                byte[] od = TileSource.Download(src.Format(ov, x, y, z));
                                if (!TileSource.IsImage(od)) continue;
                                using (var ms = new MemoryStream(od))
                                using (var img = Image.FromStream(ms))
                                    g.DrawImage(img, 0, 0, TileSize, TileSize);
                            }
                            catch { }
                        }
            }
            catch (Exception ex)
            {
                if (tileErrors++ < 5) Log.Write("xəritə kafeli (" + src.Name + " " + key + "): " + ex.Message);
            }

            lock (sync)
            {
                pending.Remove(key);
                if (src != source) { if (bmp != null) bmp.Dispose(); continue; }
                if (bmp == null) { failed.Add(key); continue; }
                cache[key] = bmp;
                lru.AddFirst(key);
                while (lru.Count > MaxCachedTiles)
                {
                    string old = lru.Last.Value;
                    lru.RemoveLast();
                    cache[old].Dispose();
                    cache.Remove(old);
                }
            }
            try { BeginInvoke((Action)Invalidate); } catch { }
        }
    }

    bool IsVisible(int z, int x, int y)
    {
        if (z != zoom) return false;
        double left = cx - Width / 2.0, top = cy - Height / 2.0;
        int n = 1 << z;
        double tl = Math.Floor(left / TileSize) - 1, tr = Math.Floor((left + Width) / TileSize) + 1;
        double tt = Math.Floor(top / TileSize) - 1, tb = Math.Floor((top + Height) / TileSize) + 1;
        if (y < tt || y > tb) return false;
        for (double tx = tl; tx <= tr; tx++) if ((((int)tx % n) + n) % n == x) return true;
        return false;
    }

    // ---- Siçan: sürüşdür, təkərlə yaxınlaşdır, maşına klik
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left) { dragging = true; moved = false; dragStart = lastMouse = e.Location; Cursor = Cursors.SizeAll; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            cx -= e.X - lastMouse.X;
            cy -= e.Y - lastMouse.Y;
            lastMouse = e.Location;
            if (Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) > 4) moved = true;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (dragging && !moved && e.Button == MouseButtons.Left)
        {
            Unit hit = HitTest(e.Location);
            if (hit != null) { Selected = hit; Invalidate(); if (UnitClicked != null) UnitClicked(hit); }
        }
        dragging = false;
        Cursor = Cursors.Default;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        SetZoom(zoom + (e.Delta > 0 ? 1 : -1), e.X, e.Y);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        SetZoom(zoom + (e.Button == MouseButtons.Right ? -1 : 1), e.X, e.Y);
        base.OnMouseDoubleClick(e);
    }

    Unit HitTest(Point p)
    {
        if (Units == null) return null;
        double left = cx - Width / 2.0, top = cy - Height / 2.0;
        Unit best = null;
        double bestDist = 12;
        foreach (var u in Units())
        {
            if (!u.HasPos) continue;
            double dx = LonToX(u.Lon, zoom) - left - p.X, dy = LatToY(u.Lat, zoom) - top - p.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestDist) { bestDist = d; best = u; }
        }
        return best;
    }
}

// Üzən bildiriş pəncərəsi: başlığından sürüşdürülür, kənarlarından ölçüsü dəyişir,
// istəyə görə hər şeyin üstündə qalır. Yeri və ölçüsü alerts_window.txt-də saxlanır.
class AlertWindow : Form
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    const int Edge = 7;     // ölçü dəyişmək üçün kənar zolağın eni
    static readonly string StateFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "alerts_window.txt");

    public readonly CardList List = new CardList { Dock = DockStyle.Fill, ItemHeight = 46, EmptyText = "Hələ bildiriş yoxdur" };
    public readonly Label TitleLabel = new Label { Dock = DockStyle.Fill, Font = Theme.Bold, ForeColor = Theme.Text, BackColor = Theme.Card, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    public event Action ClearClicked;
    public bool WantVisible = true;     // istifadəçi bağlayıbsa, avtomatik açılmasın deyə yadda saxlanır
    readonly PillButton pin = new PillButton("📌", 32, 28);
    readonly PillButton clear = new PillButton("Təmizlə", 72, 28);
    readonly PillButton close = new PillButton("✕", 32, 28);

    public AlertWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Card;
        Font = Theme.Regular;
        Text = "Bildirişlər";
        MinimumSize = new Size(260, 150);
        Padding = new Padding(Edge, 4, Edge, Edge);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        var head = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Card, Cursor = Cursors.SizeAll };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 150, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Card, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
        foreach (var b in new[] { close, clear, pin }) { b.Margin = new Padding(3, 0, 0, 0); buttons.Controls.Add(b); }
        head.Controls.Add(TitleLabel);
        head.Controls.Add(buttons);
        TitleLabel.Cursor = Cursors.SizeAll;
        head.MouseDown += StartDrag;
        TitleLabel.MouseDown += StartDrag;
        Controls.Add(List);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Theme.Card });
        Controls.Add(head);

        pin.Click += (s, e) => { TopMost = !TopMost; UpdatePin(); SaveState(); };
        clear.Click += (s, e) => { if (ClearClicked != null) ClearClicked(); };
        close.Click += (s, e) => { WantVisible = false; Hide(); SaveState(); };
        var tip = new ToolTip();
        tip.SetToolTip(pin, "Həmişə üstdə");
        tip.SetToolTip(close, "Gizlət (menyudan yenidən açılır)");

        LoadState();
        UpdatePin();
        Move += (s, e) => SaveStateLater();
        Resize += (s, e) => SaveStateLater();
    }

    // Fokusu oğurlamadan göstər (işlədiyiniz pəncərə aktiv qalır)
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;       // CS_DROPSHADOW: pəncərənin kölgəsi
            cp.ExStyle |= 0x80;             // WS_EX_TOOLWINDOW: Alt+Tab-da görünməsin
            return cp;
        }
    }

    void UpdatePin()
    {
        pin.Primary = TopMost;
        pin.Invalidate();
    }

    void StartDrag(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
    }

    // Kənarlardan və künclərdən ölçü dəyişmə
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x84 /*WM_NCHITTEST*/ || (int)m.Result != 1 /*HTCLIENT*/) return;
        Point p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        bool l = p.X < Edge, r = p.X >= Width - Edge, t = p.Y < Edge, b = p.Y >= Height - Edge;
        if (t && l) m.Result = (IntPtr)13;
        else if (t && r) m.Result = (IntPtr)14;
        else if (b && l) m.Result = (IntPtr)16;
        else if (b && r) m.Result = (IntPtr)17;
        else if (l) m.Result = (IntPtr)10;
        else if (r) m.Result = (IntPtr)11;
        else if (t) m.Result = (IntPtr)12;
        else if (b) m.Result = (IntPtr)15;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        // Sağ aşağı küncdə ölçü dəyişmə işarəsi
        using (var b = new SolidBrush(Theme.Stopped))
            for (int i = 0; i < 3; i++)
                for (int j = 0; j <= i; j++)
                    g.FillRectangle(b, Width - 6 - j * 4, Height - 14 + i * 4 - 0, 2, 2);
    }

    // ---- Yer və ölçü
    System.Windows.Forms.Timer saveTimer;

    void SaveStateLater()
    {
        if (saveTimer == null)
        {
            saveTimer = new System.Windows.Forms.Timer { Interval = 800 };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveState(); };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    public void SaveState()
    {
        try
        {
            File.WriteAllText(StateFile, Left + "," + Top + "," + Width + "," + Height + "," + (WantVisible ? 1 : 0) + "," + (TopMost ? 1 : 0));
        }
        catch { }
    }

    void LoadState()
    {
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        var bounds = new Rectangle(area.Right - 380, area.Bottom - 340, 360, 320);   // default: sağ aşağı künc
        TopMost = true;
        try
        {
            if (File.Exists(StateFile))
            {
                string[] p = File.ReadAllText(StateFile).Split(',');
                var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                // Ekranlardan biri ilə kəsişirsə qəbul et (monitor ayrılıbsa, default yerə qayıt)
                foreach (var s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(new Rectangle(r.X, r.Y, r.Width, 40))) { bounds = r; break; }
                WantVisible = p.Length < 5 || p[4] == "1";
                TopMost = p.Length < 6 || p[5] == "1";
            }
        }
        catch { }
        Bounds = bounds;
    }
}

class Alert
{
    public long Time;
    public string Text;         // bildirişin mətni (txt)
    public string Name;         // bildirişin adı (Wialon-da qurulan)
    public long UnitId;         // hansı maşın
    public double Lat, Lon;     // hadisənin yeri
    public bool HasPos;
    public Color Color = Theme.Dark;
}

class MainForm : Form
{
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
    [DllImport("user32.dll")] static extern bool FlashWindow(IntPtr hwnd, bool invert);

    const int UnitFlags = 0x1 | 0x400;          // əsas + son mesaj və mövqe
    const int ResourceFlags = 0x1 | 0x20 | 0x400;   // əsas + mesajlar (onlayn bildirişlər bunlarla gəlir) + bildiriş siyahısı
    int unknownLogged;

    readonly Wialon api;
    readonly float volume;
    readonly string soundFile;
    readonly Dictionary<long, Unit> units = new Dictionary<long, Unit>();
    readonly HashSet<long> resources = new HashSet<long>();
    readonly HeaderBar header = new HeaderBar { Dock = DockStyle.Top };
    readonly SearchBox search = new SearchBox("Maşın və ya ünvan axtar...") { Dock = DockStyle.Top };
    readonly CardList unitBox = new CardList { Dock = DockStyle.Fill, ItemHeight = 62, EmptyText = "Maşınlar yüklənir..." };
    readonly AlertWindow alertWin = new AlertWindow();
    readonly CardList alertBox;
    readonly Label unitTitle = new Label { Dock = DockStyle.Top, Height = 34, Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Card, Padding = new Padding(4, 2, 0, 0) };
    readonly Label status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0) };
    readonly Label ram = new Label { Dock = DockStyle.Right, Width = 140, ForeColor = Theme.Muted, Font = Theme.Small, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 16, 0) };
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ViewSwitch viewSwitch = new ViewSwitch();
    static readonly string ViewFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "view.txt");
    readonly MapView map = new MapView { Dock = DockStyle.Fill };
    readonly PillButton layerButton = new PillButton("Xəritə ▾", 170, 34);
    readonly ContextMenuStrip headerMenu = new ContextMenuStrip();
    readonly List<string> layers = new List<string> { LayerOsm, LayerTopo, LayerSentinel, LayerEsri, LayerMapbox, LayerGoogle };
    TileSource serverTiles;
    string filter;                                  // null, "move", "stop", "off"
    SplitContainer top;
    readonly Queue<Unit> geocodeQueue = new Queue<Unit>();
    readonly Dictionary<string, string> addressCache = new Dictionary<string, string>();
    Thread worker;
    volatile bool running = true;
    int geocodeFailures;

    public MainForm(Wialon api, float volume, string soundFile)
    {
        this.api = api;
        this.volume = volume;
        this.soundFile = soundFile;
        Text = "Browser Mini - " + api.UserName;
        Width = 1360;
        Height = 860;
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Regular;
        BackColor = Theme.Surface;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        // ---- Yuxarı zolaq
        header.UserName = api.UserName;
        header.FilterClicked += f => { filter = f; header.ActiveFilter = f; header.Invalidate(); RebuildUnitList(); };
        header.MenuClicked += p => headerMenu.Show(header, p, ToolStripDropDownDirection.BelowLeft);

        // ---- Sol kart: başlıq, axtarış, maşın siyahısı
        unitBox.DrawItem = DrawUnitCard;
        unitBox.SelectionChanged += () =>
        {
            Unit u = SelectedUnit();
            if (u == null) return;
            map.Selected = u;
            map.CenterOn(u, 0);
            if (trackEnabled) { trackDebounce.Stop(); trackDebounce.Start(); }   // oxlarla sürətli keçəndə hər birini yükləmə
        };
        trackDebounce.Tick += (s, e) => { trackDebounce.Stop(); LoadTrack(); };
        unitBox.ItemActivated += () => map.CenterOn(SelectedUnit(), 16);
        search.Input.TextChanged += (s, e) => RebuildUnitList();
        var unitMenu = new ContextMenuStrip();
        unitMenu.Items.Add("Xəritədə yaxınlaşdır", null, (s, e) => map.CenterOn(SelectedUnit(), 16));
        unitMenu.Items.Add("Koordinatı kopyala", null, (s, e) => CopyCoords());
        unitMenu.Items.Add("Brauzerdə aç (OpenStreetMap)", null, (s, e) => OpenMap());
        unitBox.ContextMenuStrip = unitMenu;

        var leftCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 14, 18) };
        leftCard.Controls.Add(unitBox);
        leftCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Card });
        leftCard.Controls.Add(search);
        leftCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Card });
        var titleRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.Card };
        unitTitle.Dock = DockStyle.Fill;
        viewSwitch.Dock = DockStyle.Right;
        viewSwitch.ModeChanged += ApplyView;
        titleRow.Controls.Add(unitTitle);
        titleRow.Controls.Add(viewSwitch);
        leftCard.Controls.Add(titleRow);
        int savedView = 0;
        try { if (File.Exists(ViewFile)) int.TryParse(File.ReadAllText(ViewFile).Trim(), out savedView); } catch { }
        viewSwitch.Mode = Math.Max(0, Math.Min(2, savedView));
        ApplyView(viewSwitch.Mode);

        // ---- Sağ kart: xəritə və üzən düymələr
        map.Units = () => units.Values;
        map.UnitColor = u => { Color c; StateOf(u, out c); return c; };
        map.UnitClicked += u => unitBox.SelectedItem = u;
        var fitButton = new PillButton("Hamısını göstər", 130, 34);
        fitButton.Click += (s, e) => map.FitAll();
        var zoomIn = new PillButton("+", 34, 34) { Font = Theme.Bold };
        zoomIn.Click += (s, e) => map.ZoomBy(1);
        var zoomOut = new PillButton("−", 34, 34) { Font = Theme.Bold };
        zoomOut.Click += (s, e) => map.ZoomBy(-1);
        layerButton.Click += (s, e) => ShowLayerMenu();
        foreach (Control c in new Control[] { layerButton, fitButton, zoomIn, zoomOut }) map.Controls.Add(c);
        trackPeriodButton.Click += (s, e) => ShowPeriodMenu();
        trackClose.Click += (s, e) => CloseTrack();
        trackPeriodButton.Visible = trackClose.Visible = false;
        map.Controls.Add(trackPeriodButton);
        map.Controls.Add(trackClose);
        map.Resize += (s, e) =>
        {
            trackPeriodButton.Location = new Point(14, 14);
            trackClose.Location = new Point(trackPeriodButton.Right + 6, 14);
            layerButton.Location = new Point(map.Width - layerButton.Width - 14, 14);
            fitButton.Location = new Point(layerButton.Left - fitButton.Width - 8, 14);
            zoomIn.Location = new Point(map.Width - zoomIn.Width - 14, map.Height - 2 * zoomIn.Height - 40);
            zoomOut.Location = new Point(map.Width - zoomOut.Width - 14, map.Height - zoomOut.Height - 34);
        };
        var mapCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 13, 15) };
        mapCard.Controls.Add(map);
        map.SizeChanged += (s, e) =>
        {
            using (var p = Theme.Round(new RectangleF(0, 0, map.Width, map.Height), 12)) map.Region = new Region(p);
        };

        // ---- Bildirişlər: ayrıca üzən pəncərədə
        alertBox = alertWin.List;
        alertBox.DrawItem = DrawAlertCard;
        alertBox.SelectionChanged += () => ShowAlertOnMap(alertBox.SelectedItem as Alert);
        alertBox.ItemActivated += () => ShowAlertOnMap(alertBox.SelectedItem as Alert);
        alertWin.ClearClicked += () => { alertBox.SetItems(new object[0]); UpdateAlertTitle(); };
        alertWin.Icon = Icon;
        UpdateAlertTitle();
        // ---- Düzülüş
        top = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 4, BackColor = Theme.Surface, FixedPanel = FixedPanel.Panel1 };
        top.Panel1.BackColor = top.Panel2.BackColor = Theme.Surface;
        top.Panel1.Controls.Add(leftCard);
        top.Panel2.Controls.Add(mapCard);
        var body = new DotPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 4) };
        body.Controls.Add(top);

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Theme.Surface };
        statusBar.Controls.Add(status);
        statusBar.Controls.Add(ram);

        Controls.Add(body);
        Controls.Add(header);
        Controls.Add(statusBar);
        Load += (s, e) => { top.SplitterDistance = 440; };

        // ---- Menyu (sağ yuxarı qara düymə)
        headerMenu.Items.Add("Bildirişlər pəncərəsini göstər", null, (s, e) => ShowAlertWindow(true));
        headerMenu.Items.Add("Bildiriş səsini yoxla", null, (s, e) => PlayAlertSound());
        var trackToggle =new ToolStripMenuItem("Klikdə hərəkət tarixçəsini göstər") { Checked = true, CheckOnClick = true };
        trackToggle.CheckedChanged += (s, e) => { trackEnabled = trackToggle.Checked; if (!trackEnabled) CloseTrack(); };
        headerMenu.Items.Add(trackToggle);
        var toggle = new ToolStripMenuItem("Xəritəni gizlət");
        toggle.Click += (s, e) =>
        {
            top.Panel2Collapsed = !top.Panel2Collapsed;
            toggle.Text = top.Panel2Collapsed ? "Xəritəni göstər" : "Xəritəni gizlət";
            if (top.Panel2Collapsed) map.Source = map.Source;   // kafelləri yaddaşdan at
            TrimMemory();
        };
        headerMenu.Items.Add(toggle);
        headerMenu.Items.Add(new ToolStripSeparator());
        headerMenu.Items.Add("Hesabdan çıxış", null, (s, e) => { Credentials.Clear(); Close(); });
        headerMenu.Items.Add("Bağla", null, (s, e) => Close());

        // ---- Saat yanındakı ikon
        tray.Icon = Icon;
        tray.Text = "Browser Mini";
        tray.DoubleClick += (s, e) => RestoreFromTray();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Aç", null, (s, e) => RestoreFromTray());
        trayMenu.Items.Add("Bildirişlər pəncərəsi", null, (s, e) => ShowAlertWindow(true));
        trayMenu.Items.Add("Bildiriş səsini yoxla", null, (s, e) => PlayAlertSound());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Hesabdan çıxış", null, (s, e) => { Credentials.Clear(); Close(); });
        trayMenu.Items.Add("Bağla", null, (s, e) => Close());
        tray.ContextMenuStrip = trayMenu;
        tray.Visible = true;

        Resize += (s, e) =>
        {
            if (WindowState == FormWindowState.Minimized) { Hide(); TrimMemory(); }
        };

        // Səs: öz prosesimizin səs sessiyası əvvəlcədən 5%-ə qoyulur
        AppVolume.PresetDefaultSession(volume);
        var timer = new System.Windows.Forms.Timer { Interval = 15000 };
        timer.Tick += (s, e) => { RefreshTimes(); UpdateRam(); };
        timer.Start();
        var volumeTimer = new System.Windows.Forms.Timer { Interval = 300 };
        volumeTimer.Tick += (s, e) => AppVolume.ApplyToNewSessions((uint)Process.GetCurrentProcess().Id, volume);
        volumeTimer.Start();

        Shown += (s, e) =>
        {
            string saved = SavedLayer();
            if (saved != null && layers.Contains(saved) && saved != LayerOsm) ChangeLayer(saved);
            UpdateLayerButton();
            if (alertWin.WantVisible) alertWin.Show();
            Start();
        };
        FormClosing += (s, e) =>
        {
            running = false;
            tray.Visible = false;
            alertWin.SaveState();
            alertWin.Close();
            ThreadPool.QueueUserWorkItem(_ => api.Logout());
        };
    }

    void Start()
    {
        status.Text = "Maşınlar yüklənir...";
        object r;
        try
        {
            r = api.Call("core/update_data_flags", new Dictionary<string, object>
            {
                { "spec", new object[]
                    {
                        new Dictionary<string, object> { { "type", "type" }, { "data", "avl_unit" }, { "flags", UnitFlags }, { "mode", 0 } },
                        new Dictionary<string, object> { { "type", "type" }, { "data", "avl_resource" }, { "flags", ResourceFlags }, { "mode", 0 } }
                    } }
            });
        }
        catch (Exception ex)
        {
            status.Text = "Xəta: " + ex.Message;
            return;
        }

        foreach (object o in AsArray(r))
        {
            var item = o as Dictionary<string, object>;
            if (item == null || !item.ContainsKey("d")) continue;
            var d = item["d"] as Dictionary<string, object>;
            if (d == null) continue;
            long id = Convert.ToInt64(item["i"]);
            int cls = d.ContainsKey("cls") ? Convert.ToInt32(d["cls"]) : 0;
            if (cls == 3) { resources.Add(id); continue; }   // 3 = resurs (bildirişlər buradan gəlir)
            if (cls != 2) continue;                           // 2 = maşın
            var u = new Unit { Id = id };
            units[id] = u;
            ApplyUnitData(u, d);
        }
        unitBox.EmptyText = "Heç nə tapılmadı";
        Log.Write("abunə: " + units.Count + " maşın, " + resources.Count + " resurs (bildirişlər resurslardan gəlir)");
        RebuildUnitList();
        status.Text = "Qoşulub  •  canlı yenilənir";
        UpdateRam();
        TrimMemory();
        map.FitAll();
        DetectServerMap();

        worker = new Thread(PollLoop) { IsBackground = true };
        worker.Start();
    }

    // ---- Siyahı: süzgəc + axtarış, ada görə sıralı
    void RebuildUnitList()
    {
        string q = search.Input.Text.Trim();
        var list = new List<Unit>();
        foreach (var u in units.Values)
        {
            if (filter != null && StateKey(u) != filter) continue;
            if (q.Length > 0 && u.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                (u.Address == null || u.Address.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0)) continue;
            list.Add(u);
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        unitBox.SetItems(list.ConvertAll(u => (object)u));
        unitTitle.Text = "Maşınlar  " + list.Count + (list.Count != units.Count ? " / " + units.Count : "");
        RefreshCounts();
    }

    void RefreshCounts()
    {
        int moving = 0, stopped = 0, offline = 0;
        foreach (var u in units.Values)
        {
            string k = StateKey(u);
            if (k == "move") moving++; else if (k == "stop") stopped++; else offline++;
        }
        header.SetCounts(units.Count, moving, stopped, offline);
    }

    void DrawUnitCard(Graphics g, object o, Rectangle r, bool selected, bool hover)
    {
        var u = (Unit)o;
        Color c;
        string state = StateOf(u, out c);
        var inner = new RectangleF(r.X + 2, r.Y + 3, r.Width - 6, r.Height - 6);
        if (selected) Theme.FillRound(g, Theme.Soft, inner, 14);
        else if (hover) Theme.FillRound(g, Theme.Hover, inner, 14);
        else using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.X + 14, r.Bottom - 1, r.Right - 14, r.Bottom - 1);
        if (selected) Theme.FillRound(g, Theme.Dark, new RectangleF(r.X + 2, r.Y + 16, 4, r.Height - 32), 2);

        // Sol: ad və vəziyyət həbi + ünvan
        int x = r.X + 16;
        Theme.DrawText(g, u.Name, Theme.Bold, Theme.Text, new Rectangle(x, r.Y + 9, r.Width - 140, 22), TextFormatFlags.Left);
        int pw = TextRenderer.MeasureText(g, state, Theme.SmallBold).Width + 22;
        var pill = new RectangleF(x, r.Y + 34, pw, 20);
        Theme.FillRound(g, Color.FromArgb(28, c), pill, 10);
        using (var b = new SolidBrush(c)) g.FillEllipse(b, pill.X + 8, pill.Y + 7, 6, 6);
        Theme.DrawText(g, state, Theme.SmallBold, c == Theme.Stopped ? Theme.Muted : c,
            new Rectangle((int)pill.X + 16, (int)pill.Y, pw - 16, 20), TextFormatFlags.VerticalCenter);
        string addr = u.Address ?? (u.HasPos ? Coords(u) : "");
        Theme.DrawText(g, addr, Theme.Small, Theme.Muted, new Rectangle(x + pw + 8, r.Y + 34, r.Width - pw - 130, 20), TextFormatFlags.VerticalCenter);

        // Sağ: sürət və vaxt
        var right = new Rectangle(r.Right - 112, r.Y + 8, 100, 24);
        if (u.HasPos)
        {
            Theme.DrawText(g, u.Speed + " km/s", Theme.Bold, u.Speed > 3 ? Theme.Text : Theme.Muted, right, TextFormatFlags.Right);
        }
        Theme.DrawText(g, TimeAgo(u.Time), Theme.Small, Theme.Muted, new Rectangle(r.Right - 112, r.Y + 34, 100, 20), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    // ---- Hərəkət tarixçəsi
    static readonly string[] Periods = { "Son 1 saat", "Son 6 saat", "Bu gün", "Dünən", "Son 24 saat", "Son 3 gün", "Son 7 gün" };
    readonly PillButton trackPeriodButton = new PillButton("Bu gün  ▾", 112, 34);
    readonly PillButton trackClose = new PillButton("✕", 34, 34);
    readonly System.Windows.Forms.Timer trackDebounce = new System.Windows.Forms.Timer { Interval = 350 };
    string trackPeriod = "Bu gün";
    bool trackEnabled = true, trackLive;
    Unit trackUnit;
    int trackSeq;

    void PeriodRange(string period, out long from, out long to)
    {
        DateTime now = DateTime.Now, today = DateTime.Today;
        DateTime f = now, t = now;
        switch (period)
        {
            case "Son 1 saat": f = now.AddHours(-1); break;
            case "Son 6 saat": f = now.AddHours(-6); break;
            case "Dünən": f = today.AddDays(-1); t = today; break;
            case "Son 24 saat": f = now.AddHours(-24); break;
            case "Son 3 gün": f = now.AddDays(-3); break;
            case "Son 7 gün": f = now.AddDays(-7); break;
            default: f = today; break;   // Bu gün
        }
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        from = (long)(f.ToUniversalTime() - epoch).TotalSeconds;
        to = (long)(t.ToUniversalTime() - epoch).TotalSeconds;
    }

    void ShowPeriodMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (string p in Periods)
        {
            string period = p;
            var item = new ToolStripMenuItem(period) { Checked = period == trackPeriod };
            item.Click += (s, e) => { trackPeriod = period; LoadTrack(); };
            menu.Items.Add(item);
        }
        menu.Show(trackPeriodButton, new Point(0, trackPeriodButton.Height + 4));
    }

    void LoadTrack()
    {
        Unit u = SelectedUnit();
        if (u == null) return;
        int seq = ++trackSeq;
        trackUnit = u;
        trackLive = trackPeriod != "Dünən";
        trackPeriodButton.Text = trackPeriod + "  ▾";
        trackPeriodButton.Width = TextRenderer.MeasureText(trackPeriodButton.Text, trackPeriodButton.Font).Width + 30;
        trackClose.Left = trackPeriodButton.Right + 6;
        trackPeriodButton.Visible = trackClose.Visible = true;
        map.TrackInfo = u.Name + "  •  yüklənir...";
        map.SetTrack(null, false);
        long from, to;
        PeriodRange(trackPeriod, out from, out to);
        long id = u.Id;
        string name = u.Name;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<TrackPoint> pts = null;
            string error = null;
            try { pts = api.LoadTrack(id, from, to); }
            catch (WialonException ex) { error = ex.Code == 7 ? "tarixçəyə baxmaq icazəsi yoxdur" : ex.Message; }
            catch (Exception ex) { error = ex.Message; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (seq != trackSeq) return;   // bu arada başqa maşın seçilib
                    if (error != null) { map.TrackInfo = name + "  •  " + error; map.Invalidate(); Log.Write("tarixçə: " + error); return; }
                    if (pts.Count < 2) { map.TrackInfo = name + "  •  bu müddətdə hərəkət yoxdur"; map.Invalidate(); return; }
                    double km = 0;
                    int maxSpeed = 0;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        km += DistanceKm(pts[i - 1], pts[i]);
                        maxSpeed = Math.Max(maxSpeed, pts[i].Speed);
                    }
                    map.TrackInfo = name + "  •  " + km.ToString("0.0") + " km  •  maks " + maxSpeed + " km/s";
                    map.SetTrack(pts, true);
                    TrimMemory();
                }));
            }
            catch { }
        });
    }

    void CloseTrack()
    {
        trackSeq++;
        trackUnit = null;
        trackPeriodButton.Visible = trackClose.Visible = false;
        map.TrackInfo = null;
        map.SetTrack(null, false);
    }

    static double DistanceKm(TrackPoint a, TrackPoint b)
    {
        double rad = Math.PI / 180, dLat = (b.Lat - a.Lat) * rad, dLon = (b.Lon - a.Lon) * rad;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(a.Lat * rad) * Math.Cos(b.Lat * rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    }

    // ---- Siyahı görünüşləri: 0 kart, 1 kompakt, 2 şəbəkə
    void ApplyView(int mode)
    {
        if (mode == 1) { unitBox.DrawItem = DrawUnitCompact; unitBox.SetLayout(36, 0); }
        else if (mode == 2) { unitBox.DrawItem = DrawUnitTile; unitBox.SetLayout(82, 150); }
        else { unitBox.DrawItem = DrawUnitCard; unitBox.SetLayout(62, 0); }
        try { File.WriteAllText(ViewFile, mode.ToString()); } catch { }
    }

    // Kompakt: bir sətir - nöqtə, ad, sürət, vaxt
    void DrawUnitCompact(Graphics g, object o, Rectangle r, bool selected, bool hover)
    {
        var u = (Unit)o;
        Color c;
        StateOf(u, out c);
        var inner = new RectangleF(r.X + 2, r.Y + 2, r.Width - 6, r.Height - 4);
        if (selected) Theme.FillRound(g, Theme.Soft, inner, 10);
        else if (hover) Theme.FillRound(g, Theme.Hover, inner, 10);
        if (selected) Theme.FillRound(g, Theme.Dark, new RectangleF(r.X + 2, r.Y + 9, 3, r.Height - 18), 1.5f);
        using (var b = new SolidBrush(c)) g.FillEllipse(b, r.X + 14, r.Y + r.Height / 2 - 4, 8, 8);
        Theme.DrawText(g, u.Name, Theme.SmallBold, Theme.Text, new Rectangle(r.X + 30, r.Y, r.Width - 170, r.Height), TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, TimeAgo(u.Time), Theme.Small, Theme.Muted, new Rectangle(r.Right - 96, r.Y, 84, r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        if (u.HasPos && u.Speed > 3)
            Theme.DrawText(g, u.Speed + " km/s", Theme.SmallBold, Theme.Text, new Rectangle(r.Right - 170, r.Y, 70, r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    // Şəbəkə: kiçik plitə - ad, vəziyyət, sürət və vaxt
    void DrawUnitTile(Graphics g, object o, Rectangle r, bool selected, bool hover)
    {
        var u = (Unit)o;
        Color c;
        string state = StateOf(u, out c);
        var tile = new RectangleF(r.X + 3, r.Y + 3, r.Width - 7, r.Height - 7);
        Theme.FillRound(g, selected ? Theme.Soft : hover ? Theme.Hover : Theme.Card, tile, 12);
        if (selected) using (var p = new Pen(Theme.Dark, 1.5f)) using (var path = Theme.Round(tile, 12)) g.DrawPath(p, path);
        else Theme.StrokeRound(g, Theme.Border, tile, 12);

        int x = (int)tile.X + 10, w = (int)tile.Width - 20;
        using (var b = new SolidBrush(c)) g.FillEllipse(b, x, tile.Y + 14, 8, 8);
        Theme.DrawText(g, u.Name, Theme.SmallBold, Theme.Text, new Rectangle(x + 13, (int)tile.Y + 8, w - 13, 20), TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, state, Theme.Small, c == Theme.Stopped ? Theme.Muted : c, new Rectangle(x, (int)tile.Y + 30, w, 18), TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, u.HasPos ? u.Speed + " km/s" : "", Theme.SmallBold, u.Speed > 3 ? Theme.Text : Theme.Muted,
            new Rectangle(x, (int)tile.Y + 50, w / 2, 18), TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, TimeAgo(u.Time), Theme.Small, Theme.Muted, new Rectangle(x + w / 2, (int)tile.Y + 50, w - w / 2, 18),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    void DrawAlertCard(Graphics g, object o, Rectangle r, bool selected, bool hover)
    {
        // Yığcam: rəngli nöqtə | mətn (1-ci sətir) + maşın • ad (2-ci sətir) | saat
        var a = (Alert)o;
        var inner = new RectangleF(r.X + 1, r.Y + 2, r.Width - 3, r.Height - 4);
        if (selected || hover) Theme.FillRound(g, selected ? Theme.Soft : Theme.Hover, inner, 10);
        else using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.X + 8, r.Bottom - 1, r.Right - 8, r.Bottom - 1);
        using (var b = new SolidBrush(a.Color)) g.FillEllipse(b, r.X + 10, r.Y + 11, 10, 10);
        DateTime when = FromUnix(a.Time);
        string time = when.Date == DateTime.Today ? when.ToString("HH:mm") : when.ToString("dd.MM HH:mm");
        int tw = TextRenderer.MeasureText(g, time, Theme.SmallBold).Width + 6;
        Theme.DrawText(g, a.Text, Theme.SmallBold, Theme.Text, new Rectangle(r.X + 28, r.Y + 4, r.Width - 36 - tw, 20), TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, time, Theme.SmallBold, Theme.Muted, new Rectangle(r.Right - tw - 8, r.Y + 4, tw, 20), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        Unit au;
        string sub = (units.TryGetValue(a.UnitId, out au) ? au.Name : "") +
                     (string.IsNullOrEmpty(a.Name) ? "" : (au != null ? "  •  " : "") + a.Name);
        Theme.DrawText(g, sub, Theme.Small, Theme.Muted, new Rectangle(r.X + 28, r.Y + 23, r.Width - 36, 18), TextFormatFlags.VerticalCenter);
    }

    void UpdateAlertTitle()
    {
        alertWin.TitleLabel.Text = "Bildirişlər  " + alertBox.Items.Count;
    }

    // ---- Xəritə növləri
    const string LayerOsm = "OpenStreetMap", LayerServer = "Server xəritəsi", LayerTopo = "Relyef (OpenTopoMap)",
                 LayerSentinel = "Peyk — Sentinel-2 (açarsız, az detallı)", LayerEsri = "Hibrid — Esri (pulsuz açar)",
                 LayerMapbox = "Hibrid — Mapbox (pulsuz açar)", LayerGoogle = "Hibrid — Google (açar)";
    static readonly string LayerFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layer.txt");
    static readonly string GoogleKeyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "google_key.txt");
    static readonly string EsriKeyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "esri_key.txt");
    static readonly string MapboxKeyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mapbox_token.txt");
    readonly Dictionary<string, TileSource> keyedTiles = new Dictionary<string, TileSource>();
    string currentLayer = LayerOsm;

    static string SavedLayer()
    {
        try { return File.Exists(LayerFile) ? File.ReadAllText(LayerFile).Trim() : null; } catch { return null; }
    }

    void ShowLayerMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (string name in layers)
        {
            string n = name;
            if (n == LayerSentinel || n == LayerEsri) menu.Items.Add(new ToolStripSeparator());   // peyk qrupu
            var item = new ToolStripMenuItem(n) { Checked = n == currentLayer };
            item.Click += (s, e) => ChangeLayer(n);
            menu.Items.Add(item);
        }
        menu.Show(layerButton, new Point(layerButton.Width, layerButton.Height + 4), ToolStripDropDownDirection.BelowLeft);
    }

    void UpdateLayerButton()
    {
        string label = currentLayer;
        int dash = label.IndexOf(" (");
        if (dash > 0) label = label.Substring(0, dash);
        layerButton.Text = label + "  ▾";
        layerButton.Width = Math.Max(150, TextRenderer.MeasureText(layerButton.Text, layerButton.Font).Width + 34);
        layerButton.Left = map.Width - layerButton.Width - 14;
        foreach (Control c in map.Controls)
            if (c is PillButton && ((PillButton)c).Text == "Hamısını göstər") c.Left = layerButton.Left - c.Width - 8;
    }

    void ChangeLayer(string name)
    {
        if (name == currentLayer) return;
        TileSource src = null;
        if (name == LayerOsm) src = TileSource.Osm;
        else if (name == LayerServer) src = serverTiles;
        else if (name == LayerTopo) src = TileSource.OpenTopo;
        else if (name == LayerSentinel) src = TileSource.Sentinel;
        else src = GetKeyedTiles(name);
        if (src == null) return;   // əvvəlki xəritə qalır
        currentLayer = name;
        map.Source = src;
        UpdateLayerButton();
        try { File.WriteAllText(LayerFile, name); } catch { }
    }

    // Açar tələb edən mənbələr: açar faylı boşdursa, necə almaq olduğunu izah edir
    TileSource GetKeyedTiles(string name)
    {
        TileSource cached;
        if (keyedTiles.TryGetValue(name, out cached)) return cached;
        string file, help;
        if (name == LayerEsri)
        {
            file = EsriKeyFile;
            help = "Esri peyk hibridi pulsuzdur (ayda 2 milyon kafel), amma Esri-nin şərtlərinə görə açar lazımdır.\n\n" +
                   "1. location.arcgis.com saytında pulsuz ArcGIS Location Platform hesabı açın.\n" +
                   "2. Developer dashboard → API keys → yeni açar yaradın.\n" +
                   "3. İcazələrdə \"Basemaps\" (Basemap styles / tiles) seçin.\n" +
                   "4. Açarı bu fayla yazıb yadda saxlayın:\n   " + file;
        }
        else if (name == LayerMapbox)
        {
            file = MapboxKeyFile;
            help = "Mapbox peyk hibridi pulsuzdur (ayda 200 000 kafel), amma Mapbox-un şərtlərinə görə token lazımdır.\n\n" +
                   "1. account.mapbox.com saytında pulsuz hesab açın.\n" +
                   "2. Tokens bölməsindəki \"Default public token\"-i (pk. ilə başlayır) kopyalayın.\n" +
                   "3. Tokeni bu fayla yazıb yadda saxlayın:\n   " + file;
        }
        else
        {
            file = GoogleKeyFile;
            help = "Google Hybrid üçün Google Maps API açarı lazımdır (Google-un şərtlərinə görə).\n\n" +
                   "1. console.cloud.google.com saytında layihə yaradın və ödəniş hesabı qoşun\n" +
                   "   (Map Tiles API-nin aylıq pulsuz limiti var).\n" +
                   "2. \"Map Tiles API\"-ni aktiv edin.\n" +
                   "3. \"Credentials\" bölməsində API açarı yaradın.\n" +
                   "4. Açarı bu fayla yazıb yadda saxlayın:\n   " + file;
        }
        string key = "";
        try { if (File.Exists(file)) key = File.ReadAllText(file).Trim(); } catch { }
        if (key.Length == 0)
        {
            MessageBox.Show(this, help + "\n\nSonra xəritə menyusundan bu növü yenidən seçin.", name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { if (!File.Exists(file)) File.WriteAllText(file, ""); } catch { }
            return null;
        }

        TileSource src;
        string error = null;
        Cursor = Cursors.WaitCursor;
        if (name == LayerEsri) src = TileSource.CreateEsriHybrid(key);
        else if (name == LayerMapbox) src = TileSource.CreateMapboxHybrid(key);
        else src = TileSource.CreateGoogleHybrid(key, out error);
        // Açarı bir kafel ilə yoxla - səhvdirsə, boş xəritə əvəzinə aydın mesaj
        if (src != null && name != LayerGoogle)
        {
            try
            {
                byte[] test = TileSource.Download(src.Url(2615, 1543, 12));
                if (!TileSource.IsImage(test)) error = "Server şəkil qaytarmadı: " + Encoding.UTF8.GetString(test, 0, Math.Min(300, test.Length));
            }
            catch (WebException ex) { error = ex.Message; }
            if (error != null) src = null;
        }
        Cursor = Cursors.Default;
        if (src == null)
        {
            Log.Write("xəritə: " + name + " alınmadı: " + error);
            MessageBox.Show(this, "Açar işləmədi:\n" + error + "\n\nAçarı yoxlayın: " + file, name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        keyedTiles[name] = src;
        return src;
    }
    // Wialon serverinin öz xəritəsi işləyirsə, onu əlavə edir (və ya seçir)
    void DetectServerMap()
    {
        double lat = 40.4093, lon = 49.8671;
        foreach (var u in units.Values) if (u.HasPos) { lat = u.Lat; lon = u.Lon; break; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            TileSource src = TileSource.DetectServer(api, lat, lon);
            if (src == null) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    serverTiles = src;
                    if (!layers.Contains(LayerServer)) layers.Insert(1, LayerServer);
                    string saved = SavedLayer();
                    if (saved == LayerServer || saved == null) ChangeLayer(LayerServer);
                }));
            }
            catch { }
        });
    }

    // ---- Arxa planda hadisələri (yeni mövqe, bildiriş) hər 2 saniyədən bir alır
    void PollLoop()
    {
        while (running)
        {
            try
            {
                object ev = api.Events();
                BeginInvoke((Action)(() => ApplyEvents(ev)));
                GeocodePending();
                Thread.Sleep(2000);
            }
            catch (WialonException ex)
            {
                if (ex.Code == 1 && TryRelogin()) return;   // yeni sessiya ilə yeni axın başladı
                BeginInvoke((Action)(() => status.Text = "Xəta: " + ex.Message + " Proqramı yenidən açın."));
                return;
            }
            catch (Exception)
            {
                if (!running) return;
                try { BeginInvoke((Action)(() => status.Text = "İnternet bağlantısı yoxdur, yenidən cəhd edilir...")); } catch { }
                Thread.Sleep(5000);
            }
        }
    }

    bool TryRelogin()
    {
        string token;
        if (!Credentials.Load(out token)) return false;
        try
        {
            api.Login(token);
            Invoke((Action)(() => { units.Clear(); resources.Clear(); unitBox.SetItems(new object[0]); Start(); }));
            return true;
        }
        catch { return false; }
    }

    void ApplyEvents(object ev)
    {
        var root = ev as Dictionary<string, object>;
        if (root == null || !root.ContainsKey("events")) return;
        bool structure = false;
        foreach (object o in AsArray(root["events"]))
        {
            var e = o as Dictionary<string, object>;
            if (e == null) continue;
            long id = Convert.ToInt64(e["i"]);
            string type = Convert.ToString(e["t"]);
            var d = e.ContainsKey("d") ? e["d"] as Dictionary<string, object> : null;
            Unit u;
            if (type == "d")
            {
                if (units.Remove(id)) structure = true;
            }
            else if (d != null && units.TryGetValue(id, out u))
            {
                if (d.ContainsKey("nm")) structure = true;
                ApplyUnitData(u, d);
            }
            else if (d != null && type == "m" && (resources.Contains(id) || d.ContainsKey("txt")))
            {
                string tp = d.ContainsKey("tp") ? Convert.ToString(d["tp"]) : "";
                if (unknownLogged++ < 100) Log.Write("bildiriş hadisəsi: i=" + id + " tp=" + tp + " açarlar=" + string.Join(",", new List<string>(d.Keys).ToArray()));
                AddAlert(d);
            }
            else if (type == "u" && d != null && (d.ContainsKey("unfu") || d.ContainsKey("ld")))
            {
                // xidməti yeniləmələr (bildiriş siyahısı, son giriş) - lazım deyil
            }
            else if (unknownLogged++ < 100)
            {
                // Tanınmayan hadisə - bildirişlər başqa formatda gəlirsə, buradan görünəcək
                Log.Write("naməlum hadisə: i=" + id + " t=" + type + (d != null ? " açarlar=" + string.Join(",", new List<string>(d.Keys).ToArray()) +
                          (d.ContainsKey("tp") ? " tp=" + d["tp"] : "") : ""));
            }
        }
        if (structure || filter != null) RebuildUnitList();
        else { unitBox.Invalidate(); RefreshCounts(); }
        map.Invalidate();
    }

    void ApplyUnitData(Unit u, Dictionary<string, object> d)
    {
        if (d.ContainsKey("nm")) u.Name = Convert.ToString(d["nm"]);
        var pos = d.ContainsKey("pos") ? d["pos"] as Dictionary<string, object> : null;
        if (pos != null)
        {
            double lat = Convert.ToDouble(pos["y"]), lon = Convert.ToDouble(pos["x"]);
            bool moved = !u.HasPos || Math.Abs(lat - u.Lat) > 0.0005 || Math.Abs(lon - u.Lon) > 0.0005;
            u.Lat = lat;
            u.Lon = lon;
            u.HasPos = true;
            u.Speed = pos.ContainsKey("s") ? Convert.ToInt32(pos["s"]) : 0;
            if (pos.ContainsKey("c")) u.Course = Convert.ToSingle(pos["c"]);
            if (moved) QueueGeocode(u);
            if (u == trackUnit && trackLive)
                map.AppendTrack(new TrackPoint { Lat = lat, Lon = lon, Speed = u.Speed, Time = Now() });
        }
        long t = 0;
        if (d.ContainsKey("t")) t = Convert.ToInt64(d["t"]);
        else if (pos != null && pos.ContainsKey("t")) t = Convert.ToInt64(pos["t"]);
        else if (d.ContainsKey("lmsg") && d["lmsg"] is Dictionary<string, object>)
        {
            var lm = (Dictionary<string, object>)d["lmsg"];
            if (lm.ContainsKey("t")) t = Convert.ToInt64(lm["t"]);
        }
        if (t > u.Time) u.Time = t;
    }

    static string StateKey(Unit u)
    {
        if (u.Time == 0 || Now() - u.Time > 3600) return "off";
        return u.Speed > 3 ? "move" : "stop";
    }

    static string StateOf(Unit u, out Color color)
    {
        switch (StateKey(u))
        {
            case "off": color = Theme.Offline; return "Əlaqə yoxdur";
            case "move": color = Theme.Moving; return "Hərəkətdə";
            default: color = Theme.Stopped; return "Dayanıb";
        }
    }

    void RefreshTimes()
    {
        if (filter != null) RebuildUnitList();
        else { unitBox.Invalidate(); RefreshCounts(); }
        map.Invalidate();
    }

    void AddAlert(Dictionary<string, object> d)
    {
        string text = d.ContainsKey("txt") ? Convert.ToString(d["txt"]).Trim() : null;
        string name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : (d.ContainsKey("nm") ? Convert.ToString(d["nm"]) : null);
        if (string.IsNullOrEmpty(text)) text = name ?? Wialon.ToJson(d);
        text = text.Replace("\r", " ").Replace("\n", " ");
        long t = d.ContainsKey("t") ? Convert.ToInt64(d["t"]) : Now();

        var alert = new Alert { Time = t, Text = text, Name = name };
        try { if (d.ContainsKey("unit")) alert.UnitId = Convert.ToInt64(d["unit"]); } catch { }
        try
        {
            if (d.ContainsKey("x") && d.ContainsKey("y"))
            {
                alert.Lon = Convert.ToDouble(d["x"]);
                alert.Lat = Convert.ToDouble(d["y"]);
                alert.HasPos = alert.Lat != 0 || alert.Lon != 0;
            }
        }
        catch { }
        alert.Color = ParseColor(d.ContainsKey("color") ? Convert.ToString(d["color"]) : null);

        var list = new List<object>(alertBox.Items);
        list.Insert(0, alert);
        if (list.Count > 200) list.RemoveRange(200, list.Count - 200);
        alertBox.SetItems(list);
        UpdateAlertTitle();

        PlayAlertSound();
        tray.ShowBalloonTip(5000, "Bildiriş", text.Length > 200 ? text.Substring(0, 200) + "…" : text, ToolTipIcon.Info);
        FlashWindow(Handle, true);
        ShowAlertWindow(false);
    }

    // Üzən pəncərəni göstər. byUser = menyudan açılıb (gizlətmə seçimini ləğv edir)
    void ShowAlertWindow(bool byUser)
    {
        if (byUser) { alertWin.WantVisible = true; alertWin.SaveState(); }
        if (!alertWin.WantVisible) return;
        if (!alertWin.Visible) alertWin.Show();
        if (byUser) alertWin.Activate();
    }

    // Bildirişə klik: maşını seç, xəritəni hadisənin yerinə apar
    void ShowAlertOnMap(Alert a)
    {
        if (a == null) return;
        Unit u;
        if (units.TryGetValue(a.UnitId, out u))
        {
            map.Selected = u;
            if (unitBox.Items.Contains(u)) unitBox.SelectedItem = u;
        }
        if (a.HasPos) map.CenterOn(a.Lat, a.Lon, 16);
        else if (u != null) map.CenterOn(u, 16);
        map.Invalidate();
    }

    // Wialon rəngi: "#RRGGBB", "RRGGBB" və ya ədəd. Çox açıq rənglər oxunmur, onda qara.
    static Color ParseColor(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return Theme.Dark;
            s = s.Trim().TrimStart('#');
            int v = s.Length == 6 || s.Length == 8
                ? int.Parse(s.Length == 8 ? s.Substring(2) : s, System.Globalization.NumberStyles.HexNumber)
                : int.Parse(s) & 0xFFFFFF;
            Color c = Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return c.GetBrightness() > 0.85f ? Theme.Dark : c;
        }
        catch { return Theme.Dark; }
    }

    void PlayAlertSound()
    {
        try
        {
            AppVolume.PresetDefaultSession(volume);
            using (var player = new SoundPlayer(soundFile)) player.Play();
        }
        catch { SystemSounds.Asterisk.Play(); }
    }

    // ---- Ünvanlar: arxa axında, maşın yerini dəyişəndə
    void QueueGeocode(Unit u)
    {
        string key = u.Lat.ToString("0.000") + "," + u.Lon.ToString("0.000");
        string cached;
        lock (addressCache)
        {
            if (addressCache.TryGetValue(key, out cached)) { u.Address = cached; return; }
        }
        u.Address = null;
        lock (geocodeQueue) { if (!geocodeQueue.Contains(u)) geocodeQueue.Enqueue(u); }
    }

    void GeocodePending()
    {
        if (geocodeFailures >= 3) return;     // server ünvan vermirsə, koordinat göstərilir
        for (int n = 0; n < 10; n++)
        {
            Unit u;
            lock (geocodeQueue) { if (geocodeQueue.Count == 0) return; u = geocodeQueue.Dequeue(); }
            double lat = u.Lat, lon = u.Lon;
            try
            {
                string addr = api.Geocode(lat, lon);
                if (addr == null) continue;
                lock (addressCache) addressCache[lat.ToString("0.000") + "," + lon.ToString("0.000")] = addr;
                BeginInvoke((Action)(() => { u.Address = addr; unitBox.Invalidate(); }));
                geocodeFailures = 0;
            }
            catch { geocodeFailures++; return; }
        }
    }

    // ---- Köməkçilər
    Unit SelectedUnit()
    {
        return unitBox.SelectedItem as Unit;
    }

    void OpenMap()
    {
        Unit u = SelectedUnit();
        if (u == null || !u.HasPos) return;
        string lat = u.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string lon = u.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Process.Start("https://www.openstreetmap.org/?mlat=" + lat + "&mlon=" + lon + "#map=16/" + lat + "/" + lon);
    }

    void CopyCoords()
    {
        Unit u = SelectedUnit();
        if (u != null && u.HasPos) Clipboard.SetText(Coords(u));
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void UpdateRam()
    {
        ram.Text = "RAM  " + (Process.GetCurrentProcess().WorkingSet64 / 1048576) + " MB";
    }

    static void TrimMemory()
    {
        GC.Collect();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
    }

    static string Coords(Unit u)
    {
        return u.Lat.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture) + ", " +
               u.Lon.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture);
    }

    static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }
    static DateTime FromUnix(long t) { return new DateTime(1970, 1, 1).AddSeconds(t).ToLocalTime(); }

    static string TimeAgo(long t)
    {
        if (t == 0) return "";
        long s = Now() - t;
        if (s < 60) return "indicə";
        if (s < 3600) return (s / 60) + " dəq əvvəl";
        if (s < 86400) return (s / 3600) + " saat əvvəl";
        return FromUnix(t).ToString("dd.MM.yyyy HH:mm");
    }

    static object[] AsArray(object o) { return o as object[] ?? new object[0]; }

    // ---------------------------------------------------------------- Başlanğıc
    [STAThread]
    static void Main()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();
        ToolStripManager.Renderer = new ToolStripProfessionalRenderer(new Theme.MenuColors());
        string dir = AppDomain.CurrentDomain.BaseDirectory;

        string server = "";       // ilk girişdə soruşulur və server.txt-də saxlanır
        string serverCfg = Path.Combine(dir, "server.txt");
        if (File.Exists(serverCfg) && File.ReadAllText(serverCfg).Trim().Length > 0) server = File.ReadAllText(serverCfg).Trim();

        float percent = 5;
        string volCfg = Path.Combine(dir, "volume.txt");
        float parsed;
        if (File.Exists(volCfg) && float.TryParse(File.ReadAllText(volCfg).Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
            percent = Math.Max(0, Math.Min(100, parsed));

        string sound = Path.Combine(dir, "sound.wav");
        if (!File.Exists(sound)) sound = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Windows Notify System Generic.wav");

        var api = new Wialon(server);

        // Yadda saxlanan girişlə avtomatik daxil ol, alınmasa giriş pəncərəsi
        string token;
        bool loggedIn = false;
        string savedError = null;
        if (server.Length > 0 && Credentials.Load(out token))
        {
            try { api.Login(token); loggedIn = true; }
            catch (Exception ex) { savedError = ex.Message; }
        }
        if (!loggedIn)
        {
            LoginForm form = null;
            form = new LoginForm(server, t =>
            {
                try
                {
                    api.Host = form.ServerUrl;
                    api.Login(t);
                    try { File.WriteAllText(serverCfg, api.Host); } catch { }
                    if (form.Remember.Checked) Credentials.Save(t); else Credentials.Clear();
                    return null;
                }
                catch (WebException) { return "Serverə qoşulmaq olmadı. İnterneti yoxlayın."; }
                catch (Exception ex) { return ex.Message; }
            });
            if (savedError != null) form.ShowError("Saxlanmış giriş işləmədi: " + savedError);
            if (form.ShowDialog() != DialogResult.OK) return;
        }

        Application.Run(new MainForm(api, percent / 100f, sound));
    }
}

// ---------------------------------------------------------------- Səs səviyyəsi
// Prosesin Windows səs sessiyasını təyin edir (Səs qarışdırıcısındakı kimi).
// Sessiya səviyyəsi ümumi səsə nisbətəndir: 0.05 = kompüter səsinin 5%-i.
public static class AppVolume
{
    static readonly HashSet<string> done = new HashSet<string>();
    static readonly List<object> keepAlive = new List<object>();
    static bool preset;

    // Səs çalınmazdan əvvəl prosesin standart sessiyasını yaradıb səviyyəni qoyur
    public static void PresetDefaultSession(float level)
    {
        if (preset) return;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devices;
        if (enumerator.EnumAudioEndpoints(0, 1, out devices) != 0) return;
        uint count;
        devices.GetCount(out count);
        Guid iid = typeof(IAudioSessionManager2).GUID;
        for (uint i = 0; i < count; i++)
        {
            IMMDevice device;
            object o;
            if (devices.Item(i, out device) != 0) continue;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) continue;
            ISimpleAudioVolume vol;
            if (((IAudioSessionManager2)o).GetSimpleAudioVolume(IntPtr.Zero, 0, out vol) == 0)
            {
                Guid ctx = Guid.Empty;
                vol.SetMasterVolume(level, ref ctx);
                keepAlive.Add(vol);
                keepAlive.Add(o);
            }
        }
        preset = true;
    }

    public static void ApplyToNewSessions(uint pid, float level)
    {
        try
        {
            foreach (var info in EnumSessions())
            {
                if (info.Pid == pid && !done.Contains(info.Id))
                {
                    var vol = info.Control as ISimpleAudioVolume;
                    Guid ctx = Guid.Empty;
                    if (vol != null && vol.SetMasterVolume(level, ref ctx) == 0) done.Add(info.Id);
                }
                Marshal.ReleaseComObject(info.Control);
            }
        }
        catch { }
    }

    // Yoxlama üçün: bütün səs sessiyalarını "pid səviyyə" kimi qaytarır
    public static string[] List()
    {
        var result = new List<string>();
        foreach (var info in EnumSessions())
        {
            var vol = info.Control as ISimpleAudioVolume;
            float level = -1;
            if (vol != null) vol.GetMasterVolume(out level);
            result.Add(info.Pid + " " + level.ToString("0.00"));
            Marshal.ReleaseComObject(info.Control);
        }
        return result.ToArray();
    }

    struct SessionInfo { public uint Pid; public string Id; public IAudioSessionControl2 Control; }

    static List<SessionInfo> EnumSessions()
    {
        var list = new List<SessionInfo>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devices;
        if (enumerator.EnumAudioEndpoints(0 /*render*/, 1 /*active*/, out devices) != 0) return list;
        uint count;
        devices.GetCount(out count);
        Guid iid = typeof(IAudioSessionManager2).GUID;
        for (uint i = 0; i < count; i++)
        {
            IMMDevice device;
            if (devices.Item(i, out device) != 0) continue;
            object o;
            if (device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o) == 0)
            {
                var manager = (IAudioSessionManager2)o;
                IAudioSessionEnumerator sessions;
                if (manager.GetSessionEnumerator(out sessions) == 0)
                {
                    int n;
                    sessions.GetCount(out n);
                    for (int j = 0; j < n; j++)
                    {
                        IAudioSessionControl2 ctl;
                        if (sessions.GetSession(j, out ctl) != 0) continue;
                        uint pid; string id;
                        ctl.GetProcessId(out pid);
                        ctl.GetSessionInstanceIdentifier(out id);
                        list.Add(new SessionInfo { Pid = pid, Id = id ?? "", Control = ctl });
                    }
                    Marshal.ReleaseComObject(sessions);
                }
                Marshal.ReleaseComObject(manager);
            }
            Marshal.ReleaseComObject(device);
        }
        Marshal.ReleaseComObject(devices);
        Marshal.ReleaseComObject(enumerator);
        return list;
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out ISimpleAudioVolume volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolume(out float level);
}
