// FrontEndTV — installateur : un client ADB intégré (la TV sur le port 5555), sans adb.exe ni téléchargement.
// Même protocole que app/src/io/freemytv/Adb.java, côté PC :
// https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/protocol.txt
// Écrit en C# 5 : il se compile avec le csc.exe livré avec Windows.

using System;
using System.IO;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FrontEndTV.Setup {

/** La clé de ce PC pour ADB (créée une fois, gardée dans le profil Windows) ; la TV la retient avec « Toujours autoriser ». */
sealed class AdbKey {
    readonly RSACryptoServiceProvider rsa;

    AdbKey(RSACryptoServiceProvider rsa) { this.rsa = rsa; }

    public static AdbKey LoadOrCreate(string file) {
        var rsa = new RSACryptoServiceProvider(2048);
        rsa.PersistKeyInCsp = false;
        try {
            if (File.Exists(file)) {
                rsa.FromXmlString(File.ReadAllText(file));
                if (rsa.KeySize == 2048) return new AdbKey(rsa);
            }
        } catch (Exception) {}
        rsa = new RSACryptoServiceProvider(2048);
        rsa.PersistKeyInCsp = false;
        File.WriteAllText(file, rsa.ToXmlString(true));
        return new AdbKey(rsa);
    }

    /** Signature PKCS#1 du jeton de 20 octets, que la TV vérifie comme un condensé SHA-1. */
    public byte[] Sign(byte[] token) {
        if (token.Length != 20) throw new IOException("jeton ADB inattendu");
        return rsa.SignHash(token, CryptoConfig.MapNameToOID("SHA1"));
    }

    /** La clé publique au format d'adbd (structure RSAPublicKey en base64, puis un nom). */
    public byte[] PublicBlob(string name) {
        RSAParameters p = rsa.ExportParameters(false);
        BigInteger n = Unsigned(p.Modulus), r32 = BigInteger.One << 32;
        BigInteger n0inv = r32 - ModInverse(n % r32, r32);
        BigInteger rr = BigInteger.ModPow(2, 4096, n);
        var blob = new MemoryStream();
        var w = new BinaryWriter(blob);   // petit-boutiste
        w.Write(64);
        w.Write((uint) n0inv);
        w.Write(LittleEndian(n, 256));
        w.Write(LittleEndian(rr, 256));
        w.Write((int) Unsigned(p.Exponent));
        w.Flush();
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(blob.ToArray()) + " " + name + "\0");
    }

    static BigInteger Unsigned(byte[] bigEndian) {
        var little = new byte[bigEndian.Length + 1];   // l'octet 0 en plus garde le nombre positif
        for (int i = 0; i < bigEndian.Length; i++) little[i] = bigEndian[bigEndian.Length - 1 - i];
        return new BigInteger(little);
    }

    static byte[] LittleEndian(BigInteger v, int size) {
        byte[] b = v.ToByteArray(), out_ = new byte[size];
        Array.Copy(b, out_, Math.Min(size, b.Length));
        return out_;
    }

    static BigInteger ModInverse(BigInteger a, BigInteger m) {
        BigInteger t = 0, newT = 1, r = m, newR = a;
        while (newR != 0) {
            BigInteger q = r / newR, tmp;
            tmp = t - q * newT; t = newT; newT = tmp;
            tmp = r - q * newR; r = newR; newR = tmp;
        }
        return t < 0 ? t + m : t;
    }
}

/** Une connexion ADB à la TV : commandes shell et installation d'un APK. */
sealed class AdbDevice : IDisposable {
    const uint CNXN = 0x4e584e43, AUTH = 0x48545541, OPEN = 0x4e45504f, OKAY = 0x59414b4f,
            CLSE = 0x45534c43, WRTE = 0x45545257, STLS = 0x534c5453;
    const uint Version = 0x01000001, MaxData = 256 * 1024;
    const uint AuthToken = 1, AuthSignature = 2, AuthPublicKey = 3;

    sealed class Packet {
        public uint Command, Arg0, Arg1;
        public byte[] Data;
    }

    readonly TcpClient tcp;
    readonly NetworkStream net;
    uint maxData = 4096, nextLocal = 1;

    AdbDevice(TcpClient tcp) {
        this.tcp = tcp;
        net = tcp.GetStream();
    }

    /**
     * Se connecte et s'authentifie. Si la TV ne connaît pas encore la clé, elle affiche « Autoriser le débogage
     * USB ? » : `waiting` est appelé, puis on attend la réponse jusqu'à `approvalMs`.
     */
    public static AdbDevice Connect(string host, int port, AdbKey key, string keyName, int approvalMs, Action waiting) {
        var tcp = new TcpClient();
        try {
            if (!tcp.ConnectAsync(host, port).Wait(5000)) throw new IOException("délai dépassé");
        } catch (Exception) {
            tcp.Close();
            throw new IOException("pas de réponse de " + host + " (port " + port + ")");
        }
        var d = new AdbDevice(tcp);
        try {
            tcp.NoDelay = true;
            d.net.ReadTimeout = 10000;
            d.net.WriteTimeout = 30000;
            d.Send(CNXN, Version, MaxData, Encoding.ASCII.GetBytes("host::\0"));
            bool signed = false, offered = false;
            while (true) {
                Packet m;
                try {
                    m = d.Read();
                } catch (IOException) {
                    throw new IOException(offered ? "la TV n'a pas autorisé ce PC" : "la TV ne répond pas en ADB");
                }
                if (m.Command == CNXN) {
                    d.maxData = Math.Max(1024, Math.Min(MaxData, m.Arg1));
                    d.net.ReadTimeout = 60000;
                    return d;
                }
                if (m.Command == STLS) throw new IOException("la TV demande le « débogage sans fil » (TLS) : active plutôt « Débogage USB »");
                if (m.Command != AUTH || m.Arg0 != AuthToken) throw new IOException("réponse ADB inattendue");
                if (!signed) {
                    d.Send(AUTH, AuthSignature, 0, key.Sign(m.Data));
                    signed = true;
                } else if (!offered) {
                    // Clé inconnue : on l'envoie, la TV affiche « Autoriser le débogage USB ? »
                    d.Send(AUTH, AuthPublicKey, 0, key.PublicBlob(keyName));
                    offered = true;
                    if (waiting != null) waiting();
                    d.net.ReadTimeout = approvalMs;
                } else {
                    throw new IOException("la TV a refusé ce PC");
                }
            }
        } catch (Exception) {
            d.Dispose();
            throw;
        }
    }

    /** Lance une commande dans le shell de la TV ; renvoie sa sortie (stdout et stderr). */
    public string Shell(string command) {
        return Run("shell:" + command, null, 0, null);
    }

    /** Installe (ou met à jour) un APK sans le copier d'abord : il passe directement à « cmd package install ». */
    public string Install(string apk, Action<int> percent) {
        long size = new FileInfo(apk).Length;
        using (var f = File.OpenRead(apk)) {
            int saved = net.ReadTimeout;
            net.ReadTimeout = 300000;   // Play Protect peut demander une confirmation sur la TV
            try { return Run("exec:cmd package install -r -S " + size, f, size, percent); }
            finally { net.ReadTimeout = saved; }
        }
    }

    /** Ouvre un service, lui envoie `input` (s'il y en a), et renvoie tout ce qu'il écrit jusqu'à sa fermeture. */
    string Run(string service, Stream input, long size, Action<int> percent) {
        uint local = nextLocal++, remote = 0;
        Send(OPEN, local, 0, Encoding.UTF8.GetBytes(service + "\0"));
        var output = new MemoryStream();
        long sent = 0;
        bool waitingAck = true;   // on attend le OKAY du OPEN, puis celui de chaque paquet envoyé
        var chunk = new byte[maxData];
        while (true) {
            if (!waitingAck && input != null && sent < size) {
                int n = input.Read(chunk, 0, (int) Math.Min(chunk.Length, size - sent));
                if (n <= 0) throw new IOException("lecture de l'APK impossible");
                var data = new byte[n];
                Array.Copy(chunk, data, n);
                Send(WRTE, local, remote, data);
                sent += n;
                waitingAck = true;
                if (percent != null) percent((int) (sent * 100 / size));
            }
            Packet m = Read();
            if (m.Arg1 != local) continue;
            if (m.Command == OKAY) {
                remote = m.Arg0;
                waitingAck = false;
            } else if (m.Command == WRTE) {
                output.Write(m.Data, 0, m.Data.Length);
                Send(OKAY, local, m.Arg0, null);
            } else if (m.Command == CLSE) {
                if (remote == 0) throw new IOException("la TV a refusé la commande");
                Send(CLSE, local, m.Arg0, null);
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }

    Packet Read() {
        byte[] h = ReadExactly(24);
        var m = new Packet();
        m.Command = BitConverter.ToUInt32(h, 0);
        m.Arg0 = BitConverter.ToUInt32(h, 4);
        m.Arg1 = BitConverter.ToUInt32(h, 8);
        uint length = BitConverter.ToUInt32(h, 12);
        if ((m.Command ^ 0xffffffff) != BitConverter.ToUInt32(h, 20)) throw new IOException("message ADB invalide");
        if (length > 1024 * 1024) throw new IOException("message ADB trop grand");
        m.Data = ReadExactly((int) length);
        return m;
    }

    byte[] ReadExactly(int n) {
        var b = new byte[n];
        int done = 0;
        while (done < n) {
            int r = net.Read(b, done, n - done);
            if (r <= 0) throw new IOException("connexion fermée par la TV");
            done += r;
        }
        return b;
    }

    void Send(uint command, uint arg0, uint arg1, byte[] data) {
        int length = data == null ? 0 : data.Length;
        uint sum = 0;
        if (data != null) foreach (byte x in data) sum += x;
        var b = new byte[24 + length];
        BitConverter.GetBytes(command).CopyTo(b, 0);
        BitConverter.GetBytes(arg0).CopyTo(b, 4);
        BitConverter.GetBytes(arg1).CopyTo(b, 8);
        BitConverter.GetBytes((uint) length).CopyTo(b, 12);
        BitConverter.GetBytes(sum).CopyTo(b, 16);
        BitConverter.GetBytes(command ^ 0xffffffff).CopyTo(b, 20);
        if (data != null) data.CopyTo(b, 24);
        net.Write(b, 0, b.Length);
        net.Flush();
    }

    public void Dispose() {
        try { tcp.Close(); } catch (Exception) {}
    }
}
}
