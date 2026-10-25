// FrontEndTV — installateur : trouver les TV du réseau local, avec leur nom, leur marque et leur modèle.
// Développé par Florian. Écrit en C# 5 : il se compile avec le csc.exe livré avec Windows.
//
// Seulement des connexions sortantes (rien qui écoute, donc pas de question du pare-feu de Windows), sur trois ports :
// 5555 (le débogage d'Android : la TV est prête), 6466 (la télécommande d'Android TV / Google TV : c'est bien une TV
// Android) et 8008 (Google Cast : sa description UPnP donne son nom, sa marque et son modèle).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FrontEndTV.Setup {

/** Une TV trouvée sur le réseau. */
sealed class Tv {
    public string Ip = "", Name = "", Brand = "", Model = "";
    public bool Adb;      // le port 5555 répond : « Débogage USB » est activé
    public bool Remote;   // le service de télécommande d'Android TV / Google TV répond (port 6466)
    public bool Cast;     // Google Cast répond (port 8008) : il dit son nom, sa marque et son modèle
    public bool Last;     // la TV de la dernière fois

    /** En gros : son nom, sinon sa marque et son modèle, sinon « TV Android ». */
    public string Title() {
        if (Name.Length > 0) return Name;
        string bm = BrandModel();
        return bm.Length > 0 ? bm : "TV Android";
    }

    /** Dessous : la marque et le modèle (s'ils ne sont pas déjà le titre), puis l'adresse. */
    public string Detail() {
        string bm = BrandModel();
        return bm.Length > 0 && bm != Title() ? bm + "  ·  " + Ip : Ip;
    }

    string BrandModel() {
        if (Model.StartsWith(Brand, StringComparison.OrdinalIgnoreCase)) return Model;
        return (Brand + " " + Model).Trim();
    }
}

static class Discovery {
    /**
     * Cherche les TV : celle de la dernière fois et les appareils que Windows connaît déjà (sa table ARP), puis, s'il
     * n'y en a aucune, les 254 adresses du réseau, par groupes de 24 (un balayage d'un coup ressemble à ce que font les
     * logiciels malveillants, et les antivirus le surveillent). `update` reçoit la liste à chaque nouveauté, depuis ce fil.
     */
    public static List<Tv> Find(bool wholeNetwork, Action<List<Tv>> update) {
        var tvs = new Dictionary<string, Tv>();
        var own = new List<string>();
        var prefixes = new List<string>();
        LocalAddresses(own, prefixes);

        string last = Engine.Normalize(Engine.Recall("ip")) ?? "";
        last = last.EndsWith(":5555") ? last.Substring(0, last.Length - 5) : "";
        var knock = new List<string>();
        if (last.Length > 0) knock.Add(last);
        foreach (string ip in Neighbors()) {
            if (InNetwork(ip, prefixes) && !own.Contains(ip) && !knock.Contains(ip)) knock.Add(ip);
        }
        Knock(knock, tvs, 1200);
        if (last.Length > 0) lock (tvs) { if (tvs.ContainsKey(last)) tvs[last].Last = true; }
        if (update != null) update(List(tvs));
        DescribeAll(tvs);
        if (update != null) update(List(tvs));

        if (wholeNetwork && List(tvs).Count == 0) {
            foreach (string prefix in prefixes) {
                var all = new List<string>();
                for (int n = 1; n <= 254; n++) {
                    string ip = prefix + "." + n;
                    if (!own.Contains(ip) && !knock.Contains(ip)) all.Add(ip);
                }
                for (int i = 0; i < all.Count; i += 24) {
                    int before = List(tvs).Count;
                    Knock(all.Skip(i).Take(24).ToList(), tvs, 500);
                    if (List(tvs).Count > before && update != null) update(List(tvs));
                }
            }
            DescribeAll(tvs);
        }
        var result = List(tvs);
        if (update != null) update(result);
        return result;
    }

    /** Les TV à montrer (prêtes, ou qui s'annoncent comme Android TV) : la dernière fois d'abord, puis les prêtes. */
    static List<Tv> List(Dictionary<string, Tv> tvs) {
        lock (tvs) {
            return tvs.Values.Where(t => t.Adb || t.Remote)
                    .OrderBy(t => t.Last ? 0 : t.Adb ? 1 : 2).ThenBy(t => t.Title(), StringComparer.CurrentCultureIgnoreCase)
                    .Select(t => new Tv { Ip = t.Ip, Name = t.Name, Brand = Brand(t), Model = t.Model, Adb = t.Adb, Remote = t.Remote, Cast = t.Cast, Last = t.Last })
                    .ToList();
        }
    }

    static Tv Mark(Dictionary<string, Tv> tvs, string ip) {
        lock (tvs) {
            Tv t;
            if (!tvs.TryGetValue(ip, out t)) { t = new Tv { Ip = ip }; tvs[ip] = t; }
            return t;
        }
    }

    // MARK: - Le réseau de ce PC

    static void LocalAddresses(List<string> own, List<string> prefixes) {
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
    }

    static bool InNetwork(string ip, List<string> prefixes) {
        return prefixes.Contains(ip.Substring(0, ip.LastIndexOf('.')));
    }

    static readonly int[] Ports = { 5555, 6466, 8008 };

    /** Frappe aux trois ports de chacune de `ips`, `ms` au plus (une TV répond en quelques ms) : ce qui répond est noté. */
    static void Knock(List<string> ips, Dictionary<string, Tv> tvs, int ms) {
        var clients = new List<TcpClient>();
        var tasks = new List<Task>();
        var asked = new List<KeyValuePair<string, int>>();
        foreach (string ip in ips) {
            foreach (int port in Ports) {
                var c = new TcpClient();
                try { tasks.Add(c.ConnectAsync(ip, port)); clients.Add(c); asked.Add(new KeyValuePair<string, int>(ip, port)); }
                catch (Exception) { c.Close(); }
            }
        }
        try { Task.WaitAll(tasks.ToArray(), ms); } catch (AggregateException) {}
        for (int i = 0; i < tasks.Count; i++) {
            if (tasks[i].Status != TaskStatus.RanToCompletion) continue;
            Tv t = Mark(tvs, asked[i].Key);
            lock (tvs) {
                if (asked[i].Value == 5555) t.Adb = true;
                else if (asked[i].Value == 6466) t.Remote = true;
                else t.Cast = true;
            }
        }
        foreach (var c in clients) { try { c.Close(); } catch (Exception) {} }
        foreach (var t in tasks) { var ignored = t.Exception; }   // marque les erreurs comme vues
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IpNetRow {
        public int Index, PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
        public uint Addr;
        public int Type;
    }

    [DllImport("iphlpapi.dll")] static extern int GetIpNetTable(IntPtr table, ref int size, bool sort);

    /** Les adresses IPv4 que Windows a vues récemment sur le réseau (« arp -a »), sans rien envoyer. */
    static List<string> Neighbors() {
        var list = new List<string>();
        try {
            int size = 0;
            GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return list;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try {
                if (GetIpNetTable(buffer, ref size, false) != 0) return list;
                int count = Marshal.ReadInt32(buffer), rowSize = Marshal.SizeOf(typeof(IpNetRow));
                for (int i = 0; i < count; i++) {
                    var row = (IpNetRow) Marshal.PtrToStructure(IntPtr.Add(buffer, 4 + i * rowSize), typeof(IpNetRow));
                    if (row.Type != 3 && row.Type != 4) continue;   // dynamique ou statique (pas « invalide »)
                    var ip = new IPAddress(row.Addr).GetAddressBytes();
                    if (ip[0] >= 224 || ip[3] == 0 || ip[3] == 255) continue;   // multidiffusion, réseau, diffusion
                    string text = string.Join(".", ip);
                    if (!list.Contains(text)) list.Add(text);
                }
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        } catch (Exception) {}   // pas Windows (essais), ou pas de table
        return list;
    }

    // MARK: - Ce que Google Cast dit de la TV (port 8008)

    /** Les descriptions des TV dont Google Cast répond (et pas encore lues), en même temps, 2,5 s au plus. */
    static void DescribeAll(Dictionary<string, Tv> tvs) {
        List<string> ips;
        lock (tvs) ips = tvs.Values.Where(t => t.Cast && (t.Adb || t.Remote) && t.Model.Length == 0 && t.Brand.Length == 0)
                .Select(t => t.Ip).Take(24).ToList();
        var reads = ips.Select(ip => Task.Factory.StartNew(() => Describe(ip, tvs))).ToArray();
        try { Task.WaitAll(reads, 2500); } catch (AggregateException) {}
    }

    /**
     * Sa description UPnP (celle que lisent les applis qui « castent ») : son nom, sa marque, son modèle ; sinon les
     * informations de Cast (eureka_info).
     */
    static void Describe(string ip, Dictionary<string, Tv> tvs) {
        string xml = Get("http://" + ip + ":8008/ssdp/device-desc.xml");
        string name = Tag(xml, "friendlyName"), maker = Tag(xml, "manufacturer"), model = Tag(xml, "modelName");
        if (name.Length == 0 || model.Length == 0) {
            string json = Get("http://" + ip + ":8008/setup/eureka_info?params=name,device_info");
            if (name.Length == 0) name = Json(json, "name");
            if (maker.Length == 0) maker = Json(json, "manufacturer");
            if (model.Length == 0) model = Json(json, "model_name");
        }
        Tv t = Mark(tvs, ip);
        lock (tvs) {
            if (name.Length > 0 && t.Name.Length == 0) t.Name = name;
            if (maker.Length > 0) t.Brand = maker;
            if (model.Length > 0) t.Model = model;
        }
    }

    /** Une page de la TV (64 Ko au plus) ; "" si elle ne répond pas. */
    static string Get(string url) {
        try {
            var request = (HttpWebRequest) WebRequest.Create(url);
            request.Timeout = 2000;
            request.ReadWriteTimeout = 2000;
            request.UserAgent = "FrontEndTV-Installateur";
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                char[] buffer = new char[65536];
                int n = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, n);
            }
        } catch (Exception) {
            return "";
        }
    }

    static string Tag(string xml, string tag) {
        Match m = Regex.Match(xml, "<(?:\\w+:)?" + tag + ">([^<]*)</", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }

    static string Json(string json, string key) {
        Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value).Trim() : "";
    }

    // MARK: - La marque

    static readonly string[] Brands = { "TCL", "Philips", "Sony", "Xiaomi", "Hisense", "Sharp", "Panasonic", "Toshiba",
            "Thomson", "Nokia", "Motorola", "Haier", "Skyworth", "Kiwi", "Metz", "Grundig", "Loewe", "Vestel", "Telefunken",
            "Continental Edison", "Polaroid", "Strong", "OK.", "JVC", "Hitachi", "Sanyo", "Bush", "Medion", "Changhong",
            "Konka", "KTC", "Formovie", "NVIDIA", "Google", "Chromecast", "Bouygues", "Freebox", "Orange", "SFR", "Mecool",
            "Homatics", "Walmart", "onn", "Realme", "OnePlus", "Infinix", "iFFALCON", "Element", "Westinghouse" };

    /** Sa marque, courte : celle de sa description UPnP (« TP Vision » → Philips), sinon lue dans son nom ou son modèle. */
    static string Brand(Tv t) {
        string text = t.Brand.Length > 0 ? t.Brand : t.Name + " " + t.Model;
        if (Regex.IsMatch(text, "TP ?Vision", RegexOptions.IgnoreCase)) return "Philips";
        if (Regex.IsMatch(text, "Chromecast", RegexOptions.IgnoreCase)) return "Google";
        foreach (string b in Brands) {
            if (Regex.IsMatch(text, @"(^|[^A-Za-z])" + Regex.Escape(b) + @"($|[^A-Za-z])", RegexOptions.IgnoreCase)) return b;
        }
        if (t.Brand.Length == 0) return "";
        string s = Regex.Replace(t.Brand, @"[,.]?\s*(Corporation|Corp\.?|Inc\.?|Co\.?,? ?Ltd\.?|Ltd\.?|Limited|GmbH|S\.?A\.?S?|Electronics)\b.*$", "",
                RegexOptions.IgnoreCase).Trim();
        return s.Length > 24 ? s.Substring(0, 24).Trim() : s;
    }
}
}
