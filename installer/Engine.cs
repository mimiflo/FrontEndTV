// FrontEndTV — installateur : tout le travail avec la TV (adb), sans interface. Program.cs l'affiche.
// Développé par Florian. Écrit en C# 5 : il se compile avec le csc.exe livré avec Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FrontEndTV.Setup {

enum StepState { Waiting, Running, Done, Failed, Skipped }
enum Tone { Info, Action, Success, Warning, Error }

/** Ce que l'installateur montre pendant le travail (appelé depuis le fil de travail). */
interface IReport {
    void Step(int index, StepState state, string detail);
    /** Le message du moment (null l'efface). */
    void Say(Tone tone, string text);
    /** Montre `text` avec un bouton « C'est fait » ; revient quand on l'a pressé ou après `seconds` secondes. */
    void WaitForUser(string text, int seconds);
}

sealed class Options {
    public string Ip = "", Name = "", Region = "", Key = "", Apk = "";
    public bool Original;
}

sealed class SetupException : Exception {
    public SetupException(string message) : base(message) {}
}

sealed class Engine {
    public const string Pkg = "io.freemytv";
    public const int StepTools = 0, StepConnect = 1, StepInstall = 2, StepActivate = 3;

    /** Dossier de travail : adb, le journal, l'APK extrait, l'adresse de la dernière TV. */
    public static readonly string Home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrontEndTV");
    public static readonly string LogFile = Path.Combine(Home, "installateur-journal.txt");
    static readonly string MemoryFile = Path.Combine(Home, "installateur.ini");
    static readonly object LogLock = new object();

    public string AdbPath;   // trouvé (ou téléchargé) à l'étape 1 ; réglable pour les essais
    readonly IReport ui;
    readonly Options o;
    string tv;   // "ip:port"
    int sdk;
    string secret = "";   // jamais dans le journal

    public Engine(IReport ui, Options o) {
        this.ui = ui;
        this.o = o;
    }

    // MARK: - Déroulé

    /** Fait tout ; renvoie le message de fin, ou lève SetupException avec ce qui bloque. */
    public string Run() {
        Directory.CreateDirectory(Home);
        Log("=== " + DateTime.Now.ToString("s") + (o.Original ? " (retour à l'origine)" : ""));
        secret = o.Key ?? "";

        ui.Step(StepTools, StepState.Running, "Recherche d'adb...");
        if (AdbPath == null) AdbPath = FindAdb();
        Adb(30000, "start-server");
        ui.Step(StepTools, StepState.Done, "Prêts");

        ui.Step(StepConnect, StepState.Running, "Connexion à " + Display(o.Ip) + "...");
        string address = Normalize(o.Ip);
        if (address == null) throw new SetupException("Adresse IP invalide : « " + o.Ip + " » (exemple : 192.168.1.50).");
        Connect(address);
        string model = Shell("getprop ro.product.manufacturer") + " " + Shell("getprop ro.product.model");
        int.TryParse(Shell("getprop ro.build.version.sdk"), out sdk);
        string release = Shell("getprop ro.build.version.release");
        Remember("ip", address);
        if (sdk > 0 && sdk < 26) throw new SetupException("Cette TV a Android " + release + " : FrontEndTV demande Android 8 ou plus récent.");
        ui.Step(StepConnect, StepState.Done, model.Trim() + " · Android " + release);
        bool wasHome = HomeActivity().StartsWith(Pkg + "/");

        return o.Original ? RunOriginal() : RunInstall(wasHome);
    }

    string RunInstall(bool wasHome) {
        ui.Step(StepInstall, StepState.Running, "Préparation...");
        string apk = ApkFile();
        bool first = !Installed();
        string installed = first ? "" : InstalledHash();
        if (installed.Length > 0 && installed == Sha256(apk)) {
            ui.Step(StepInstall, StepState.Done, "Déjà à jour");
        } else {
            ui.Step(StepInstall, StepState.Running, "Envoi de l'appli (" + (new FileInfo(apk).Length / 1048576.0).ToString("0.0") + " Mo)...");
            ui.Say(Tone.Info, "Si la TV affiche Play Protect : « Plus de détails » → « Installer quand même ».");
            string r = Adb(300000, "-s", tv, "install", "-r", apk);
            ui.Say(Tone.Info, null);
            if (!r.Contains("Success")) {
                if (r.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
                    throw new SetupException("Une autre version de FrontEndTV (signée autrement) est déjà sur la TV. Remets l'accueil d'origine, désinstalle-la (Paramètres → Applications), puis relance.");
                if (r.Contains("INSTALL_FAILED_VERSION_DOWNGRADE"))
                    throw new SetupException("La TV a déjà une version plus récente de FrontEndTV.");
                if (Regex.IsMatch(r, "USER_RESTRICTED|VERIFICATION_FAILURE|rejected"))
                    throw new SetupException("La TV a refusé l'installation (Play Protect, ou « Installer via USB » désactivé). Accepte sur la TV, puis relance.");
                throw new SetupException("L'installation a échoué : " + LastLine(r));
            }
            ui.Step(StepInstall, StepState.Done, first ? "Appli installée" : "Appli mise à jour");
        }
        // TCL et quelques marques bloquent les applis en arrière-plan : on les autorise
        Shell("appops set " + Pkg + " AUTO_START allow 2>/dev/null; appops set " + Pkg + " APP_AUTO_START allow 2>/dev/null");

        ui.Step(StepActivate, StepState.Running, "Envoi de tes réglages...");
        string extras = "";
        string region = Regex.Replace(o.Region ?? "", "[^A-Za-z]", "").ToUpperInvariant();
        if (region.Length != 2 && first) region = DefaultRegion();
        if (region.Length == 2) { extras += " --es tmdb_region " + region; Remember("region", region); }
        string name = CleanName(o.Name);
        if (name.Length > 0) extras += " --es user_name '" + name + "'";
        Remember("name", name);
        string key = Regex.Replace(o.Key ?? "", @"[^A-Za-z0-9.\-_]", "");
        if (key.Length > 0) {
            secret = key;
            if (!TmdbKeyWorks(key)) ui.Say(Tone.Warning, "TMDB n'a pas accepté la clé (ou pas d'Internet) : elle est envoyée quand même.");
            extras += " --es tmdb_key '" + key + "'";
        }

        if (!SwitchHome("free", extras, !wasHome)) {
            throw new SetupException("L'activation n'est pas confirmée. Ouvre FrontEndTV sur la TV : la ligne « Écran d'accueil » explique ce qui bloque. "
                    + "Cas fréquent : un Wi-Fi enregistré pendant la configuration de la TV ; oublie-le, reconnecte-toi, puis relance.");
        }
        Thread.Sleep(2000);
        Shell("input keyevent KEYCODE_HOME");
        ui.Step(StepActivate, StepState.Done, "FrontEndTV est l'écran d'accueil");
        Adb(10000, "disconnect", tv);
        return "C'est fait ! FrontEndTV est ton écran d'accueil : il vient de s'ouvrir sur la TV.";
    }

    string RunOriginal() {
        ui.Step(StepInstall, StepState.Skipped, "Rien à installer");
        ui.Step(StepActivate, StepState.Running, "Retour à l'accueil d'origine...");
        if (!Installed()) {
            ui.Step(StepActivate, StepState.Done, "FrontEndTV n'est pas installé");
            return "FrontEndTV n'est pas sur cette TV : l'écran d'accueil d'origine est déjà là.";
        }
        if (!SwitchHome("original", "", false)) {
            throw new SetupException("Pas encore confirmé. Ouvre FrontEndTV sur la TV → Écran d'accueil → Original.");
        }
        Shell("input keyevent KEYCODE_HOME");
        ui.Step(StepActivate, StepState.Done, "Accueil d'origine remis");
        Adb(10000, "disconnect", tv);
        return "L'écran d'accueil d'origine est revenu, avec toutes les applis. Pour supprimer FrontEndTV : Paramètres → Applications → FrontEndTV → Désinstaller.";
    }

    // MARK: - adb

    string FindAdb() {
        var candidates = new List<string> { Path.Combine(Home, @"platform-tools\adb.exe") };
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, @"Android\Sdk\platform-tools\adb.exe"));
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            try { if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "adb.exe")); } catch (ArgumentException) {}
        }
        foreach (string c in candidates) if (File.Exists(c)) return c;

        // Les outils officiels de Google (~10 Mo), une seule fois
        ui.Step(StepTools, StepState.Running, "Téléchargement des outils Android de Google...");
        string zip = Path.Combine(Home, "platform-tools.zip");
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest) WebRequest.Create("https://dl.google.com/android/repository/platform-tools-latest-windows.zip");
            request.Timeout = 30000;
            using (var response = request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(zip)) {
                long total = response.ContentLength, done = 0;
                var buffer = new byte[65536];
                int n, shown = -1;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) {
                    output.Write(buffer, 0, n);
                    done += n;
                    int percent = total > 0 ? (int) (done * 100 / total) : -1;
                    if (percent != shown && percent >= 0) {
                        shown = percent;
                        ui.Step(StepTools, StepState.Running, "Téléchargement des outils Android de Google... " + percent + " %");
                    }
                }
            }
            using (var archive = new ZipArchive(File.OpenRead(zip), ZipArchiveMode.Read)) {
                foreach (var entry in archive.Entries) {
                    if (!entry.FullName.StartsWith("platform-tools/") || entry.FullName.EndsWith("/") || entry.FullName.Contains("..")) continue;
                    string target = Path.Combine(Home, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var from = entry.Open())
                    using (var to = File.Create(target)) from.CopyTo(to);
                }
            }
        } catch (Exception e) {
            throw new SetupException("Impossible de télécharger les outils Android (Internet ?) : " + e.Message);
        } finally {
            try { File.Delete(zip); } catch (Exception) {}
        }
        string adb = Path.Combine(Home, @"platform-tools\adb.exe");
        if (!File.Exists(adb)) throw new SetupException("adb introuvable après le téléchargement.");
        return adb;
    }

    /** adb avec ses arguments ; renvoie sa sortie (stdout et stderr), ne lève jamais d'erreur d'adb. */
    string Adb(int timeoutMs, params string[] args) {
        var start = new ProcessStartInfo(AdbPath, string.Join(" ", args.Select(Quote)));
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        var text = new StringBuilder();
        var outDone = new ManualResetEvent(false);
        var errDone = new ManualResetEvent(false);
        string result;
        try {
            using (var p = new Process()) {
                p.StartInfo = start;
                p.OutputDataReceived += (s, e) => { if (e.Data == null) outDone.Set(); else lock (text) text.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data == null) errDone.Set(); else lock (text) text.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) {
                    try { p.Kill(); } catch (Exception) {}
                    lock (text) text.AppendLine("(adb ne répond plus)");
                }
                // Le serveur adb lancé au passage garde parfois la sortie ouverte : on n'attend pas plus de 2 s.
                if (outDone.WaitOne(2000)) errDone.WaitOne(500);
            }
            lock (text) result = text.ToString().Trim();
        } catch (Exception e) {
            result = "(adb n'a pas pu démarrer : " + e.Message + ")";
        }
        Log("adb " + string.Join(" ", args) + " -> " + result.Replace("\r", "").Replace("\n", " | "));
        return result;
    }

    string Shell(string command) { return Adb(60000, "-s", tv, "shell", command).Trim(); }
    string State() { return Adb(15000, "-s", tv, "get-state").Trim(); }

    void Connect(string address) {
        tv = address;
        Adb(10000, "disconnect", tv);
        string r = Adb(20000, "connect", tv);
        if (Regex.IsMatch(r, "cannot|failed|refused|unable|timed out", RegexOptions.IgnoreCase)) {
            throw new SetupException("Pas de réponse de " + Display(address) + ". Vérifie que la TV est allumée, sur le même réseau, et que « Débogage USB » est activé.");
        }
        bool asked = false;
        for (int i = 0; i < 120; i++) {
            string state = State();
            if (state == "device") { ui.Say(Tone.Info, null); return; }
            if (!asked) {
                asked = true;
                ui.Say(Tone.Action, "Regarde la TV : accepte « Autoriser le débogage USB ? » en cochant « Toujours autoriser ».");
            }
            ui.Step(StepConnect, StepState.Running, "En attente de ton accord sur la TV... " + (120 - i) + " s");
            Thread.Sleep(1000);
            if (i % 6 == 5 && state != "unauthorized") Adb(20000, "connect", tv);
        }
        throw new SetupException("La TV n'a pas autorisé ce PC (2 minutes). Relance et accepte le message sur la TV.");
    }

    string HomeActivity() {
        string r = Shell("cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME");
        return LastLine(r);
    }

    bool Installed() { return Shell("pm path " + Pkg).Contains("package:"); }

    /** Empreinte SHA-256 de l'APK installé ("" si la TV ne sait pas la calculer). */
    string InstalledHash() {
        string r = Shell("p=$(pm path " + Pkg + " | head -n 1); p=${p#package:}; test -f ${p:-/nonexistent} && sha256sum $p 2>/dev/null");
        Match m = Regex.Match(r, "^([0-9a-fA-F]{64})");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
    }

    /** « Autoriser le débogage USB ? » est à l'écran de la TV. */
    bool DebugPromptShown() {
        return Regex.IsMatch(Shell("dumpsys window | grep -E mCurrentFocus"), "debugging", RegexOptions.IgnoreCase);
    }

    bool SwitchDone(string mode) {
        string current = HomeActivity();
        if (mode == "free") return current.StartsWith(Pkg + "/");
        return current.Length > 0 && !current.StartsWith(Pkg + "/") && !current.Contains("ResolverActivity")
                && !current.Contains("Error") && current.Contains("/");
    }

    /**
     * Ouvre FrontEndTV sur la TV avec la demande de bascule, puis attend qu'elle soit faite. L'appli a sa propre
     * autorisation de débogage : la première fois, la TV affiche un second « Autoriser le débogage USB ? ».
     * maybePrompt : ce second message peut apparaître (l'accueil n'était pas encore FrontEndTV).
     */
    bool SwitchHome(string mode, string extras, bool maybePrompt) {
        const string Prompt = "Sur la TV, FrontEndTV demande à son tour « Autoriser le débogage USB ? » : coche « Toujours autoriser », puis OK.";
        Shell("am start -n " + Pkg + "/.SettingsActivity --es switch " + mode + extras);
        if (maybePrompt && sdk < 30) {
            // Android 8 à 10 peut donner la réponse du message au PC encore connecté : on se déconnecte pendant ce temps.
            Adb(10000, "disconnect", tv);
            ui.WaitForUser(Prompt + "\nPas de message sur la TV ? Appuie simplement sur « C'est fait ».", 45);
            Adb(20000, "connect", tv);
        }
        string doing = mode == "free" ? "Activation (les applis publicitaires sont mises de côté)..." : "Retour à l'accueil d'origine...";
        bool prompted = false;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 240) {
            ui.Step(StepActivate, StepState.Running, doing + " " + (int) clock.Elapsed.TotalSeconds + " s");
            Thread.Sleep(2000);
            if (State() != "device") { Adb(20000, "connect", tv); continue; }
            if (SwitchDone(mode)) { ui.Say(Tone.Info, null); return true; }
            if (!prompted && sdk >= 30 && DebugPromptShown()) {
                prompted = true;
                ui.Say(Tone.Action, Prompt);
            }
        }
        return false;
    }

    // MARK: - L'APK

    /** L'APK choisi (-Apk), sinon celui intégré à cet .exe, copié dans le dossier de travail. */
    string ApkFile() {
        if (!string.IsNullOrEmpty(o.Apk)) {
            if (!File.Exists(o.Apk)) throw new SetupException("APK introuvable : " + o.Apk);
            return Path.GetFullPath(o.Apk);
        }
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FrontEndTV.apk")) {
            if (s == null) throw new SetupException("Cet installateur ne contient pas FrontEndTV (il doit être créé par build.sh).");
            string target = Path.Combine(Home, "FrontEndTV.apk");
            using (var f = File.Create(target)) s.CopyTo(f);
            return target;
        }
    }

    static string Sha256(string file) {
        using (var sha = SHA256.Create())
        using (var f = File.OpenRead(file)) {
            return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }
    }

    // MARK: - Trouver la TV

    /** Les adresses du réseau local qui répondent sur le port 5555 (le débogage réseau d'Android), en ~1 seconde. */
    public static List<string> FindTVs() {
        var own = new List<string>();
        var prefixes = new List<string>();
        try {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses) {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = a.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    own.Add(ip);
                    string prefix = ip.Substring(0, ip.LastIndexOf('.'));
                    if (!prefixes.Contains(prefix)) prefixes.Add(prefix);
                }
            }
        } catch (Exception) {}
        var probes = new List<KeyValuePair<string, TcpClient>>();
        var tasks = new List<Task>();
        foreach (string prefix in prefixes) {
            for (int n = 1; n <= 254; n++) {
                string ip = prefix + "." + n;
                var c = new TcpClient();
                try { tasks.Add(c.ConnectAsync(ip, 5555)); probes.Add(new KeyValuePair<string, TcpClient>(ip, c)); }
                catch (Exception) { c.Close(); }
            }
        }
        // Une TV répond en quelques millisecondes ; on laisse jusqu'à 1,5 s aux Wi-Fi lents
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1500) {
            Thread.Sleep(100);
            bool any = tasks.Any(t => t.Status == TaskStatus.RanToCompletion);
            if (any && clock.ElapsedMilliseconds >= 800) break;
            if (tasks.All(t => t.IsCompleted)) break;
        }
        var found = new List<string>();
        for (int i = 0; i < probes.Count; i++) {
            if (tasks[i].Status == TaskStatus.RanToCompletion && !own.Contains(probes[i].Key)) found.Add(probes[i].Key);
        }
        foreach (var p in probes) { try { p.Value.Close(); } catch (Exception) {} }
        foreach (var t in tasks) { var ignored = t.Exception; }   // marque les erreurs comme vues
        return found;
    }

    /** La TV de la dernière fois, si elle répond. */
    public static string LastTV() {
        string address = Normalize(Recall("ip"));
        if (address == null) return null;
        string[] parts = address.Split(':');
        using (var c = new TcpClient()) {
            try { return c.ConnectAsync(parts[0], int.Parse(parts[1])).Wait(1500) && c.Connected ? Display(address) : null; }
            catch (Exception) { return null; }
        }
    }

    /** "1.2.3.4" → "1.2.3.4:5555" ; "1.2.3.4:37000" tel quel ; sinon null. */
    public static string Normalize(string a) {
        a = Regex.Replace(a ?? "", @"\s", "");
        if (Regex.IsMatch(a, @"^\d{1,3}(\.\d{1,3}){3}$")) return a + ":5555";
        if (Regex.IsMatch(a, @"^\d{1,3}(\.\d{1,3}){3}:\d{1,5}$")) return a;
        return null;
    }

    static string Display(string address) {
        return (address ?? "").EndsWith(":5555") ? address.Substring(0, address.Length - 5) : address;
    }

    // MARK: - Réglages

    public static string DefaultRegion() {
        try {
            string r = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant();
            if (Regex.IsMatch(r, "^[A-Z]{2}$") && r != "IV") return r;   // IV : réglage « invariant », pas un pays
        } catch (Exception) {}
        return "FR";
    }

    /** Le prénom sans accents ni caractères spéciaux (il passe dans une commande de la TV). */
    static string CleanName(string t) {
        if (string.IsNullOrEmpty(t)) return "";
        var sb = new StringBuilder();
        foreach (char ch in t.Normalize(NormalizationForm.FormD)) {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @"[^A-Za-z0-9 \-]", "").Trim();
    }

    static bool TmdbKeyWorks(string key) {
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            bool v3 = Regex.IsMatch(key, "^[0-9a-fA-F]{32}$");
            var request = (HttpWebRequest) WebRequest.Create("https://api.themoviedb.org/3/configuration" + (v3 ? "?api_key=" + key : ""));
            request.Timeout = 15000;
            if (!v3) request.Headers["Authorization"] = "Bearer " + key;
            using (request.GetResponse()) return true;
        } catch (Exception) {
            return false;
        }
    }

    /** Petite mémoire locale : l'adresse de la TV, le prénom et le pays (jamais la clé TMDB). */
    public static string Recall(string key) {
        try {
            foreach (string line in File.ReadAllLines(MemoryFile)) {
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq) == key) return line.Substring(eq + 1);
            }
        } catch (Exception) {}
        return "";
    }

    static void Remember(string key, string value) {
        try {
            var lines = File.Exists(MemoryFile) ? File.ReadAllLines(MemoryFile).Where(l => !l.StartsWith(key + "=")).ToList() : new List<string>();
            lines.Add(key + "=" + value);
            File.WriteAllLines(MemoryFile, lines);
        } catch (Exception) {}
    }

    // MARK: - Outils

    void Log(string line) {
        if (secret.Length > 0) line = line.Replace(secret, "***");
        lock (LogLock) {
            try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch (Exception) {}
        }
    }

    static string LastLine(string text) {
        string[] lines = text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
    }

    /** Un argument pour la ligne de commande Windows (entre guillemets, \ et " protégés). */
    static string Quote(string a) {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var b = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a) {
            if (c == '\\') { slashes++; continue; }
            b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            b.Append(c);
        }
        b.Append('\\', slashes * 2).Append('"');
        return b.ToString();
    }
}
}
