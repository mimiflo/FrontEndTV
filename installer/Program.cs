// FrontEndTV — installateur pour Windows : la fenêtre. Le travail avec la TV est dans Engine.cs.
// Développé par Florian. Écrit en C# 5 : il se compile avec le csc.exe livré avec Windows (voir build.sh).
//
// Options (facultatives) : -Ip 192.168.1.50 -Name Florian -Region FR -Key <clé TMDB> -Apk <fichier>
//                          -Original (remet l'accueil d'origine) -Auto (lance tout de suite, sans rien demander)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("FrontEndTV — installateur")]
[assembly: AssemblyDescription("Installe FrontEndTV sur une Google TV / Android TV")]
[assembly: AssemblyProduct("FrontEndTV")]
[assembly: AssemblyCompany("Florian")]
[assembly: AssemblyCopyright("Licence MIT — d'après Free My TV")]
[assembly: AssemblyVersion("1.2.0.0")]

namespace FrontEndTV.Setup {

static class Program {
    [STAThread]
    static int Main(string[] args) {
        try { SetProcessDPIAware(); } catch (Exception) {}   // net à 125 %, 150 %... (sinon Windows étire une image floue)
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Options o; bool auto;
        Parse(args, out o, out auto);
        Application.Run(new MainForm(o, auto));
        return 0;
    }

    static void Parse(string[] args, out Options o, out bool auto) {
        o = new Options();
        auto = false;
        for (int i = 0; i < args.Length; i++) {
            string a = args[i].TrimStart('-', '/').ToLowerInvariant();
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (a) {
                case "ip": o.Ip = next; i++; break;
                case "name": o.Name = next; i++; break;
                case "region": o.Region = next; i++; break;
                case "key": o.Key = next; i++; break;
                case "apk": o.Apk = next; i++; break;
                case "original": o.Original = true; break;
                case "auto": auto = true; break;
            }
        }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

/** Les couleurs : celles des réglages de FrontEndTV (fond nuit, violet). */
static class Palette {
    public static readonly Color Back = Color.FromArgb(16, 13, 24);
    public static readonly Color Card = Color.FromArgb(29, 24, 42);
    public static readonly Color Field = Color.FromArgb(37, 31, 54);
    public static readonly Color Border = Color.FromArgb(58, 50, 82);
    public static readonly Color Text = Color.FromArgb(245, 243, 250);
    public static readonly Color Dim = Color.FromArgb(160, 153, 184);
    public static readonly Color Accent = Color.FromArgb(139, 92, 246);
    public static readonly Color AccentHover = Color.FromArgb(160, 122, 250);
    public static readonly Color Green = Color.FromArgb(52, 211, 153);
    public static readonly Color Amber = Color.FromArgb(251, 191, 36);
    public static readonly Color Red = Color.FromArgb(248, 113, 113);
    public static readonly Color Cyan = Color.FromArgb(103, 192, 255);

    public static Color For(Tone t) {
        switch (t) {
            case Tone.Action: return Cyan;
            case Tone.Success: return Green;
            case Tone.Warning: return Amber;
            case Tone.Error: return Red;
            default: return Accent;
        }
    }

    /** `top` posé sur `under` avec l'opacité `alpha` (0-255). */
    public static Color Mix(Color under, Color top, int alpha) {
        return Color.FromArgb(under.R + (top.R - under.R) * alpha / 255, under.G + (top.G - under.G) * alpha / 255,
                under.B + (top.B - under.B) * alpha / 255);
    }

    public static GraphicsPath Round(RectangleF r, float radius) {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

sealed class MainForm : Form, IReport {
    readonly Options options;
    readonly bool auto;
    readonly float scale;
    readonly Font body, small, bold, title;
    Image logo;

    // Page 1 : les réglages
    Panel setup;
    Field ip, name, region, key;
    Label ipStatus;
    LinkLabel search;
    RoundButton go;

    // Page 2 : le déroulé
    Panel progress;
    Steps steps;
    Message message;
    RoundButton close, retry;
    Label pageTitle;
    Thread worker;
    readonly ManualResetEvent userDone = new ManualResetEvent(false);

    public MainForm(Options o, bool auto) {
        options = o;
        this.auto = auto;
        // L'échelle de l'écran (125 %, 150 %...), réduite si la fenêtre ne tiendrait pas en hauteur
        float dpi;
        using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96f;
        Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        scale = Math.Max(0.75f, Math.Min(dpi, (screen.Height - 56 * dpi) / Height0));
        // Polices en pixels, à la même échelle que tout le reste : rien ne déborde ni ne s'étire
        body = Px(14f, false);
        small = Px(12.5f, false);
        bold = Px(14.5f, true);
        title = Px(28f, true);

        Text = "FrontEndTV — installation";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Back;
        ForeColor = Palette.Text;
        Font = body;
        ClientSize = new Size(S(Width0), S(Height0));
        // L'icône en toutes tailles (16 à 192 px) : nette dans la barre de titre et la barre des tâches
        try {
            using (Stream s = Resource("app.ico")) if (s != null) Icon = new Icon(new MemoryStream(ReadAll(s)));
        } catch (Exception) {}
        try {
            using (Stream s = Resource("icon.png")) if (s != null) logo = Image.FromStream(new MemoryStream(ReadAll(s)));
        } catch (Exception) {}

        BuildSetup();
        BuildProgress();
        Controls.Add(setup);
        Controls.Add(progress);
        progress.Visible = false;
    }

    const int Width0 = 520, Height0 = 640;   // taille de la fenêtre à 100 %

    int S(float v) { return (int) Math.Round(v * scale); }

    Font Px(float px, bool semibold) {
        return new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", px * scale, GraphicsUnit.Pixel);
    }

    static Stream Resource(string name) {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
    }

    static byte[] ReadAll(Stream s) {
        var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        // Barre de titre sombre (Windows 10 20H1+ et 11)
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) {}
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        if (options.Original && auto) { Start(true); return; }
        if (ip.Value.Length == 0) Search(); else if (auto) Start(options.Original);
    }

    // MARK: - En-tête commun

    void AddHeader(Control page, string subtitle) {
        var head = new Header(logo, title, small, "FrontEndTV", subtitle, scale);
        head.Bounds = new Rectangle(S(32), S(26), S(456), S(64));
        page.Controls.Add(head);
    }

    Panel Page() {
        var p = new Panel();
        p.Bounds = new Rectangle(0, 0, S(Width0), S(Height0));
        p.BackColor = Palette.Back;
        return p;
    }

    Label AddLabel(Control parent, string text, Font font, Color color, int x, int y, int w, int h) {
        var l = new Label();
        l.Text = text;
        l.Font = font;
        l.ForeColor = color;
        l.BackColor = parent.BackColor;
        l.Bounds = new Rectangle(x, y, w, h);
        l.UseMnemonic = false;
        parent.Controls.Add(l);
        return l;
    }

    LinkLabel Link(Control parent, string text, int x, int y, int w, ContentAlignment align, LinkLabelLinkClickedEventHandler click) {
        var l = new LinkLabel();
        l.Text = text;
        l.Font = small;
        l.LinkColor = Palette.AccentHover;
        l.ActiveLinkColor = Palette.Text;
        l.VisitedLinkColor = Palette.AccentHover;
        l.LinkBehavior = LinkBehavior.HoverUnderline;
        l.BackColor = parent.BackColor;
        l.AutoSize = true;
        l.Location = new Point(x, y);
        l.LinkClicked += click;
        parent.Controls.Add(l);
        // Aligné à droite ou centré dans la largeur w, d'après sa taille réelle
        if (align == ContentAlignment.MiddleRight) l.Left = x + w - l.PreferredWidth;
        else if (align == ContentAlignment.MiddleCenter) l.Left = x + (w - l.PreferredWidth) / 2;
        return l;
    }

    // MARK: - Page 1 : réglages

    void BuildSetup() {
        setup = Page();
        AddHeader(setup, "Écran d'accueil pour Google TV / Android TV");

        var card = new Card(Palette.Card, scale);
        card.Bounds = new Rectangle(S(32), S(108), S(456), S(108));
        setup.Controls.Add(card);
        AddLabel(card, "Sur la TV, une seule fois", bold, Palette.Text, S(16), S(12), S(420), S(22));
        AddLabel(card, "1.  Paramètres → Système → À propos : appuie 7 fois sur « Build ».\n"
                + "2.  Options pour les développeurs : active « Débogage USB ».\n"
                + "3.  La TV et ce PC sont sur le même réseau (Wi-Fi ou câble).",
                small, Palette.Dim, S(16), S(38), S(428), S(62));

        int y = S(234);
        AddLabel(setup, "Adresse IP de la TV", bold, Palette.Text, S(32), y, S(300), S(22));
        search = Link(setup, "Rechercher", S(388), y + S(2), S(100), ContentAlignment.MiddleRight, (s, e) => { Touch(); Search(); });
        ip = new Field("ex. 192.168.1.50", scale, body);
        ip.Bounds = new Rectangle(S(32), y + S(26), S(456), S(40));
        ip.Value = options.Ip;
        setup.Controls.Add(ip);
        ipStatus = AddLabel(setup, "", small, Palette.Dim, S(32), y + S(70), S(456), S(20));
        ipStatus.AutoEllipsis = true;

        y = S(336);
        AddLabel(setup, "Ton prénom", bold, Palette.Text, S(32), y, S(300), S(22));
        AddLabel(setup, "Pays", bold, Palette.Text, S(368), y, S(120), S(22));
        name = new Field("facultatif, pour « Bonsoir, … »", scale, body);
        name.Bounds = new Rectangle(S(32), y + S(26), S(320), S(40));
        name.Value = options.Name.Length > 0 ? options.Name : Engine.Recall("name");
        setup.Controls.Add(name);
        region = new Field("FR", scale, body);
        region.Bounds = new Rectangle(S(368), y + S(26), S(120), S(40));
        region.Box.MaxLength = 2;
        region.Box.CharacterCasing = CharacterCasing.Upper;
        string r = options.Region.Length > 0 ? options.Region : Engine.Recall("region");
        region.Value = r.Length > 0 ? r : Engine.DefaultRegion();
        setup.Controls.Add(region);

        y = S(424);
        AddLabel(setup, "Clé TMDB", bold, Palette.Text, S(32), y, S(300), S(22));
        Link(setup, "Obtenir une clé gratuite", S(288), y + S(2), S(200), ContentAlignment.MiddleRight,
                (s, e) => { Touch(); Open("https://www.themoviedb.org/settings/api"); });
        key = new Field("facultatif — vide : la TV garde sa clé", scale, body);
        key.Bounds = new Rectangle(S(32), y + S(26), S(456), S(40));
        key.Value = options.Key;
        setup.Controls.Add(key);
        AddLabel(setup, "Pour les recommandations de films et séries. Elle reste sur ta TV.", small, Palette.Dim,
                S(32), y + S(70), S(456), S(20));

        go = new RoundButton(GoText, Palette.Accent, Palette.AccentHover, Color.White, scale);
        go.Font = Px(16f, true);
        go.Bounds = new Rectangle(S(32), S(528), S(456), S(50));
        go.Click += (s, e) => Start(false);
        setup.Controls.Add(go);
        Link(setup, "Remettre l'écran d'accueil d'origine", S(32), S(588), S(456), ContentAlignment.MiddleCenter, (s, e) => {
            Touch();
            if (MessageBox.Show(this, "Remettre l'écran d'accueil d'origine de la TV ?\nLes applis mises de côté reviennent comme avant.",
                    "FrontEndTV", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) Start(true);
        });
        AddLabel(setup, "par Florian · d'après Free My TV (licence MIT)", small, Color.FromArgb(110, 104, 132),
                S(32), S(612), S(456), S(20)).TextAlign = ContentAlignment.MiddleCenter;
        foreach (Field f in new[] { ip, name, region, key }) {
            f.Box.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Start(false); } else Touch();
            };
            f.Box.MouseDown += (s, e) => Touch();
            f.MouseDown += (s, e) => Touch();
        }
        rescan.Interval = 4000;
        rescan.Tick += (s, e) => {
            rescan.Stop();
            if (setup.Visible && ip.Value.Length == 0) Search();
        };
        countdown.Interval = 1000;
        countdown.Tick += (s, e) => {
            if (--remaining <= 0) { StopCountdown(); Start(false); } else go.Text = GoText + "  ·  " + remaining + " s";
        };
    }

    /** Cherche la TV : celle de la dernière fois, sinon tout le réseau local. */
    void Search() {
        rescan.Stop();
        if (searching) return;
        searching = true;
        search.LinkColor = Palette.Dim;
        Status("Recherche de la TV sur ton réseau...", Palette.Dim);
        var t = new Thread(() => {
            string last = Engine.LastTV();
            List<string> found = last != null ? new List<string> { last } : Engine.FindTVs();
            Post(() => {
                searching = false;
                search.LinkColor = Palette.AccentHover;
                if (found.Count == 0) {
                    // On recherche toute seule toutes les 4 s : dès que la TV répond, ça démarre
                    Status("Aucune TV trouvée pour l'instant — je continue de chercher...", Palette.Amber);
                    ip.Box.Focus();
                    rescan.Start();
                    return;
                }
                if (ip.Value.Length == 0 || ip.Value == lastSuggested) ip.Value = found[0];
                lastSuggested = found[0];
                if (auto) { Start(options.Original); return; }
                if (found.Count > 1) {
                    Status("✔ " + found.Count + " appareils trouvés : " + string.Join(", ", found) + " — choisis le bon.", Palette.Green);
                    return;
                }
                Status((last != null ? "✔ Ta TV de la dernière fois" : "✔ TV trouvée sur ton réseau")
                        + (touched ? "" : " — démarrage automatique"), Palette.Green);
                StartCountdown(last != null ? 4 : 8);
            });
        });
        t.IsBackground = true;
        t.Start();
    }
    string lastSuggested = "";
    bool searching;

    // MARK: - Démarrage automatique (s'arrête dès qu'on touche à un champ)

    const string GoText = "Installer FrontEndTV";
    readonly System.Windows.Forms.Timer countdown = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer rescan = new System.Windows.Forms.Timer();
    int remaining;
    bool touched;

    void StartCountdown(int seconds) {
        if (touched || (worker != null && worker.IsAlive)) return;
        remaining = seconds;
        go.Text = GoText + "  ·  " + remaining + " s";
        countdown.Start();
    }

    void StopCountdown() {
        countdown.Stop();
        go.Text = GoText;
    }

    /** On touche à un réglage : plus de démarrage automatique. */
    void Touch() {
        if (touched) return;
        touched = true;
        if (countdown.Enabled) {
            StopCountdown();
            if (ipStatus.Text.Contains(" — démarrage automatique")) {
                Status(ipStatus.Text.Substring(0, ipStatus.Text.IndexOf(" — démarrage automatique")), Palette.Green);
            }
        }
    }

    void Status(string text, Color color) {
        ipStatus.Text = text;
        ipStatus.ForeColor = color;
    }

    // MARK: - Page 2 : déroulé

    void BuildProgress() {
        progress = Page();
        AddHeader(progress, "Installation sur ta TV");
        pageTitle = AddLabel(progress, "", bold, Palette.Text, S(32), S(108), S(456), S(24));

        steps = new Steps(scale, bold, small);
        steps.Bounds = new Rectangle(S(32), S(144), S(456), S(3 * 64));
        progress.Controls.Add(steps);

        message = new Message(scale, body, Px(13.5f, true));
        message.Bounds = new Rectangle(S(32), S(356), S(456), S(140));
        message.Done.Click += (s, e) => { message.Done.Visible = false; userDone.Set(); };
        message.Visible = false;
        progress.Controls.Add(message);

        retry = new RoundButton("Réessayer", Palette.Accent, Palette.AccentHover, Color.White, scale);
        retry.Font = bold;
        retry.Bounds = new Rectangle(S(32), S(530), S(220), S(48));
        retry.Click += (s, e) => { progress.Visible = false; setup.Visible = true; go.Focus(); };
        progress.Controls.Add(retry);
        close = new RoundButton("Fermer", Palette.Field, Palette.Border, Palette.Text, scale);
        close.Font = bold;
        close.Bounds = new Rectangle(S(268), S(530), S(220), S(48));
        close.Click += (s, e) => Close();
        progress.Controls.Add(close);
        Link(progress, "Ouvrir le journal", S(32), S(596), S(456), ContentAlignment.MiddleCenter, (s, e) => Open(Engine.LogFile));
    }

    void Start(bool original) {
        StopCountdown();
        rescan.Stop();
        if (worker != null && worker.IsAlive) return;
        if (Engine.Normalize(ip.Value) == null) {
            Status(ip.Value.Length == 0 ? "Entre l'adresse IP de la TV (Paramètres → Réseau et Internet)."
                    : "Ça ne ressemble pas à une adresse IP (ex. 192.168.1.50).", Palette.Amber);
            ip.Box.Focus();
            return;
        }
        var o = new Options();
        o.Ip = ip.Value;
        o.Name = name.Value;
        o.Region = region.Value;
        o.Key = key.Value;
        o.Apk = options.Apk;
        o.Original = original;

        setup.Visible = false;
        progress.Visible = true;
        pageTitle.Text = original ? "Retour à l'écran d'accueil d'origine" : "Installation de FrontEndTV";
        steps.Reset(new[] { "Connexion à la TV", original ? "Application" : "Installation de l'appli",
                original ? "Accueil d'origine" : "Activation comme écran d'accueil" });
        Say(Tone.Info, null);
        retry.Visible = false;
        close.Text = "Annuler";
        close.Left = S(32);
        close.Width = S(456);

        worker = new Thread(() => {
            string done = null, error = null;
            try { done = new Engine(this, o).Run(); }
            catch (SetupException e) { error = e.Message; }
            catch (Exception e) { error = "Erreur inattendue : " + e.Message; }
            Post(() => Finish(done, error));
        });
        worker.IsBackground = true;   // fermer la fenêtre arrête tout
        worker.Start();
    }

    void Finish(string done, string error) {
        steps.FailRunning();
        if (error == null) {
            Say(Tone.Success, done);
            close.Text = "Terminer";
            close.Left = S(32);
            close.Width = S(456);
            close.Fill = Palette.Accent;
            close.Hover = Palette.AccentHover;
            close.Invalidate();
            close.Focus();
        } else {
            Say(Tone.Error, error);
            close.Text = "Fermer";
            close.Left = S(268);
            close.Width = S(220);
            retry.Visible = true;
            retry.Focus();
        }
    }

    // MARK: - IReport (appelé depuis le fil de travail)

    public void Step(int index, StepState state, string detail) {
        Post(() => steps.Set(index, state, detail));
    }

    public void Say(Tone tone, string text) {
        Action a = () => {
            message.Visible = text != null;
            message.Done.Visible = false;
            if (text != null) message.Display(tone, text);
        };
        if (InvokeRequired) Post(a); else a();
    }

    public void WaitForUser(string text, int seconds) {
        userDone.Reset();
        for (int s = seconds; s > 0; s--) {
            string line = text + "\nPas de message ? Appuie sur « C'est fait » (sinon je continue dans " + s + " s).";
            Post(() => {
                message.Visible = true;
                message.Display(Tone.Action, line);
                if (!message.Done.Visible && !userDone.WaitOne(0)) message.Done.Visible = true;
            });
            if (userDone.WaitOne(1000)) break;
        }
        Say(Tone.Info, null);
    }

    /** Exécute `a` dans le fil de la fenêtre (rien si elle est déjà fermée). */
    void Post(Action a) {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
        catch (InvalidOperationException) {}
    }

    static void Open(string target) {
        try { Process.Start(target); } catch (Exception) {}
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (worker != null && worker.IsAlive && e.CloseReason == CloseReason.UserClosing
                && MessageBox.Show(this, "L'installation est en cours. Arrêter ?", "FrontEndTV",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) {
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

// MARK: - Petits éléments dessinés à la main

/** Logo, titre et sous-titre. */
sealed class Header : Control {
    readonly Image logo;
    readonly Font big, small;
    readonly string line1, line2;
    readonly float scale;

    public Header(Image logo, Font big, Font small, string line1, string line2, float scale) {
        this.logo = logo; this.big = big; this.small = small; this.line1 = line1; this.line2 = line2; this.scale = scale;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Palette.Back;
    }

    Bitmap scaled;   // le logo réduit une fois, en haute qualité

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        int size = Height;
        if (logo != null) {
            if (scaled == null || scaled.Width != size) {
                scaled = new Bitmap(size, size);
                using (Graphics s = Graphics.FromImage(scaled)) {
                    s.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    s.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    s.CompositingQuality = CompositingQuality.HighQuality;
                    s.DrawImage(logo, new Rectangle(0, 0, size, size));
                }
            }
            // Remplir un chemin arrondi avec l'image : bords lissés (une découpe SetClip ferait des escaliers)
            using (var path = Palette.Round(new RectangleF(0, 0, size, size), size * 0.24f))
            using (var brush = new TextureBrush(scaled)) g.FillPath(brush, path);
        }
        int x = logo != null ? size + (int) (16 * scale) : 0;
        int h1 = TextRenderer.MeasureText(g, line1, big, Size.Empty, TextFormatFlags.NoPadding).Height;
        int h2 = TextRenderer.MeasureText(g, line2, small, Size.Empty, TextFormatFlags.NoPadding).Height;
        int top = Math.Max(0, (size - h1 - h2) / 2);
        TextRenderer.DrawText(g, line1, big, new Point(x, top), Palette.Text, Palette.Back, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, line2, small, new Point(x + (int) (1 * scale), top + h1), Palette.Dim, Palette.Back, TextFormatFlags.NoPadding);
    }
}

/** Fond arrondi. */
sealed class Card : Panel {
    readonly Color fill;
    readonly float scale;

    public Card(Color fill, float scale) {
        this.fill = fill; this.scale = scale;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = fill;
    }

    protected override void OnPaintBackground(PaintEventArgs e) {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Palette.Back);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Palette.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12 * scale))
        using (var b = new SolidBrush(fill)) e.Graphics.FillPath(b, path);
    }
}

/** Champ de saisie arrondi (le contour devient violet quand on écrit dedans). */
sealed class Field : Control {
    public readonly TextBox Box = new TextBox();
    readonly float scale;

    public Field(string cue, float scale, Font font) {
        this.scale = scale;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Back;
        Box.BorderStyle = BorderStyle.None;
        Box.BackColor = Palette.Field;
        Box.ForeColor = Palette.Text;
        Box.Font = font;
        Box.GotFocus += (s, e) => Invalidate();
        Box.LostFocus += (s, e) => Invalidate();
        Box.HandleCreated += (s, e) => {
            try { SendMessage(Box.Handle, 0x1501, (IntPtr) 1, cue); } catch (Exception) {}   // texte d'exemple, en gris
        };
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
    }

    public string Value {
        get { return Box.Text.Trim(); }
        set { Box.Text = value ?? ""; }
    }

    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        int pad = (int) (14 * scale);
        Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Width - 2 * pad, Box.PreferredHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        Box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Palette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Palette.Round(r, 10 * scale))
        using (var b = new SolidBrush(Palette.Field))
        using (var pen = new Pen(Box.Focused ? Palette.Accent : Palette.Border, Box.Focused ? 1.5f * scale : 1f)) {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
}

/** Bouton arrondi. */
sealed class RoundButton : Control {
    public Color Fill, Hover, Ink;
    readonly float scale;
    bool over, down;

    public RoundButton(string text, Color fill, Color hover, Color ink, float scale) {
        Text = text; Fill = fill; Hover = hover; Ink = ink; this.scale = scale;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Back;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
    protected override void OnKeyUp(KeyEventArgs e) {
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(BackColor);   // la couleur derrière les coins arrondis
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color c = !Enabled ? Palette.Border : over ? Hover : Fill;
        if (down) c = ControlPaint.Dark(c, 0.05f);
        using (var path = Palette.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 12 * scale))
        using (var b = new SolidBrush(c)) {
            g.FillPath(b, path);
            if (Focused && ShowFocusCues) using (var pen = new Pen(Color.FromArgb(150, Color.White), 1.5f * scale)) g.DrawPath(pen, path);
        }
        // Le texte avec sa couleur de fond (ClearType net), sur sa seule surface : les coins restent arrondis
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        Size text = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
        var at = new Point((Width - text.Width) / 2, (Height - text.Height) / 2);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(at, text), Ink, c, flags);
    }
}

/** Les étapes, avec leur pastille : en attente, en cours (qui tourne), faite, en échec. */
sealed class Steps : Control {
    readonly float scale;
    readonly Font name, detail;
    string[] titles = new string[0], details = new string[0];
    StepState[] states = new StepState[0];
    readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer();
    float angle;

    public Steps(float scale, Font name, Font detail) {
        this.scale = scale; this.name = name; this.detail = detail;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Back;
        spin.Interval = 33;
        spin.Tick += (s, e) => { angle = (angle + 12) % 360; Invalidate(); };
    }

    public void Reset(string[] t) {
        titles = t;
        details = new string[t.Length];
        states = new StepState[t.Length];
        for (int i = 0; i < t.Length; i++) details[i] = "";
        spin.Start();
        Invalidate();
    }

    public void Set(int i, StepState state, string text) {
        if (i < 0 || i >= titles.Length) return;
        // Les étapes d'avant sont forcément finies
        for (int j = 0; j < i; j++) if (states[j] == StepState.Running || states[j] == StepState.Waiting) states[j] = StepState.Done;
        states[i] = state;
        details[i] = text ?? "";
        Invalidate();
    }

    public void FailRunning() {
        for (int i = 0; i < states.Length; i++) if (states[i] == StepState.Running) states[i] = StepState.Failed;
        spin.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float row = 64 * scale, dot = 26 * scale;
        for (int i = 0; i < titles.Length; i++) {
            float y = i * row;
            var circle = new RectangleF(1, y + 4 * scale, dot, dot);
            // Le trait qui relie les pastilles
            if (i < titles.Length - 1) {
                using (var p = new Pen(states[i] == StepState.Done ? Color.FromArgb(90, Palette.Green) : Palette.Border, 2 * scale))
                    g.DrawLine(p, circle.X + dot / 2, circle.Bottom + 6 * scale, circle.X + dot / 2, y + row + 2 * scale);
            }
            Color ink = Palette.Dim;
            switch (states[i]) {
                case StepState.Waiting:
                case StepState.Skipped:
                    using (var p = new Pen(Palette.Border, 2 * scale)) g.DrawEllipse(p, circle);
                    break;
                case StepState.Running:
                    ink = Palette.Text;
                    using (var p = new Pen(Color.FromArgb(70, Palette.Accent), 2.5f * scale)) g.DrawEllipse(p, circle);
                    using (var p = new Pen(Palette.Accent, 2.5f * scale)) {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawArc(p, circle, angle, 100);
                    }
                    break;
                case StepState.Done:
                    ink = Palette.Text;
                    using (var b = new SolidBrush(Palette.Green)) g.FillEllipse(b, circle);
                    using (var p = new Pen(Palette.Back, 2.4f * scale)) {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                        g.DrawLines(p, new[] {
                            new PointF(circle.X + dot * 0.28f, circle.Y + dot * 0.52f),
                            new PointF(circle.X + dot * 0.44f, circle.Y + dot * 0.68f),
                            new PointF(circle.X + dot * 0.73f, circle.Y + dot * 0.36f) });
                    }
                    break;
                case StepState.Failed:
                    ink = Palette.Text;
                    using (var b = new SolidBrush(Palette.Red)) g.FillEllipse(b, circle);
                    using (var p = new Pen(Palette.Back, 2.4f * scale)) {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        float a = dot * 0.33f, z = dot * 0.67f;
                        g.DrawLine(p, circle.X + a, circle.Y + a, circle.X + z, circle.Y + z);
                        g.DrawLine(p, circle.X + z, circle.Y + a, circle.X + a, circle.Y + z);
                    }
                    break;
            }
            int x = (int) (dot + 16 * scale);
            int h = TextRenderer.MeasureText(g, "Ag", name, Size.Empty, TextFormatFlags.NoPadding).Height;
            TextRenderer.DrawText(g, titles[i], name, new Point(x, (int) (y + 4 * scale)), ink, BackColor, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, details[i], detail, new Rectangle(x, (int) (y + 6 * scale) + h, Width - x, (int) (22 * scale)),
                    Palette.Dim, BackColor, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        if (Array.IndexOf(states, StepState.Running) < 0) spin.Stop();
    }
}

/** Le message du moment : à faire sur la TV (bleu), réussite (vert), souci (orange / rouge). */
sealed class Message : Control {
    public readonly RoundButton Done;
    readonly float scale;
    Color tint = Palette.Accent;

    public Message(float scale, Font font, Font buttonFont) {
        this.scale = scale;
        Font = font;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Back;
        Done = new RoundButton("C'est fait", Palette.Accent, Palette.AccentHover, Color.White, scale);
        Done.Font = buttonFont;
        Done.BackColor = Palette.Card;
        Done.Visible = false;
        Done.VisibleChanged += (s, e) => { Fit(); Invalidate(); };
        Controls.Add(Done);
    }

    public void Display(Tone tone, string text) {
        tint = Palette.For(tone);
        Done.BackColor = Palette.Mix(Palette.Card, tint, 28);
        Text = text.Replace("« ", "«\u00a0").Replace(" »", "\u00a0»");   // les guillemets ne restent pas seuls en bout de ligne
        Fit();
        Invalidate();
    }

    /** La hauteur juste pour le texte (et le bouton « C'est fait » s'il est là). */
    void Fit() {
        int left = (int) (28 * scale), right = (int) (18 * scale);
        Size t = TextRenderer.MeasureText(Text ?? "", Font, new Size(Width - left - right, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int h = t.Height + (int) (14 * scale) + (Done.Visible ? (int) (58 * scale) : (int) (14 * scale));
        Height = Math.Max((int) (56 * scale), h);
    }

    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        if (Done == null) return;   // pendant le constructeur
        Done.SetBounds(Width - (int) (128 * scale), Height - (int) (46 * scale), (int) (112 * scale), (int) (34 * scale));
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Palette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Palette.Mix(Palette.Card, tint, 28);
        using (var path = Palette.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12 * scale))
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        // Le liseré de couleur : une petite barre arrondie (lissée), à l'intérieur de la carte
        using (var bar = Palette.Round(new RectangleF(10 * scale, 14 * scale, 4 * scale, Height - 28 * scale), 2 * scale))
        using (var b = new SolidBrush(tint)) g.FillPath(b, bar);
        int left = (int) (28 * scale), right = (int) (18 * scale), top = (int) (14 * scale);
        int bottom = Done.Visible ? (int) (54 * scale) : (int) (12 * scale);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(left, top, Width - left - right, Height - top - bottom),
                Palette.Text, fill, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}
}
