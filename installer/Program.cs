// FrontEndTV — installateur pour Windows : la fenêtre. Le travail avec la TV est dans Engine.cs.
// Développé par Florian. Écrit en C# 5 : il se compile avec le csc.exe livré avec Windows (voir build.sh).
//
// La fenêtre : une ouverture animée (Splash), la liste des TV trouvées avec leur marque et leur modèle (DeviceList,
// Discovery.cs), le pays dans un menu (Picker), puis le déroulé de l'installation (Steps).
//
// Options (facultatives) : -Ip 192.168.1.50 -Name Florian -Region FR -Key <clé TMDB> -Apk <fichier>
//                          -Original (remet l'accueil d'origine) -Auto (lance tout de suite, sans rien demander)
//                          -SelfTest <image.png> (ouvre la fenêtre, enregistre ses pages en images — aussi l'ouverture
//                          et le menu des pays — et se ferme : la vérification de la compilation GitHub sur un vrai Windows)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Les informations du fichier (clic droit → Propriétés → Détails) ; BuildInfo est écrit par installer/build-exe.sh.
[assembly: AssemblyTitle("FrontEndTV - installateur")]
[assembly: AssemblyDescription("Installe FrontEndTV sur une Google TV / Android TV")]
[assembly: AssemblyProduct("FrontEndTV")]
[assembly: AssemblyCompany("Florian")]
[assembly: AssemblyCopyright("© Florian")]
[assembly: AssemblyVersion(FrontEndTV.Setup.BuildInfo.Version)]
[assembly: AssemblyFileVersion(FrontEndTV.Setup.BuildInfo.Version)]
[assembly: AssemblyInformationalVersion(FrontEndTV.Setup.BuildInfo.Version)]

namespace FrontEndTV.Setup {

static class Program {
    [STAThread]
    static int Main(string[] args) {
        // Jamais de fermeture sans un mot : toute erreur est montrée et notée dans un fichier
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
        try {
            try { SetProcessDPIAware(); } catch (Exception) {}   // net à 125 %, 150 %... (sinon Windows étire une image floue)
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Options o; bool auto; string selfTest;
            Parse(args, out o, out auto, out selfTest);
            quiet = selfTest != null;
            var form = new MainForm(o, auto, selfTest);
            Application.Run(form);
            return form.ExitCode;
        } catch (Exception e) {
            Crash(e);
            return 1;
        }
    }

    static bool crashed, quiet;   // quiet : -SelfTest, personne pour fermer une fenêtre d'erreur

    static void Crash(Exception e) {
        if (crashed) return;
        crashed = true;
        string file = Path.Combine(Engine.Home, "installateur-erreur.txt");
        try {
            Directory.CreateDirectory(Engine.Home);
            File.AppendAllText(file, "=== " + DateTime.Now.ToString("s") + " " + BuildInfo.Version + Environment.NewLine
                    + (e == null ? "?" : e.ToString()) + Environment.NewLine);
        } catch (Exception) {}
        if (!quiet) try {
            string what = (e == null ? "?" : e.Message) + "\n\nLes détails sont dans :\n" + file;
            try { Dialog.Tell(null, "L'installateur a rencontré une erreur", what, Tone.Error); }
            catch (Exception) {   // même la fenêtre du message ne s'ouvre pas : celle de Windows
                MessageBox.Show("L'installateur a rencontré une erreur :\n\n" + what, "FrontEndTV", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        } catch (Exception) {}
        Environment.Exit(1);
    }

    static void Parse(string[] args, out Options o, out bool auto, out string selfTest) {
        o = new Options();
        auto = false;
        selfTest = null;
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
                case "selftest": selfTest = next; i++; break;
            }
        }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

/** Les couleurs : celles des réglages de FrontEndTV sur la TV (bleu nuit, cartes plus claires, violet). */
static class Palette {
    public static readonly Color Back = Color.FromArgb(14, 19, 32);
    public static readonly Color Card = Color.FromArgb(21, 28, 43);
    public static readonly Color Edge = Color.FromArgb(34, 43, 61);
    public static readonly Color Field = Color.FromArgb(28, 36, 54);
    public static readonly Color Border = Color.FromArgb(46, 57, 79);
    public static readonly Color Text = Color.FromArgb(245, 247, 250);
    public static readonly Color Dim = Color.FromArgb(154, 163, 181);
    public static readonly Color Faint = Color.FromArgb(98, 108, 128);
    public static readonly Color Accent = Color.FromArgb(123, 97, 255);
    public static readonly Color AccentHover = Color.FromArgb(150, 128, 255);
    public static readonly Color AccentEnd = Color.FromArgb(160, 100, 250);   // l'autre bout du dégradé du bouton
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

    /** L'échelle de l'écran (1 à 100 %), celle de la fenêtre principale ; d'après l'écran avant elle. */
    public static float Scale = ScreenScale();

    static float ScreenScale() {
        try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); }
        catch (Exception) { return 1f; }
    }

    /**
     * Une fenêtre aux couleurs du thème : barre de titre sombre (Windows 10 et 11) et, sur Windows 11, de la couleur
     * du fond, avec un liseré et un titre assortis. Ailleurs, rien ne change.
     */
    public static void Theme(Form f) {
        try {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            int caption = ColorRef(Back), border = ColorRef(Border), title = ColorRef(Text);
            DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
            DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
            DwmSetWindowAttribute(f.Handle, 36, ref title, 4);
        } catch (Exception) {}   // pas Windows (essais), ou Windows trop ancien
    }

    static int ColorRef(Color c) { return c.R | c.G << 8 | c.B << 16; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /** Le menu du clic droit d'un champ (couper, copier, coller, tout sélectionner), dans le thème. */
    public static ContextMenuStrip TextMenu(TextBox box, Font font, float scale) {
        var menu = new ContextMenuStrip();
        menu.Renderer = new DarkRenderer(Border);
        menu.ShowImageMargin = false;
        menu.Font = font;
        menu.Padding = new Padding((int) (3 * scale));
        Func<string, string, EventHandler, ToolStripMenuItem> item = (text, keys, click) => {
            var i = new ToolStripMenuItem(text, null, click);
            i.ShortcutKeyDisplayString = keys;
            i.Padding = new Padding((int) (6 * scale), (int) (5 * scale), (int) (6 * scale), (int) (5 * scale));
            menu.Items.Add(i);
            return i;
        };
        ToolStripMenuItem cut = item("Couper", "Ctrl+X", (s, e) => box.Cut()), copy = item("Copier", "Ctrl+C", (s, e) => box.Copy()),
                paste = item("Coller", "Ctrl+V", (s, e) => box.Paste());
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem all = item("Tout sélectionner", "Ctrl+A", (s, e) => { box.Focus(); box.SelectAll(); });
        menu.Opening += (s, e) => {
            cut.Enabled = copy.Enabled = box.SelectionLength > 0;
            bool text = false;
            try { text = Clipboard.ContainsText(); } catch (Exception) {}
            paste.Enabled = text;
            all.Enabled = box.TextLength > 0;
        };
        return menu;
    }

    /** Une TV (écran arrondi et pied) au milieu de `r`, comme celle du logo. */
    public static void DrawTv(Graphics g, RectangleF r, Color ink, float scale) {
        float w = r.Width * 0.62f, h = r.Height * 0.40f, x = r.X + (r.Width - w) / 2, y = r.Y + r.Height * 0.24f;
        using (var pen = new Pen(ink, 2.2f * scale))
        using (var screen = Round(new RectangleF(x, y, w, h), 3.5f * scale)) g.DrawPath(pen, screen);
        using (var b = new SolidBrush(ink))
        using (var stand = Round(new RectangleF(r.X + r.Width * 0.38f, y + h + r.Height * 0.08f, r.Width * 0.24f, 2.4f * scale), 1.2f * scale))
            g.FillPath(b, stand);
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
    readonly Font body, small, bold, title, caps;
    Image logo;

    // L'ouverture : le logo apparaît, puis glisse à sa place pendant que la page arrive
    Splash splash;
    Header setupHeader;

    // Page 1 : la TV et les réglages
    Panel setup;
    DeviceList tvs;
    Field ip, name, key;
    Picker country;
    Note ipStatus;
    LinkLabel refresh;
    RoundButton go;

    // Page 2 : le déroulé
    Panel progress;
    Steps steps;
    Message message;
    RoundButton close, retry;
    Label pageTitle;
    Thread worker;
    readonly ManualResetEvent userDone = new ManualResetEvent(false);

    readonly string selfTest;   // -SelfTest : l'image où enregistrer la fenêtre
    public int ExitCode { get; private set; }

    public MainForm(Options o, bool auto, string selfTest) {
        options = o;
        this.auto = auto;
        this.selfTest = selfTest;
        // L'échelle de l'écran (125 %, 150 %...), réduite si la fenêtre ne tiendrait pas en hauteur
        float dpi;
        using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96f;
        Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        scale = Math.Max(0.75f, Math.Min(dpi, (screen.Height - 56 * dpi) / Height0));
        // Essais de mise en page (Mono ignore l'échelle de l'écran) : FRONTENDTV_SCALE=1.5 force 150 %
        float forced;
        if (float.TryParse(Environment.GetEnvironmentVariable("FRONTENDTV_SCALE") ?? "", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out forced) && forced >= 0.75f && forced <= 3) scale = forced;
        Palette.Scale = scale;   // les fenêtres de message à la même échelle
        // Polices en pixels, à la même échelle que tout le reste : rien ne déborde ni ne s'étire
        body = Px(14f, false);
        small = Px(12.5f, false);
        bold = Px(14.5f, true);
        title = Px(28f, true);
        caps = Px(11.5f, true);

        Text = "FrontEndTV — installation";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Back;
        ForeColor = Palette.Text;
        Font = body;
        ClientSize = new Size(S(Width0), S(Height0));
        // L'icône en toutes tailles (16 à 256 px) : nette dans la barre de titre et la barre des tâches
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
        // L'animation d'ouverture (pas en -Auto ni en -SelfTest) : la fenêtre arrive en fondu, par-dessus la page
        if (!auto && selfTest == null) {
            splash = NewSplash();
            Controls.Add(splash);
            splash.BringToFront();
            Opacity = 0;
        }
    }

    const int Width0 = 560, Height0 = 770, W = Width0 - 64;   // taille de la fenêtre à 100 %, largeur du contenu

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
        Palette.Theme(this);
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        if (selfTest != null) { SelfTest(); return; }
        if (options.Original && auto) { Start(true); return; }
        if (ip.Value.Length == 0) Search(); else if (auto) Start(options.Original);
        if (splash != null) {
            splash.Page = PageImage(setup);   // la page qui arrive (déjà en recherche), en image, pour le fondu enchaîné
            splash.Play();
        }
    }

    Splash NewSplash() {
        var sp = new Splash(logo, Px(34f, true), Px(14f, false), scale);
        sp.Bounds = new Rectangle(0, 0, S(Width0), S(Height0));
        sp.LogoTo = new RectangleF(setupHeader.Left, setupHeader.Top, setupHeader.Height, setupHeader.Height);
        sp.Fade = a => { try { Opacity = a; } catch (Exception) {} };
        sp.Finished += (s, e) => {
            Opacity = 1;
            Controls.Remove(splash);
            splash.Dispose();
            splash = null;
            if (go.Enabled) tvs.Focus();
        };
        return sp;
    }

    static Bitmap PageImage(Control page) {
        var b = new Bitmap(page.Width, page.Height);
        page.DrawToBitmap(b, new Rectangle(Point.Empty, page.Size));
        return b;
    }

    /** -SelfTest : l'ouverture, les deux pages enregistrées en images (avec des TV et des étapes d'exemple), puis on ferme. */
    void SelfTest() {
        var timer = new System.Windows.Forms.Timer();
        timer.Interval = 1500;
        timer.Tick += (s, e) => {
            timer.Stop();
            string dir = Path.GetDirectoryName(Path.GetFullPath(selfTest)), stem = Path.GetFileNameWithoutExtension(selfTest);
            tvs.SetItems(new List<Tv> {
                new Tv { Ip = "192.168.1.50", Name = "TV du salon", Brand = "TCL", Model = "Smart TV Pro", Adb = true, Remote = true, Last = true },
                new Tv { Ip = "192.168.1.62", Name = "Chambre", Brand = "Philips", Model = "55PUS8007", Remote = true },
                new Tv { Ip = "192.168.1.80", Brand = "Xiaomi", Model = "Mi Box S", Adb = true } });
            tvs.Select(0);
            ip.Value = tvs.Current.Ip;
            TellAbout(tvs.Current, false);
            Refresh();
            Snapshot(selfTest, false);
            using (Bitmap page = PageImage(setup)) {
                // Une image de l'ouverture, au moment où le logo commence à glisser vers sa place
                using (var sp = new Splash(logo, Px(34f, true), Px(14f, false), scale))
                using (var intro = new Bitmap(page.Width, page.Height))
                using (Graphics g = Graphics.FromImage(intro)) {
                    sp.Size = page.Size;
                    sp.Page = page;
                    sp.LogoTo = new RectangleF(setupHeader.Left, setupHeader.Top, setupHeader.Height, setupHeader.Height);
                    sp.Draw(g, 1150);
                    intro.Save(Path.Combine(dir, stem + "-intro.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            Dialog.Snapshot(this, Path.Combine(dir, stem + "-question.png"));
            country.OpenList();   // le menu des pays, ouvert
            Application.DoEvents();
            Snapshot(Path.Combine(dir, stem + "-pays.png"), true);
            country.CloseList();
            setup.Visible = false;
            progress.Visible = true;
            pageTitle.Text = "INSTALLATION DE FRONTENDTV — TV DU SALON";
            steps.Reset(new[] { "Connexion à la TV", "Installation de l'appli", "Activation comme écran d'accueil" });
            steps.Set(Engine.StepConnect, StepState.Done, "TCL Smart TV Pro · Android 11");
            steps.Set(Engine.StepInstall, StepState.Running, "Envoi de l'appli (1,5 Mo)... 45 %");
            Say(Tone.Action, "Regarde la TV : accepte « Autoriser le débogage USB ? » en cochant « Toujours autoriser ».");
            retry.Visible = false;   // comme pendant une vraie installation
            close.Text = "Annuler";
            close.Left = S(32);
            close.Width = S(W);
            Refresh();
            Snapshot(Path.Combine(dir, stem + "-2.png"), false);
            ExitCode = 0;
            Close();
        };
        timer.Start();
    }

    /**
     * La page entière (sans la barre de titre) ; `screen` : copiée de l'écran, avec le menu ouvert par-dessus (aussi
     * sous Mono, pour les essais hors de Windows : son DrawToBitmap rend une page vide).
     */
    void Snapshot(string file, bool screen) {
        Control page = setup.Visible ? setup : progress;
        Application.DoEvents();   // les dernières modifications dessinées
        using (var b = new Bitmap(page.Width, page.Height)) {
            bool copied = false;
            if (screen || Type.GetType("Mono.Runtime") != null) {
                try {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(page.PointToScreen(Point.Empty), Point.Empty, page.Size);
                    copied = true;
                } catch (Exception) {}   // pas d'écran à copier (session sans bureau) : la page seule
            }
            if (!copied) page.DrawToBitmap(b, new Rectangle(Point.Empty, page.Size));
            b.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    // MARK: - En-tête commun

    Header AddHeader(Control page, string subtitle) {
        var head = new Header(logo, title, small, "FrontEndTV", subtitle, scale);
        head.Bounds = new Rectangle(S(32), S(24), S(W), S(60));
        page.Controls.Add(head);
        return head;
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
        l.DisabledLinkColor = Palette.Dim;
        l.LinkBehavior = LinkBehavior.HoverUnderline;
        l.BackColor = parent.BackColor;
        l.AutoSize = true;
        l.Location = new Point(x, y);
        l.LinkClicked += click;
        parent.Controls.Add(l);
        Align(l, x, w, align);
        return l;
    }

    /** Aligné à droite ou centré dans la largeur w, d'après sa taille réelle. */
    static void Align(LinkLabel l, int x, int w, ContentAlignment align) {
        if (align == ContentAlignment.MiddleRight) l.Left = x + w - l.PreferredWidth;
        else if (align == ContentAlignment.MiddleCenter) l.Left = x + (w - l.PreferredWidth) / 2;
    }

    // MARK: - Page 1 : la TV et les réglages

    void BuildSetup() {
        setup = Page();
        setupHeader = AddHeader(setup, "Écran d'accueil pour Google TV / Android TV");
        int inner = W - 32;   // la largeur dans une carte

        // Carte 1 : la TV (la liste, l'adresse, ce qui se passe)
        var tvCard = new Card(Palette.Card, scale);
        tvCard.Bounds = new Rectangle(S(32), S(100), S(W), S(342));
        setup.Controls.Add(tvCard);
        AddLabel(tvCard, "TA TV", caps, Palette.Dim, S(16), S(16), S(200), S(18));
        refresh = Link(tvCard, "Actualiser", S(16), S(15), S(inner), ContentAlignment.MiddleRight, (s, e) => { Touch(); Search(); });
        tvs = new DeviceList(scale, bold, small);
        tvs.Bounds = new Rectangle(S(16), S(42), S(inner), DeviceList.HeightFor(3, scale));
        tvs.Picked += (s, e) => {
            Touch();
            Tv t = tvs.Current;
            if (t == null) return;
            ip.Value = t.Ip;
            TellAbout(t, false);
        };
        tvs.Chosen += (s, e) => { if (tvs.Current != null && tvs.Current.Adb) Start(false); };
        tvCard.Controls.Add(tvs);
        int y = S(246);
        AddLabel(tvCard, "ADRESSE IP", caps, Palette.Dim, S(16), y, S(110), S(40)).TextAlign = ContentAlignment.MiddleLeft;
        ip = new Field("ex. 192.168.1.50", scale, body);
        ip.Bounds = new Rectangle(S(126), y, S(inner - 110), S(40));
        ip.Value = options.Ip;
        typedIp = options.Ip.Length > 0;
        ip.Box.TextChanged += (s, e) => {
            if (!ip.Box.Focused) return;   // seulement ce qu'on tape (un choix dans la liste écrit aussi ici)
            typedIp = true;
            string a = Engine.Normalize(ip.Value);
            tvs.SelectIp(a != null && a.EndsWith(":5555") ? a.Substring(0, a.Length - 5) : "");
        };
        tvCard.Controls.Add(ip);
        ipStatus = new Note(scale, small);
        ipStatus.Bounds = new Rectangle(S(16), y + S(50), S(inner), S(36));
        tvCard.Controls.Add(ipStatus);

        // Carte 2 : les réglages (prénom, pays, clé TMDB facultative)
        var more = new Card(Palette.Card, scale);
        more.Bounds = new Rectangle(S(32), tvCard.Bottom + S(14), S(W), S(176));
        setup.Controls.Add(more);
        int half = (inner - 12) / 2;
        AddLabel(more, "TON PRÉNOM", caps, Palette.Dim, S(16), S(16), S(half), S(18));
        AddLabel(more, "PAYS", caps, Palette.Dim, S(16 + half + 12), S(16), S(half), S(18));
        name = new Field("facultatif, pour « Bonsoir, … »", scale, body);
        name.Bounds = new Rectangle(S(16), S(40), S(half), S(40));
        name.Value = options.Name.Length > 0 ? options.Name : Engine.Recall("name");
        more.Controls.Add(name);
        country = new Picker(scale, body, small);
        country.Bounds = new Rectangle(S(16 + half + 12), S(40), S(half), S(40));
        foreach (string[] c in Countries.All) country.Items.Add(c);
        string r = options.Region.Length > 0 ? options.Region.ToUpperInvariant() : Engine.Recall("region");
        country.Value = r.Length == 2 ? r : Engine.DefaultRegion();
        country.Changed += (s, e) => Touch();
        country.MouseDown += (s, e) => Touch();
        more.Controls.Add(country);
        AddLabel(more, "CLÉ TMDB  ·  FACULTATIF", caps, Palette.Dim, S(16), S(96), S(260), S(18));
        Link(more, "Obtenir une clé gratuite", S(16), S(95), S(inner), ContentAlignment.MiddleRight,
                (s, e) => { Touch(); Open("https://www.themoviedb.org/settings/api"); });
        key = new Field("pas besoin : les recommandations sont intégrées", scale, body);
        key.Bounds = new Rectangle(S(16), S(120), S(inner), S(40));
        key.Value = options.Key;
        more.Controls.Add(key);

        go = new RoundButton(GoText, Palette.Accent, Palette.AccentHover, Color.White, scale);
        go.Gradient = true;
        go.Font = Px(16f, true);
        go.Bounds = new Rectangle(S(32), more.Bottom + S(16), S(W), S(52));
        go.Click += (s, e) => Start(false);
        setup.Controls.Add(go);
        Link(setup, "Remettre l'écran d'accueil d'origine", S(32), go.Bottom + S(12), S(W), ContentAlignment.MiddleCenter, (s, e) => {
            Touch();
            if (Dialog.Ask(this, "Remettre l'écran d'accueil d'origine ?", "Les applis mises de côté reviennent comme avant. "
                    + "FrontEndTV reste installé : tu pourras y revenir.", "Remettre", "Annuler")) Start(true);
        });
        AddLabel(setup, "par Florian", small, Palette.Faint,
                S(32), go.Bottom + S(38), S(W), S(20)).TextAlign = ContentAlignment.MiddleCenter;
        foreach (Field f in new[] { ip, name, key }) {
            f.Box.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Start(false); } else Touch();
            };
            f.Box.MouseDown += (s, e) => Touch();
            f.MouseDown += (s, e) => Touch();
        }
        rescan.Interval = 5000;
        rescan.Tick += (s, e) => {
            rescan.Stop();
            // Toutes les 5 s tant qu'aucune TV prête n'est choisie ; tout le réseau une fois sur quatre (~20 s)
            Tv t = tvs.Current;
            if (setup.Visible && (t == null || !t.Adb) && !typedIp) Search(++rescans % 4 == 0);
        };
        countdown.Interval = 1000;
        countdown.Tick += (s, e) => {
            if (--remaining <= 0) { StopCountdown(); Start(false); } else go.Text = GoText + "  ·  " + remaining + " s";
        };
    }

    int rescans;
    bool searching, searchedOnce, typedIp;

    /** Cherche les TV (voir Discovery.Find) ; la liste se remplit au fur et à mesure. */
    void Search(bool wholeNetwork = true) {
        rescan.Stop();
        if (searching) return;
        searching = true;
        refresh.Enabled = false;   // grisé pendant la recherche
        tvs.Searching = !searchedOnce;
        if (!searchedOnce) Status("Recherche des TV sur ton réseau...", Palette.Dim);
        var t = new Thread(() => {
            List<Tv> found = Discovery.Find(wholeNetwork, list => Post(() => ShowTvs(list, false)));
            Post(() => {
                searching = false;
                searchedOnce = true;
                tvs.Searching = false;
                refresh.Enabled = true;
                ShowTvs(found, true);
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    /**
     * Les TV trouvées. Sans choix fait à la main, la bonne est choisie : celle de la dernière fois, sinon la seule
     * prête. À la fin d'une recherche, elle démarre seule après un compte à rebours, si personne n'a touché à rien.
     */
    void ShowTvs(List<Tv> found, bool final) {
        tvs.SetItems(found);
        if (!typedIp && !touched && tvs.Current == null) {
            int pick = found.FindIndex(t => t.Last && t.Adb);
            if (pick < 0 && found.Count(t => t.Adb) == 1) pick = found.FindIndex(t => t.Adb);
            if (pick < 0 && found.Count == 1) pick = 0;
            if (pick >= 0) {
                tvs.Select(pick);
                ip.Value = found[pick].Ip;
            }
        }
        if (!final || typedIp) return;
        Tv chosen = tvs.Current;
        if (found.Count == 0) {
            Status("Aucune TV trouvée pour l'instant — je continue de chercher...", Palette.Amber);
            rescan.Start();
        } else if (chosen == null) {
            Status(found.Count + " TV trouvées : choisis la tienne dans la liste.", Palette.Green);
            if (!found.Any(t => t.Adb)) rescan.Start();
        } else {
            TellAbout(chosen, true);
        }
    }

    /** Ce qu'on peut dire de la TV choisie ; `final` : après une recherche (elle peut démarrer seule). */
    void TellAbout(Tv t, bool final) {
        if (!t.Adb) {
            Status("Active « Débogage USB » sur cette TV (Paramètres → Système → Options pour les développeurs). "
                    + "Je réessaie tout seul.", Palette.Amber);
            rescan.Start();
            return;
        }
        if (final && auto) { Start(options.Original); return; }
        bool alone = tvs.Items.Count(x => x.Adb) == 1;
        Status((t.Last ? "Ta TV de la dernière fois est prête" : t.Title() + " est prête")
                + (final && !touched && (t.Last || alone) ? " — démarrage automatique" : ""), Palette.Green);
        if (final && (t.Last || alone)) StartCountdown(t.Last ? 4 : 8);
    }

    // MARK: - Démarrage automatique (s'arrête dès qu'on touche à un réglage)

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
        ipStatus.Show(text, color);
    }

    // MARK: - Page 2 : déroulé

    void BuildProgress() {
        progress = Page();
        AddHeader(progress, "Installation sur ta TV");
        pageTitle = AddLabel(progress, "", caps, Palette.Dim, S(32), S(112), S(W), S(18));

        var stepsCard = new Card(Palette.Card, scale);
        stepsCard.Bounds = new Rectangle(S(32), S(138), S(W), S(2 * 64 + 48 + 40));
        progress.Controls.Add(stepsCard);
        steps = new Steps(scale, bold, small);
        steps.BackColor = Palette.Card;
        steps.Bounds = new Rectangle(S(20), S(20), S(W - 40), S(2 * 64 + 48));
        stepsCard.Controls.Add(steps);

        message = new Message(scale, body, Px(13.5f, true));
        message.Bounds = new Rectangle(S(32), stepsCard.Bottom + S(16), S(W), S(140));
        message.Done.Click += (s, e) => { message.Done.Visible = false; userDone.Set(); };
        message.Visible = false;
        progress.Controls.Add(message);

        retry = new RoundButton("Réessayer", Palette.Accent, Palette.AccentHover, Color.White, scale);
        retry.Gradient = true;
        retry.Font = bold;
        retry.Bounds = new Rectangle(S(32), S(Height0 - 132), S((W - 16) / 2), S(52));
        retry.Click += (s, e) => { progress.Visible = false; setup.Visible = true; go.Focus(); };
        progress.Controls.Add(retry);
        close = new RoundButton("Fermer", Palette.Field, Palette.Border, Palette.Text, scale);
        close.Font = bold;
        close.Bounds = new Rectangle(S(32 + (W + 16) / 2), S(Height0 - 132), S((W - 16) / 2), S(52));
        close.Click += (s, e) => Close();
        progress.Controls.Add(close);
        Link(progress, "Ouvrir le journal", S(32), S(Height0 - 62), S(W), ContentAlignment.MiddleCenter, (s, e) => Open(Engine.LogFile));
    }

    void Start(bool original) {
        StopCountdown();
        rescan.Stop();
        if (worker != null && worker.IsAlive) return;
        if (Engine.Normalize(ip.Value) == null) {
            Status(ip.Value.Length == 0 ? "Choisis ta TV dans la liste, ou entre son adresse IP (Paramètres → Réseau et Internet)."
                    : "Ça ne ressemble pas à une adresse IP (ex. 192.168.1.50).", Palette.Amber);
            ip.Box.Focus();
            return;
        }
        var o = new Options();
        o.Ip = ip.Value;
        o.Name = name.Value;
        o.Region = country.Value;
        o.Key = key.Value;
        o.Apk = options.Apk;
        o.Original = original;

        setup.Visible = false;
        progress.Visible = true;
        Tv t = tvs.Current;
        string which = t != null && t.Ip == ip.Value ? " — " + t.Title() : "";
        pageTitle.Text = ((original ? "Retour à l'écran d'accueil d'origine" : "Installation de FrontEndTV") + which).ToUpperInvariant();
        steps.Reset(new[] { "Connexion à la TV", original ? "Application" : "Installation de l'appli",
                original ? "Accueil d'origine" : "Activation comme écran d'accueil" });
        Say(Tone.Info, null);
        retry.Visible = false;
        close.Text = "Annuler";
        close.Fill = Palette.Field;
        close.Hover = Palette.Border;
        close.Gradient = false;
        close.Left = S(32);
        close.Width = S(W);

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
            retry.Visible = false;
            close.Text = "Terminer";
            close.Left = S(32);
            close.Width = S(W);
            close.Fill = Palette.Accent;
            close.Hover = Palette.AccentHover;
            close.Gradient = true;
            close.Invalidate();
            close.Focus();
        } else {
            Say(Tone.Error, error);
            close.Text = "Fermer";
            close.Left = S(32 + (W + 16) / 2);
            close.Width = S((W - 16) / 2);
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
                && !Dialog.Ask(this, "L'installation est en cours. Arrêter ?", "Ce qui est déjà fait sur la TV y reste ; "
                        + "tu pourras relancer l'installateur ensuite.", "Arrêter", "Continuer")) {
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

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
        // « FrontEnd » en blanc et « TV » en violet, comme sur la TV
        if (line1.EndsWith("TV")) {
            string head = line1.Substring(0, line1.Length - 2);
            int w = TextRenderer.MeasureText(g, head, big, Size.Empty, TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(g, head, big, new Point(x, top), Palette.Text, Palette.Back, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "TV", big, new Point(x + w, top), Palette.AccentHover, Palette.Back, TextFormatFlags.NoPadding);
        } else {
            TextRenderer.DrawText(g, line1, big, new Point(x, top), Palette.Text, Palette.Back, TextFormatFlags.NoPadding);
        }
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
        using (var path = Palette.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 14 * scale))
        using (var b = new SolidBrush(fill))
        using (var pen = new Pen(Palette.Edge, 1f)) {
            e.Graphics.FillPath(b, path);
            e.Graphics.DrawPath(pen, path);
        }
    }
}

/** Ce qui se passe, sous l'adresse : une pastille de couleur, puis le texte (deux lignes au plus). */
sealed class Note : Control {
    readonly float scale;
    Color tint = Palette.Dim;

    public Note(float scale, Font font) {
        this.scale = scale;
        Font = font;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Show(string text, Color color) {
        Text = text;
        tint = color;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        Color back = Parent != null ? Parent.BackColor : Palette.Back;
        g.Clear(back);
        if (string.IsNullOrEmpty(Text)) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int lineH = TextRenderer.MeasureText(g, "Ag", Font, Size.Empty, TextFormatFlags.NoPadding).Height;
        float dot = 7 * scale;
        using (var b = new SolidBrush(tint)) g.FillEllipse(b, 1, (lineH - dot) / 2f + 1, dot, dot);
        int x = (int) (dot + 9 * scale);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), tint, back,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
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
        Box.ContextMenuStrip = Palette.TextMenu(Box, font, scale);   // pas le menu blanc de Windows
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
    public bool Gradient;   // le bouton principal : un dégradé violet
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
        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        using (var path = Palette.Round(r, 12 * scale)) {
            if (Gradient && Enabled) {
                Color end = over ? Palette.Mix(Palette.AccentEnd, Color.White, 30) : Palette.AccentEnd;
                using (var b = new LinearGradientBrush(r, c, down ? ControlPaint.Dark(end, 0.05f) : end, 0f)) g.FillPath(b, path);
                // Un liseré clair en haut, comme une surface en verre
                using (var light = new LinearGradientBrush(r, Color.FromArgb(70, Color.White), Color.FromArgb(0, Color.White), 90f))
                using (var pen = new Pen(light, 1f)) g.DrawPath(pen, path);
            } else {
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
            }
            if (Focused && ShowFocusCues) using (var pen = new Pen(Color.FromArgb(150, Color.White), 1.5f * scale)) g.DrawPath(pen, path);
        }
        if (Gradient && Enabled) {   // sur un dégradé : le texte lissé (ClearType a besoin d'un fond uni)
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var ink = new SolidBrush(Ink))
            using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Text, Font, ink, new RectangleF(0, 0, Width, Height), f);
            return;
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

/**
 * L'ouverture (1,7 s) : la fenêtre arrive en fondu, le logo grandit dans une lueur violette, « FrontEndTV » monte en
 * fondu ; puis la page arrive en fondu enchaîné pendant que le logo glisse jusqu'à sa place dans l'en-tête.
 */
sealed class Splash : Control {
    public const int Total = 1700;
    public Bitmap Page;            // la page qui arrive, en image
    public RectangleF LogoTo;      // la place du logo dans l'en-tête de la page
    public Action<double> Fade;    // l'opacité de la fenêtre (0 à 1)
    public event EventHandler Finished;
    readonly Image logo;
    readonly Font big, small;
    readonly float scale;
    readonly Stopwatch clock = new Stopwatch();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    bool pageReady;

    public Splash(Image logo, Font big, Font small, float scale) {
        this.logo = logo; this.big = big; this.small = small; this.scale = scale;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Palette.Back;
        timer.Interval = 15;
        timer.Tick += (s, e) => {
            long ms = clock.ElapsedMilliseconds;
            if (Fade != null) Fade(Math.Min(1.0, ms / 220.0));
            Invalidate();
            if (ms >= Total) {
                timer.Stop();
                if (Finished != null) Finished(this, EventArgs.Empty);
            }
        };
    }

    public void Play() {
        clock.Start();
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e) {
        Draw(e.Graphics, clock.ElapsedMilliseconds);
    }

    static float Clamp(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    static float EaseOut(float t) { return 1 - (1 - t) * (1 - t) * (1 - t); }
    static float EaseInOut(float t) { return t < 0.5f ? 4 * t * t * t : 1 - (float) Math.Pow(-2 * t + 2, 3) / 2; }
    static float Back(float t) { const float c = 1.6f; return 1 + (c + 1) * (float) Math.Pow(t - 1, 3) + c * (float) Math.Pow(t - 1, 2); }

    /** L'image à `ms` millisecondes du début. */
    public void Draw(Graphics g, float ms) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Palette.Back);
        float appear = Clamp(ms / 700f), words = EaseOut(Clamp((ms - 250) / 500f)), line = EaseOut(Clamp((ms - 420) / 500f));
        float leave = EaseInOut(Clamp((ms - 1050) / 600f));   // le passage à la page
        float cx = Width / 2f, cy = Height * 0.42f;

        // La lueur derrière le logo : elle s'ouvre, puis s'éteint au passage
        float glow = EaseOut(appear) * (1 - leave);
        if (glow > 0.01f) {
            float r = (150 + 40 * EaseOut(appear)) * scale;
            using (var path = new GraphicsPath()) {
                path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                using (var brush = new PathGradientBrush(path)) {
                    brush.CenterColor = Color.FromArgb((int) (120 * glow), Palette.Accent);
                    brush.SurroundColors = new[] { Color.FromArgb(0, Palette.Back) };
                    g.FillPath(brush, path);
                }
            }
        }

        // La page qui arrive, en fondu enchaîné ; sans son logo, que remplace celui qui glisse (sinon il serait en double)
        if (Page != null && leave > 0) {
            if (!pageReady && LogoTo.Width > 0) {
                using (Graphics p = Graphics.FromImage(Page))
                using (var b = new SolidBrush(Palette.Back)) p.FillRectangle(b, RectangleF.Inflate(LogoTo, 2, 2));
                pageReady = true;
            }
            DrawFaded(g, Page, new RectangleF(0, 0, Width, Height), leave);
        }

        // « FrontEndTV » (TV en violet) et la ligne du dessous, qui montent en fondu puis s'effacent
        float textAlpha = words * (1 - Clamp(leave * 2.2f));
        if (textAlpha > 0.01f) {
            SizeF a = g.MeasureString("FrontEnd", big, PointF.Empty, StringFormat.GenericTypographic);
            SizeF b = g.MeasureString("TV", big, PointF.Empty, StringFormat.GenericTypographic);
            float x = cx - (a.Width + b.Width) / 2, y = cy + 92 * scale + 14 * scale * (1 - words);
            using (var white = new SolidBrush(Color.FromArgb((int) (255 * textAlpha), Palette.Text)))
            using (var violet = new SolidBrush(Color.FromArgb((int) (255 * textAlpha), Palette.AccentHover))) {
                g.DrawString("FrontEnd", big, white, x, y, StringFormat.GenericTypographic);
                g.DrawString("TV", big, violet, x + a.Width, y, StringFormat.GenericTypographic);
            }
            float sub = line * (1 - Clamp(leave * 2.2f));
            string text = "Installation sur ta TV";
            SizeF c = g.MeasureString(text, small, PointF.Empty, StringFormat.GenericTypographic);
            using (var dim = new SolidBrush(Color.FromArgb((int) (255 * sub), Palette.Dim)))
                g.DrawString(text, small, dim, cx - c.Width / 2, y + a.Height + 8 * scale + 8 * scale * (1 - line), StringFormat.GenericTypographic);
            // Une petite barre qui charge
            float bw = 120 * scale, by = y + a.Height + c.Height + 30 * scale;
            using (var track = new SolidBrush(Color.FromArgb((int) (60 * sub), Palette.Border)))
                g.FillRectangle(track, cx - bw / 2, by, bw, 3 * scale);
            float fill = EaseInOut(Clamp((ms - 450) / 650f));
            using (var bar = new SolidBrush(Color.FromArgb((int) (255 * sub), Palette.Accent)))
                g.FillRectangle(bar, cx - bw / 2, by, bw * fill, 3 * scale);
        }

        // Le logo : il grandit (léger rebond), puis glisse et rapetisse jusqu'à sa place dans l'en-tête
        if (logo != null && appear > 0) {
            float size = 132 * scale * (0.55f + 0.45f * Back(appear));
            var from = new RectangleF(cx - size / 2, cy - size / 2 - 10 * scale, size, size);
            RectangleF to = LogoTo.Width > 0 ? LogoTo : from;
            var r = new RectangleF(from.X + (to.X - from.X) * leave, from.Y + (to.Y - from.Y) * leave,
                    from.Width + (to.Width - from.Width) * leave, from.Height + (to.Height - from.Height) * leave);
            DrawFaded(g, logo, r, EaseOut(Clamp(ms / 380f)));
        }
    }

    static void DrawFaded(Graphics g, Image image, RectangleF r, float alpha) {
        if (alpha >= 0.999f) { g.DrawImage(image, r); return; }
        using (var attributes = new ImageAttributes()) {
            var m = new ColorMatrix();
            m.Matrix33 = alpha;
            attributes.SetColorMatrix(m);
            g.DrawImage(image, new[] { r.Location, new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) },
                    new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
        }
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            timer.Dispose();
            if (Page != null) Page.Dispose();
        }
        base.Dispose(disposing);
    }
}

/**
 * Les TV trouvées, à choisir : une ligne chacune (son nom, sa marque et son modèle, son adresse, et si elle est prête),
 * la choisie encadrée de violet. Flèches ou clic pour choisir, Entrée ou double-clic pour installer. Vide : la
 * recherche, ou comment préparer la TV.
 */
sealed class DeviceList : Control {
    public List<Tv> Items = new List<Tv>();
    public bool Searching { get { return searching; } set { searching = value; if (value) spin.Start(); Invalidate(); } }
    public event EventHandler Picked, Chosen;
    readonly float scale;
    readonly Font name, detail;
    int selected = -1, hover = -1, top;
    bool searching;
    float angle;
    readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer();
    const int RowH = 58, Gap = 8, Shown = 3;

    public static int HeightFor(int rows, float scale) { return (int) Math.Round((rows * RowH + (rows - 1) * Gap) * scale); }

    public DeviceList(float scale, Font name, Font detail) {
        this.scale = scale; this.name = name; this.detail = detail;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Card;   // dans la carte « Ta TV »
        TabStop = true;
        spin.Interval = 33;
        spin.Tick += (s, e) => {
            angle = (angle + 12) % 360;
            if (!searching) spin.Stop();
            Invalidate();
        };
    }

    public Tv Current { get { return selected >= 0 && selected < Items.Count ? Items[selected] : null; } }

    /** Une nouvelle liste ; la TV choisie le reste (même adresse). */
    public void SetItems(List<Tv> list) {
        string keep = Current != null ? Current.Ip : null;
        Items = list ?? new List<Tv>();
        selected = keep == null ? -1 : Items.FindIndex(t => t.Ip == keep);
        hover = -1;
        top = Math.Max(0, Math.Min(top, Items.Count - Shown));
        Invalidate();
    }

    public void Select(int i) {
        selected = i >= 0 && i < Items.Count ? i : -1;
        if (selected >= 0) {
            if (selected < top) top = selected;
            if (selected >= top + Shown) top = selected - Shown + 1;
        }
        Invalidate();
    }

    /** L'adresse tapée à la main : sa ligne choisie, ou aucune. */
    public void SelectIp(string ip) {
        Select(Items.FindIndex(t => t.Ip == ip));
    }

    float Row { get { return (RowH + Gap) * scale; } }

    int At(Point p) {
        int i = top + (int) (p.Y / Row);
        return p.Y % Row <= RowH * scale && i < Items.Count ? i : -1;
    }

    void Pick(int i) {
        if (i < 0 || i >= Items.Count) return;
        Select(i);
        if (Picked != null) Picked(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e) { int h = At(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Pick(At(e.Location)); base.OnMouseDown(e); }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { if (At(e.Location) >= 0 && Chosen != null) Chosen(this, EventArgs.Empty); base.OnMouseDoubleClick(e); }
    protected override void OnMouseWheel(MouseEventArgs e) {
        top = Math.Max(0, Math.Min(Items.Count - Shown, top - Math.Sign(e.Delta)));
        Invalidate();
        base.OnMouseWheel(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Enter || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Down) Pick(Math.Min(Items.Count - 1, selected + 1));
        else if (e.KeyCode == Keys.Up) Pick(Math.Max(0, selected - 1));
        else if (e.KeyCode == Keys.Enter && Current != null && Chosen != null) Chosen(this, EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Items.Count == 0) { DrawEmpty(g); return; }
        for (int v = 0; v < Shown && top + v < Items.Count; v++) {
            int i = top + v;
            Tv t = Items[i];
            float gutter = Items.Count > Shown ? 10 * scale : 0;   // la place de la barre de défilement
            var r = new RectangleF(0.5f, v * Row + 0.5f, Width - 1.5f - gutter, RowH * scale - 1);
            bool on = i == selected;
            Color back = on ? Palette.Mix(Palette.Field, Palette.Accent, 52) : i == hover ? Palette.Mix(Palette.Field, Palette.Accent, 18) : Palette.Field;
            using (var path = Palette.Round(r, 12 * scale)) {
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
                if (on) using (var pen = new Pen(Focused ? Palette.AccentHover : Palette.Accent, 1.6f * scale)) g.DrawPath(pen, path);
            }
            // La vignette : une TV blanche sur un carré violet
            float tile = 40 * scale;
            var square = new RectangleF(r.X + 10 * scale, r.Y + (r.Height - tile) / 2, tile, tile);
            using (var path = Palette.Round(square, 10 * scale))
            using (var b = new LinearGradientBrush(square, Color.FromArgb(124, 77, 255), Color.FromArgb(76, 43, 160), 45f))
                g.FillPath(b, path);
            Palette.DrawTv(g, square, Color.White, scale);
            // Son nom, puis marque · modèle · adresse
            int x = (int) (square.Right + 14 * scale);
            string state = t.Adb ? "Prête" : "Débogage à activer";
            Color stateColor = t.Adb ? Palette.Green : Palette.Amber;
            Size chip = TextRenderer.MeasureText(g, state, detail, Size.Empty, TextFormatFlags.NoPadding);
            int textRight = (int) (r.Right - chip.Width - 40 * scale);
            const TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int hName = TextRenderer.MeasureText(g, "Ag", name, Size.Empty, TextFormatFlags.NoPadding).Height;
            int hDetail = TextRenderer.MeasureText(g, "Ag", detail, Size.Empty, TextFormatFlags.NoPadding).Height;
            int y = (int) (r.Y + (r.Height - hName - hDetail - 3 * scale) / 2);
            TextRenderer.DrawText(g, t.Title(), name, new Rectangle(x, y, textRight - x, hName), Palette.Text, back, one);
            // Dessous : la marque et le modèle (raccourcis s'il le faut), puis l'adresse, toujours entière
            string sub = ((t.Last ? "La dernière fois  ·  " : "") + t.Kind()).Trim(), ip = (sub.Length > 0 ? "  ·  " : "") + t.Ip;
            int y2 = y + hName + (int) (3 * scale), ipW = TextRenderer.MeasureText(g, ip, detail, Size.Empty, TextFormatFlags.NoPadding).Width;
            int room = Math.Max(0, textRight - x - ipW), subW = Math.Min(room, TextRenderer.MeasureText(g, sub, detail, Size.Empty, TextFormatFlags.NoPadding).Width);
            if (sub.Length > 0) TextRenderer.DrawText(g, sub, detail, new Rectangle(x, y2, room, hDetail), Palette.Dim, back, one);
            TextRenderer.DrawText(g, ip, detail, new Point(x + subW, y2), Palette.Dim, back, TextFormatFlags.NoPadding);
            // À droite : prête (vert) ou débogage à activer (orange)
            float dot = 8 * scale, cx = r.Right - 14 * scale - chip.Width - dot - 6 * scale, cy = r.Y + r.Height / 2;
            using (var b = new SolidBrush(stateColor)) g.FillEllipse(b, cx, cy - dot / 2, dot, dot);
            TextRenderer.DrawText(g, state, detail, new Point((int) (cx + dot + 6 * scale), (int) (cy - chip.Height / 2f)), stateColor, back, TextFormatFlags.NoPadding);
        }
        // Plus de trois : où on en est
        if (Items.Count > Shown) {
            float h = Height * Shown / (float) Items.Count, y = (Height - h) * top / (float) (Items.Count - Shown);
            using (var b = new SolidBrush(Palette.Border)) g.FillRectangle(b, Width - 3 * scale, y, 3 * scale, h);
        }
    }

    void DrawEmpty(Graphics g) {
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Palette.Round(r, 12 * scale)) {
            using (var b = new SolidBrush(Palette.Field)) g.FillPath(b, path);
            using (var pen = new Pen(Palette.Border, 1f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
        }
        const TextFormatFlags center = TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter;
        int hName = TextRenderer.MeasureText(g, "Ag", name, Size.Empty, TextFormatFlags.NoPadding).Height;
        if (searching) {
            float d = 30 * scale;
            var circle = new RectangleF(Width / 2f - d / 2, Height / 2f - d - 8 * scale, d, d);
            using (var p = new Pen(Color.FromArgb(70, Palette.Accent), 3 * scale)) g.DrawEllipse(p, circle);
            using (var p = new Pen(Palette.Accent, 3 * scale)) {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, circle, angle, 100);
            }
            TextRenderer.DrawText(g, "Recherche des TV sur ton réseau...", name, new Rectangle(0, (int) (circle.Bottom + 14 * scale), Width, hName),
                    Palette.Text, Palette.Field, center);
            TextRenderer.DrawText(g, "Les TV Android et Google TV allumées s'affichent ici.", detail,
                    new Rectangle(0, (int) (circle.Bottom + 18 * scale) + hName, Width, (int) (20 * scale)), Palette.Dim, Palette.Field, center);
            return;
        }
        int x = (int) (20 * scale), y = (int) (18 * scale);
        TextRenderer.DrawText(g, "Aucune TV trouvée pour l'instant", name, new Point(x, y), Palette.Text, Palette.Field, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Sur la TV, une seule fois :\n"
                + "1.  Paramètres → Système → À propos : appuie 7 fois sur « Build ».\n"
                + "2.  Options pour les développeurs : active « Débogage USB ».\n"
                + "3.  La TV et ce PC sont sur le même réseau (Wi-Fi ou câble).",
                detail, new Rectangle(x, y + hName + (int) (10 * scale), Width - 2 * x, Height - y - hName), Palette.Dim, Palette.Field,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) spin.Dispose();
        base.Dispose(disposing);
    }
}

/**
 * Un menu déroulant sombre (le pays) : fermé, comme un champ, avec son code et une flèche ; ouvert, une liste sous lui.
 * Clavier : flèches pour changer, une lettre pour sauter au pays qui commence par elle, Entrée ou Espace pour ouvrir.
 */
sealed class Picker : Control {
    public readonly List<string[]> Items = new List<string[]>();   // { code, nom }
    public event EventHandler Changed;
    readonly float scale;
    readonly Font small;
    int selected = -1;
    ToolStripDropDown drop;

    public Picker(float scale, Font font, Font small) {
        this.scale = scale;
        this.small = small;
        Font = font;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Back;
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    /** Le code du pays choisi ; un code absent de la liste y est ajouté (sous son code). */
    public string Value {
        get { return selected >= 0 ? Items[selected][0] : ""; }
        set {
            int i = Items.FindIndex(x => x[0] == value);
            if (i < 0 && !string.IsNullOrEmpty(value)) { Items.Add(new[] { value, value }); i = Items.Count - 1; }
            selected = i;
            Invalidate();
        }
    }

    bool IsOpen { get { return drop != null && drop.Visible; } }

    void Choose(int i) {
        if (i < 0 || i >= Items.Count) return;
        bool changed = i != selected;
        selected = i;
        Invalidate();
        if (changed && Changed != null) Changed(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Up) Choose(selected - 1);
        else if (e.KeyCode == Keys.Down && !e.Alt) Choose(selected + 1);
        else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.F4 || (e.KeyCode == Keys.Down && e.Alt)) OpenList();
        base.OnKeyDown(e);
    }
    protected override void OnKeyPress(KeyPressEventArgs e) {
        int i = Jump(e.KeyChar, selected);
        if (i >= 0) Choose(i);
        base.OnKeyPress(e);
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        Focus();
        if (!IsOpen) OpenList();
        base.OnMouseDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    /** Le pays suivant (après `from`) dont le nom commence par la lettre `c`, accents ignorés ; -1 sinon. */
    int Jump(char c, int from) {
        if (!char.IsLetter(c)) return -1;
        string letter = Plain(c.ToString());
        for (int k = 1; k <= Items.Count; k++) {
            int i = (from + k + Items.Count) % Items.Count;
            if (Plain(Items[i][1]).StartsWith(letter)) return i;
        }
        return -1;
    }

    static string Plain(string s) {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s.Normalize(System.Text.NormalizationForm.FormD)) {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString().ToUpperInvariant();
    }

    public void OpenList() {
        if (Items.Count == 0 || IsOpen) return;
        var list = new ChoiceList(Items, selected, Math.Min(9, Items.Count), scale, Font, small);
        list.Size = new Size(Width - 2, list.Height);
        list.Jump = Jump;
        list.Chosen += (s, e) => { Choose(list.Selected); CloseList(); };
        list.Cancelled += (s, e) => CloseList();
        var host = new ToolStripControlHost(list);
        host.Margin = Padding.Empty;
        host.Padding = Padding.Empty;
        host.AutoSize = false;
        host.Size = list.Size;
        drop = new ToolStripDropDown();
        drop.Padding = new Padding(1);
        drop.Renderer = new DarkRenderer(Palette.Accent);
        drop.DropShadowEnabled = false;
        drop.Items.Add(host);
        drop.Closed += (s, e) => { Invalidate(); Focus(); };
        // Sous le champ, ou au-dessus s'il n'y a pas la place (petit écran, barre des tâches)
        int gap = (int) (4 * scale), listH = list.Height + 2;
        Rectangle area = Screen.FromControl(this).WorkingArea;
        if (PointToScreen(new Point(0, Height + gap)).Y + listH > area.Bottom) {
            drop.Show(this, new Point(0, -gap), ToolStripDropDownDirection.AboveRight);
        } else {
            drop.Show(this, new Point(0, Height + gap));
        }
        list.Focus();
        Invalidate();
    }

    public void CloseList() {
        if (drop != null) drop.Close();
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Palette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool lit = Focused || IsOpen;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Palette.Round(r, 10 * scale))
        using (var b = new SolidBrush(Palette.Field))
        using (var pen = new Pen(lit ? Palette.Accent : Palette.Border, lit ? 1.5f * scale : 1f)) {
            g.FillPath(b, path);
            g.DrawPath(pen, path);
        }
        int pad = (int) (14 * scale), arrow = (int) (10 * scale);
        // La flèche (vers le haut quand la liste est ouverte)
        float ax = Width - pad - arrow, ay = Height / 2f, d = arrow / 2f;
        using (var pen = new Pen(Palette.Dim, 1.8f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
            float tip = IsOpen ? -d / 2 : d / 2;
            g.DrawLines(pen, new[] { new PointF(ax, ay - tip), new PointF(ax + d, ay + tip), new PointF(ax + arrow, ay - tip) });
        }
        if (selected < 0) return;
        string code = Items[selected][0];
        Size codeSize = TextRenderer.MeasureText(g, code, small, Size.Empty, TextFormatFlags.NoPadding);
        int codeX = (int) (ax - 10 * scale - codeSize.Width);
        TextRenderer.DrawText(g, code, small, new Point(codeX, (Height - codeSize.Height) / 2), Palette.Dim, Palette.Field, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Items[selected][1], Font, new Rectangle(pad, 0, codeX - pad - (int) (8 * scale), Height), Palette.Text, Palette.Field,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

}

/**
 * La liste ouverte d'un Picker, dessinée à la main comme le reste (pas de liste ni de barre de défilement de Windows) :
 * le choix en violet, son code à droite, une barre de défilement fine qu'on peut tirer. Souris, molette et clavier
 * (flèches, Page préc./suiv., Début, Fin, une lettre, Entrée, Échap).
 */
sealed class ChoiceList : Control {
    public int Selected;
    public Func<char, int, int> Jump;   // le pays suivant qui commence par cette lettre
    public event EventHandler Chosen, Cancelled;
    readonly List<string[]> items;
    readonly int rows, rowH;
    readonly float scale;
    readonly Font small;
    int top, dragFrom = -1, dragTop;

    public ChoiceList(List<string[]> items, int selected, int rows, float scale, Font font, Font small) {
        this.items = items; this.rows = rows; this.scale = scale; this.small = small;
        rowH = (int) (34 * scale);
        Font = font;
        Selected = Math.Max(0, selected);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Palette.Field;
        Height = rows * rowH;
        top = Math.Max(0, Math.Min(items.Count - rows, Selected - rows / 2));
        Cursor = Cursors.Hand;
    }

    bool Scrolls { get { return items.Count > rows; } }

    RectangleF Thumb() {
        float h = Math.Max(24 * scale, Height * rows / (float) items.Count);
        float y = (Height - h) * top / Math.Max(1, items.Count - rows);
        return new RectangleF(Width - 9 * scale, y + 2 * scale, 5 * scale, h - 4 * scale);
    }

    void Show(int i) {
        Selected = Math.Max(0, Math.Min(items.Count - 1, i));
        if (Selected < top) top = Selected;
        if (Selected >= top + rows) top = Selected - rows + 1;
        Invalidate();
    }

    protected override bool IsInputKey(Keys k) {
        return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End
                || k == Keys.Enter || k == Keys.Escape || k == Keys.Space || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        switch (e.KeyCode) {
            case Keys.Up: Show(Selected - 1); break;
            case Keys.Down: Show(Selected + 1); break;
            case Keys.PageUp: Show(Selected - rows); break;
            case Keys.PageDown: Show(Selected + rows); break;
            case Keys.Home: Show(0); break;
            case Keys.End: Show(items.Count - 1); break;
            case Keys.Enter:
            case Keys.Space: if (Chosen != null) Chosen(this, EventArgs.Empty); break;
            case Keys.Escape: if (Cancelled != null) Cancelled(this, EventArgs.Empty); break;
        }
        e.Handled = true;
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e) {
        int i = Jump != null ? Jump(e.KeyChar, Selected) : -1;
        if (i >= 0) Show(i);
        e.Handled = true;
        base.OnKeyPress(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e) {
        top = Math.Max(0, Math.Min(items.Count - rows, top - Math.Sign(e.Delta) * 3));
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (Scrolls && e.X >= Width - 14 * scale) {   // la barre : on la tire
            dragFrom = e.Y;
            dragTop = top;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (dragFrom >= 0) {
            float per = (Height - Thumb().Height) / Math.Max(1, items.Count - rows);
            top = Math.Max(0, Math.Min(items.Count - rows, dragTop + (int) Math.Round((e.Y - dragFrom) / Math.Max(1f, per))));
            Invalidate();
        } else {
            int i = top + e.Y / rowH;
            if (i < items.Count && i != Selected && e.X < Width - 14 * scale) { Selected = i; Invalidate(); }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        bool dragged = dragFrom >= 0;
        dragFrom = -1;
        Capture = false;
        if (!dragged && e.X < Width - 14 * scale && top + e.Y / rowH < items.Count && Chosen != null) Chosen(this, EventArgs.Empty);
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(Palette.Field);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = (int) (12 * scale), right = (int) ((Scrolls ? 16 : 12) * scale);
        for (int v = 0; v < rows && top + v < items.Count; v++) {
            int i = top + v;
            var row = new Rectangle(0, v * rowH, Width, rowH);
            Color back = Palette.Field;
            if (i == Selected) {
                back = Palette.Mix(Palette.Field, Palette.Accent, 120);
                using (var path = Palette.Round(new RectangleF(4 * scale, row.Y + 2 * scale, Width - (Scrolls ? 18 : 8) * scale, rowH - 4 * scale), 7 * scale))
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
            }
            var r = new Rectangle(pad, row.Y, Width - pad - right, rowH);
            TextRenderer.DrawText(g, items[i][1], Font, r, Palette.Text, back,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, items[i][0], small, r, i == Selected ? Palette.Text : Palette.Dim, back,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }
        if (Scrolls) {
            using (var path = Palette.Round(Thumb(), 2.5f * scale))
            using (var b = new SolidBrush(dragFrom >= 0 ? Palette.AccentHover : Palette.Border)) g.FillPath(b, path);
        }
    }
}

/** Les menus (liste des pays, clic droit dans un champ) : fond sombre, le choix en violet, un liseré. */
sealed class DarkRenderer : ToolStripRenderer {
    readonly Color edge;

    public DarkRenderer(Color edge) { this.edge = edge; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
        using (var b = new SolidBrush(Palette.Field)) e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
        using (var pen = new Pen(edge)) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Palette.Round(new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2), 5))
        using (var b = new SolidBrush(Palette.Mix(Palette.Field, Palette.Accent, 120))) e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
        var item = e.Item as ToolStripMenuItem;
        bool shortcut = item != null && e.Text == item.ShortcutKeyDisplayString;
        e.TextColor = !e.Item.Enabled ? Palette.Border : shortcut ? Palette.Dim : Palette.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
        int y = e.Item.Height / 2;
        using (var pen = new Pen(Palette.Border)) e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }
}

/**
 * Une question ou un message aux couleurs de l'installateur (les boîtes de Windows sont blanches) : une pastille de
 * couleur (? ou !), un titre, le texte, un ou deux boutons. Entrée : le bouton violet ; Échap : l'autre.
 */
sealed class Dialog : Form {
    readonly string heading, text;
    readonly Color tint;
    readonly char glyph;
    readonly Font head, body;
    readonly float scale;
    readonly RoundButton yes, no;
    readonly int pad, icon, textX, headH;
    bool answer;

    Dialog(string heading, string text, string yesText, string noText, Tone tone, float scale) {
        this.heading = heading; this.text = text; this.scale = scale;
        tint = Palette.For(tone);
        glyph = tone == Tone.Info || tone == Tone.Action ? '?' : '!';
        head = new Font("Segoe UI Semibold", 17f * scale, GraphicsUnit.Pixel);
        body = new Font("Segoe UI", 14f * scale, GraphicsUnit.Pixel);
        Text = "FrontEndTV";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Back;
        ForeColor = Palette.Text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        int w = (int) (440 * scale);
        pad = (int) (26 * scale);
        icon = (int) (40 * scale);
        textX = pad + icon + (int) (16 * scale);
        int textW = w - textX - pad;
        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        headH = TextRenderer.MeasureText(heading, head, new Size(textW, int.MaxValue), wrap).Height;
        int bodyH = string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, body, new Size(textW, int.MaxValue), wrap).Height;
        int bottom = pad + Math.Max(icon, headH + (int) (8 * scale) + bodyH) + (int) (26 * scale);
        int bw = (int) (136 * scale), bh = (int) (44 * scale), gap = (int) (12 * scale);
        Font buttons = new Font("Segoe UI Semibold", 14.5f * scale, GraphicsUnit.Pixel);
        yes = new RoundButton(yesText, Palette.Accent, Palette.AccentHover, Color.White, scale);
        yes.Gradient = true;
        yes.Font = buttons;
        yes.Bounds = new Rectangle(w - pad - bw, bottom, bw, bh);
        yes.Click += (s, e) => Done(true);
        Controls.Add(yes);
        if (noText != null) {
            no = new RoundButton(noText, Palette.Field, Palette.Border, Palette.Text, scale);
            no.Font = buttons;
            no.Bounds = new Rectangle(yes.Left - gap - bw, bottom, bw, bh);
            no.Click += (s, e) => Done(false);
            Controls.Add(no);
        }
        ClientSize = new Size(w, bottom + bh + pad);
    }

    void Done(bool value) {
        answer = value;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        Palette.Theme(this);
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        (no ?? yes).Focus();   // un Entrée par mégarde ne fait rien de grave : le bouton choisi d'abord est « non »
    }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData) {
        if (keyData == Keys.Escape) { Done(false); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(Palette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var circle = new RectangleF(pad, pad, icon, icon);
        using (var b = new SolidBrush(Palette.Mix(Palette.Back, tint, 60))) g.FillEllipse(b, circle);
        using (var pen = new Pen(tint, 1.5f * scale)) g.DrawEllipse(pen, circle);
        Size gs = TextRenderer.MeasureText(g, glyph.ToString(), head, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, glyph.ToString(), head, new Point((int) (circle.X + (icon - gs.Width) / 2f), (int) (circle.Y + (icon - gs.Height) / 2f)),
                tint, Palette.Mix(Palette.Back, tint, 60), TextFormatFlags.NoPadding);
        int textW = ClientSize.Width - textX - pad;
        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        int top = string.IsNullOrEmpty(text) ? pad + Math.Max(0, (icon - headH) / 2) : pad + (int) (2 * scale);
        TextRenderer.DrawText(g, heading, head, new Rectangle(textX, top, textW, headH), Palette.Text, Palette.Back, wrap);
        if (!string.IsNullOrEmpty(text)) {
            TextRenderer.DrawText(g, text, body, new Rectangle(textX, top + headH + (int) (8 * scale), textW, ClientSize.Height),
                    Palette.Dim, Palette.Back, wrap);
        }
    }

    /** Une question à deux réponses : vrai si on choisit `yes`. */
    public static bool Ask(IWin32Window owner, string heading, string text, string yes, string no) {
        using (var d = new Dialog(heading, text, yes, no, Tone.Action, Palette.Scale)) {
            d.ShowDialog(owner);
            return d.answer;
        }
    }

    /** -SelfTest : la question « Remettre l'écran d'accueil d'origine ? » en image, barre de titre comprise. */
    public static void Snapshot(Form owner, string file) {
        using (var d = new Dialog("Remettre l'écran d'accueil d'origine ?", "Les applis mises de côté reviennent comme avant. "
                + "FrontEndTV reste installé : tu pourras y revenir.", "Remettre", "Annuler", Tone.Action, Palette.Scale)) {
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(owner.Left + (owner.Width - d.Width) / 2, owner.Top + (owner.Height - d.Height) / 2);
            d.Show(owner);
            d.Refresh();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            using (var b = new Bitmap(d.Width, d.Height)) {
                try {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(d.Location, Point.Empty, d.Size);
                } catch (Exception) {
                    d.DrawToBitmap(b, new Rectangle(Point.Empty, d.Size));
                }
                b.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            d.Close();
        }
    }

    /** Un message, avec un seul bouton. */
    public static void Tell(IWin32Window owner, string heading, string text, Tone tone) {
        using (var d = new Dialog(heading, text, "OK", null, tone, Palette.Scale)) {
            if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
            d.ShowDialog(owner);
        }
    }

    protected override void Dispose(bool disposing) {
        if (disposing) { head.Dispose(); body.Dispose(); }
        base.Dispose(disposing);
    }
}

/** Les pays (code ISO pour TMDB, nom en français), triés par nom. */
static class Countries {
    public static readonly string[][] All = Sorted(new[] {
        "ZA Afrique du Sud", "DZ Algérie", "DE Allemagne", "SA Arabie saoudite", "AR Argentine", "AU Australie", "AT Autriche",
        "BE Belgique", "BO Bolivie", "BR Brésil", "BG Bulgarie", "CM Cameroun", "CA Canada", "CL Chili", "CN Chine", "CO Colombie",
        "KR Corée du Sud", "CR Costa Rica", "CI Côte d'Ivoire", "HR Croatie", "DK Danemark", "EG Égypte", "AE Émirats arabes unis",
        "EC Équateur", "ES Espagne", "EE Estonie", "US États-Unis", "FI Finlande", "FR France", "GR Grèce", "GP Guadeloupe",
        "GT Guatemala", "GF Guyane", "HK Hong Kong", "HU Hongrie", "IN Inde", "ID Indonésie", "IE Irlande", "IS Islande",
        "IL Israël", "IT Italie", "JM Jamaïque", "JP Japon", "KE Kenya", "RE La Réunion", "LV Lettonie", "LB Liban", "LT Lituanie",
        "LU Luxembourg", "MG Madagascar", "MY Malaisie", "MA Maroc", "MQ Martinique", "MU Maurice", "YT Mayotte", "MX Mexique",
        "MC Monaco", "NC Nouvelle-Calédonie", "NG Nigeria", "NO Norvège", "NZ Nouvelle-Zélande", "PA Panama", "PY Paraguay",
        "NL Pays-Bas", "PE Pérou", "PH Philippines", "PL Pologne", "PF Polynésie française", "PT Portugal", "QA Qatar",
        "DO République dominicaine", "CZ République tchèque", "RO Roumanie", "GB Royaume-Uni", "RU Russie", "SN Sénégal",
        "RS Serbie", "SG Singapour", "SK Slovaquie", "SI Slovénie", "SE Suède", "CH Suisse", "TW Taïwan", "TH Thaïlande",
        "TN Tunisie", "TR Turquie", "UA Ukraine", "UY Uruguay", "VE Venezuela", "VN Viêt Nam" });

    static string[][] Sorted(string[] lines) {
        var list = new List<string[]>();
        foreach (string l in lines) list.Add(new[] { l.Substring(0, 2), l.Substring(3) });
        var french = System.Globalization.CultureInfo.GetCultureInfo("fr-FR").CompareInfo;
        list.Sort((a, b) => french.Compare(a[1], b[1], System.Globalization.CompareOptions.IgnoreCase));
        return list.ToArray();
    }
}
}
