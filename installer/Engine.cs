// FrontEndTV — installateur : tout le travail avec la TV, sans interface. Program.cs l'affiche, Adb.cs parle à la TV.
// Développé par Florian. Écrit en C# 5 : il se compile avec le csc.exe livré avec Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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
    public const int StepConnect = 0, StepInstall = 1, StepActivate = 2;

    /** Dossier de travail : la clé ADB de ce PC, le journal, l'APK extrait, l'adresse de la dernière TV. */
    public static readonly string Home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrontEndTV");
    public static readonly string LogFile = Path.Combine(Home, "installateur-journal.txt");
    static readonly string MemoryFile = Path.Combine(Home, "installateur.ini");
    static readonly string KeyFile = Path.Combine(Home, "cle-adb.xml");
    static readonly object LogLock = new object();

    readonly IReport ui;
    readonly Options o;
    AdbKey adbKey;
    AdbDevice device;
    string host;
    int port, sdk;
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
        try { return RunConnected(); }
        finally { Disconnect(); }
    }

    int switchStep = StepActivate;   // l'étape où SwitchHome montre son avancement
    bool replaced;   // l'ancienne version (autre clé) a été désinstallée : ses réglages sont perdus

    /** Envoie l'APK à la TV ; renvoie ce que dit Android (« Success » ou l'erreur). */
    string SendApk(string apk) {
        string size = (new FileInfo(apk).Length / 1048576.0).ToString("0.0") + " Mo";
        ui.Say(Tone.Info, "Si la TV affiche Play Protect : « Plus de détails » → « Installer quand même ».");
        string r;
        try {
            r = device.Install(apk, p => ui.Step(StepInstall, StepState.Running, "Envoi de l'appli (" + size + ")... " + p + " %"));
        } catch (IOException e) {
            Disconnect();
            throw new SetupException("L'envoi de l'appli a été coupé (" + e.Message + "). Vérifie le Wi-Fi, puis relance.");
        }
        Log("install -> " + r.Trim());
        return r;
    }

    string RunConnected() {
        string address = Normalize(o.Ip);
        if (address == null) throw new SetupException("Adresse IP invalide : « " + o.Ip + " » (exemple : 192.168.1.50).");
        string[] parts = address.Split(':');
        host = parts[0];
        port = int.Parse(parts[1]);
        ui.Step(StepConnect, StepState.Running, "Connexion à " + Display(address) + "...");
        adbKey = AdbKey.LoadOrCreate(KeyFile);
        Connect(120000);
        string model = (Shell("getprop ro.product.manufacturer") + " " + Shell("getprop ro.product.model")).Trim();
        int.TryParse(Shell("getprop ro.build.version.sdk"), out sdk);
        string release = Shell("getprop ro.build.version.release");
        Remember("ip", address);
        if (sdk > 0 && sdk < 26) throw new SetupException("Cette TV a Android " + release + " : FrontEndTV demande Android 8 ou plus récent.");
        ui.Step(StepConnect, StepState.Done, model + " · Android " + release);
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
            string r = SendApk(apk);
            if (r.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE")) {
                // Une ancienne version signée avec une autre clé : Android refuse de la mettre à jour. On la remplace,
                // une fois : accueil d'origine d'abord (ses applis mises de côté reviennent), puis désinstallation.
                ui.Say(Tone.Info, "Ta TV a une ancienne version de FrontEndTV signée avec une autre clé : je la remplace (une seule fois).");
                if (wasHome) {
                    switchStep = StepInstall;
                    bool back = SwitchHome("original", "", false);
                    switchStep = StepActivate;
                    if (!back) throw new SetupException("Impossible de remettre l'accueil d'origine avant de remplacer l'ancienne version. "
                            + "Sur la TV : FrontEndTV → Écran d'accueil → Original, puis relance.");
                }
                ui.Step(StepInstall, StepState.Running, "Désinstallation de l'ancienne version...");
                string u = Shell("pm uninstall " + Pkg);
                if (!u.Contains("Success")) throw new SetupException("L'ancienne version n'a pas pu être désinstallée : " + LastLine(u));
                first = true;
                wasHome = false;
                replaced = true;
                r = SendApk(apk);
            }
            ui.Say(Tone.Info, null);
            if (!r.Contains("Success")) {
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
        string done = "C'est fait ! FrontEndTV est ton écran d'accueil : il vient de s'ouvrir sur la TV.";
        if (replaced && key.Length == 0) {
            done += " L'ancienne version a été remplacée : relance l'installateur avec ta clé TMDB pour retrouver les recommandations.";
        }
        return done;
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
        return "L'écran d'accueil d'origine est revenu, avec toutes les applis. Pour supprimer FrontEndTV : Paramètres → Applications → FrontEndTV → Désinstaller.";
    }

    // MARK: - Connexion (client ADB intégré : Adb.cs)

    void Connect(int approvalMs) {
        Disconnect();
        try {
            device = AdbDevice.Connect(host, port, adbKey, "FrontEndTV-Installateur@" + Environment.MachineName, approvalMs, () => {
                ui.Say(Tone.Action, "Regarde la TV : accepte « Autoriser le débogage USB ? » en cochant « Toujours autoriser ».");
                ui.Step(StepConnect, StepState.Running, "En attente de ton accord sur la TV...");
            });
            Log("connecté à " + host + ":" + port);
            ui.Say(Tone.Info, null);
        } catch (IOException e) {
            Log("connexion : " + e.Message);
            if (e.Message.StartsWith("pas de réponse")) {
                throw new SetupException("La TV ne répond pas à l'adresse " + host + ". Vérifie qu'elle est allumée, sur le même réseau "
                        + "que ce PC, et que « Débogage USB » est activé (Paramètres → Système → Options pour les développeurs).");
            }
            throw new SetupException("Connexion à la TV impossible : " + e.Message + ".");
        }
    }

    /** Reconnecte en silence (TV qui a coupé, Wi-Fi qui a sauté) ; false si elle ne répond pas encore. */
    bool Reconnect() {
        try { Connect(15000); return true; } catch (SetupException) { return false; }
    }

    void Disconnect() {
        if (device != null) { device.Dispose(); device = null; }
    }

    /** Une commande dans le shell de la TV ; "" si la connexion a sauté (elle sera refaite au prochain essai). */
    string Shell(string command) {
        if (device == null && !Reconnect()) return "";
        string r;
        try {
            r = device.Shell(command).Trim();
        } catch (IOException e) {
            r = "";
            Log("shell : " + e.Message);
            Disconnect();
        }
        Log("$ " + command + " -> " + r.Replace("\r", "").Replace("\n", " | "));
        return r;
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
            Disconnect();
            ui.WaitForUser(Prompt, 45);
        }
        string doing = mode == "free" ? "Activation (les applis publicitaires sont mises de côté)..." : "Retour à l'accueil d'origine...";
        bool prompted = false;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 240) {
            ui.Step(switchStep, StepState.Running, doing + " " + (int) clock.Elapsed.TotalSeconds + " s");
            Thread.Sleep(2000);
            if (device == null && !Reconnect()) continue;
            if (SwitchDone(mode)) { ui.Say(Tone.Info, null); return true; }
            if (!prompted && sdk >= 30 && DebugPromptShown()) {
                prompted = true;
                ui.Say(Tone.Action, Prompt);
            }
        }
        return false;
    }

    // MARK: - L'APK

    /**
     * L'APK à installer : celui donné avec -Apk ; sinon FrontEndTV.apk à côté de l'installateur ou déjà téléchargé, s'il
     * est bien celui de cette version (même empreinte SHA-256) ; sinon celui de la Release GitHub de cette version,
     * téléchargé puis vérifié. Il n'est pas dans l'.exe : un gros contenu compressé caché dans un programme fait partie
     * de ce qui fait réagir les antivirus.
     */
    string ApkFile() {
        if (!string.IsNullOrEmpty(o.Apk)) {
            if (!File.Exists(o.Apk)) throw new SetupException("APK introuvable : " + o.Apk);
            return Path.GetFullPath(o.Apk);
        }
        string beside = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "FrontEndTV.apk");
        string cached = Path.Combine(Home, "FrontEndTV-" + BuildInfo.Version + ".apk");
        foreach (string f in new[] { beside, cached }) {
            if (File.Exists(f) && Sha256(f) == BuildInfo.ApkSha256) return f;
        }
        ui.Step(StepInstall, StepState.Running, "Téléchargement de FrontEndTV " + BuildInfo.Version + "...");
        string part = cached + ".part";
        try {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest) WebRequest.Create(BuildInfo.ApkUrl);
            request.Timeout = 30000;
            request.UserAgent = "FrontEndTV-Installateur";
            using (var response = request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(part)) {
                long total = response.ContentLength, done = 0;
                var buffer = new byte[65536];
                int n, shown = -1;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) {
                    output.Write(buffer, 0, n);
                    done += n;
                    int percent = total > 0 ? (int) (done * 100 / total) : -1;
                    if (percent >= 0 && percent != shown) {
                        shown = percent;
                        ui.Step(StepInstall, StepState.Running, "Téléchargement de FrontEndTV " + BuildInfo.Version + "... " + percent + " %");
                    }
                }
            }
        } catch (Exception e) {
            try { File.Delete(part); } catch (Exception) {}
            Log("téléchargement : " + e.Message);
            throw new SetupException("Impossible de télécharger FrontEndTV (Internet ?) : " + e.Message
                    + ". Sinon, mets FrontEndTV.apk de la version " + BuildInfo.Version + " à côté de l'installateur.");
        }
        if (Sha256(part) != BuildInfo.ApkSha256) {
            File.Delete(part);
            throw new SetupException("Le fichier téléchargé n'est pas celui attendu (empreinte différente) : rien n'a été installé.");
        }
        if (File.Exists(cached)) File.Delete(cached);
        File.Move(part, cached);
        return cached;
    }

    static string Sha256(string file) {
        using (var sha = SHA256.Create())
        using (var f = File.OpenRead(file)) {
            return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }
    }

    // MARK: - Adresses (la recherche des TV est dans Discovery.cs)

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
}
}
