package io.freemytv;

import android.content.Context;
import android.os.Build;
import android.os.SystemClock;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Settings > Wi-Fi: the network the TV is on and the ones around it, read through the TV's own ADB (the one the switch
 * uses): Android gives an app neither the networks' names (without asking for the location) nor a way to join one.
 * Joining uses `cmd wifi connect-network` (Android 11 and later); elsewhere, and for enterprise or WEP networks, the
 * TV's own Wi-Fi settings. The password only goes to that command, never to the log. State is kept in the settings'
 * process (:home), where it is shown.
 */
final class Wifi {
    private Wifi() {}

    static final class Network {
        final String ssid, security;   // security: open, owe, wpa2, wpa3, wep or eap
        final int rssi;

        Network(String ssid, int rssi, String security) {
            this.ssid = ssid;
            this.rssi = rssi;
            this.security = security;
        }

        boolean open() { return security.equals("open") || security.equals("owe"); }

        /** Joined from here: the TV's command knows open, OWE, WPA2 and WPA3 networks. */
        boolean joinable() { return JOINABLE && !security.equals("eap") && !security.equals("wep"); }
    }

    /** `cmd wifi connect-network`, `start-scan` and `list-scan-results` came with Android 11. */
    static final boolean JOINABLE = Build.VERSION.SDK_INT >= 30;

    static volatile boolean enabled = true, scanning, connecting, read;
    /** The network the TV is on ("" when none), its signal (0 when unknown), and the last thing worth saying. */
    static volatile String current = "", message = "";
    static volatile int currentRssi;
    static volatile List<Network> networks = Collections.emptyList();
    private static volatile long readAt;

    /** On the Wi-Fi page, every 20 s: what the TV is on, and the networks around. */
    static void refreshIfStale(Context c) {
        if (SystemClock.elapsedRealtime() - readAt > 20_000) refresh(c);
    }

    static synchronized void refresh(Context c) {
        if (scanning || connecting || !Capabilities.adb(c)) return;
        scanning = true;
        readAt = SystemClock.elapsedRealtime();
        Context app = c.getApplicationContext();
        new Thread(() -> {
            try (Adb adb = Adb.connect(app, 10_000, null)) {
                readStatus(adb);
                if (enabled) {
                    String found;
                    if (JOINABLE) {
                        adb.shell("cmd wifi start-scan");
                        SystemClock.sleep(3000);
                        found = adb.shell("cmd wifi list-scan-results");
                    } else {
                        found = scanSection(adb.shell("dumpsys wifi"));
                    }
                    networks = parse(found);
                } else {
                    networks = Collections.emptyList();
                }
                if (!connecting) message = "";
                read = true;
            } catch (IOException e) {
                message = Ui.trf("Can't read the networks: {error}", "error", String.valueOf(e.getMessage()));
            } finally {
                scanning = false;
                readAt = SystemClock.elapsedRealtime();
            }
        }, "wifi").start();
    }

    /** "Wi-Fi is off": turns it on (svc works on every version), then reads again. */
    static void enable(Context c) {
        if (scanning || connecting) return;
        Context app = c.getApplicationContext();
        connecting = true;
        message = Ui.tr("Turning Wi-Fi on…");
        new Thread(() -> {
            try (Adb adb = Adb.connect(app, 10_000, null)) {
                adb.shell("svc wifi enable");
                SystemClock.sleep(3000);
                readStatus(adb);
                message = "";
            } catch (IOException e) {
                message = Ui.trf("Couldn't turn Wi-Fi on: {error}", "error", String.valueOf(e.getMessage()));
            } finally {
                connecting = false;
                readAt = 0;   // the networks, now
            }
        }, "wifi").start();
    }

    /** Joins `n` (with `password`, unused for an open network), then waits up to 20 s to see the TV on it. */
    static void connect(Context c, Network n, String password) {
        if (connecting) return;
        Context app = c.getApplicationContext();
        connecting = true;
        message = Ui.trf("Connecting to {name}…", "name", n.ssid);
        AppLog.w("Wi-Fi: joining a " + n.security + " network");
        new Thread(() -> {
            try (Adb adb = Adb.connect(app, 10_000, null)) {
                String out = adb.shell("cmd wifi connect-network " + quote(n.ssid) + " " + n.security
                        + (n.open() ? "" : " " + quote(password)));
                String lower = out.toLowerCase(java.util.Locale.ROOT);
                if (lower.contains("exception") || lower.contains("error") || lower.contains("invalid") || lower.contains("unknown")) {
                    AppLog.w("Wi-Fi: " + out.trim());
                    throw new IOException(out.trim());
                }
                long until = SystemClock.elapsedRealtime() + 20_000;
                while (SystemClock.elapsedRealtime() < until) {
                    SystemClock.sleep(2000);
                    readStatus(adb);
                    if (n.ssid.equals(current)) break;
                }
                message = n.ssid.equals(current) ? Ui.trf("Connected to {name}.", "name", n.ssid)
                        : Ui.trf("Couldn't connect to {name}: check the password.", "name", n.ssid);
            } catch (IOException e) {
                message = Ui.trf("Couldn't connect: {error}", "error", String.valueOf(e.getMessage()));
            } finally {
                connecting = false;
                readAt = 0;   // the list again (the network now on top)
            }
        }, "wifi").start();
    }

    /** One argument for the TV's shell: in single quotes, the quotes inside closed and reopened around \'. */
    private static String quote(String s) {
        return "'" + s.replace("'", "'\\''") + "'";
    }

    private static final Pattern SSID = Pattern.compile("SSID: \"?([^\",]*)\"?,"), RSSI = Pattern.compile("RSSI: (-?\\d+)");

    /** Whether Wi-Fi is on, the network the TV is on, and its signal. */
    private static void readStatus(Adb adb) throws IOException {
        String s = JOINABLE ? adb.shell("cmd wifi status") : adb.shell("dumpsys wifi | grep -m 2 -E 'Wi-Fi is|mWifiInfo'");
        enabled = !s.contains("Wifi is disabled") && !s.contains("Wi-Fi is disabled");
        Matcher m = SSID.matcher(s);
        String ssid = m.find() ? m.group(1).trim() : "";
        current = !enabled || ssid.equals("<unknown ssid>") ? "" : ssid;
        Matcher r = RSSI.matcher(s);
        currentRssi = !current.isEmpty() && r.find() ? Integer.parseInt(r.group(1)) : 0;
    }

    /** Before Android 11, the scan results are in `dumpsys wifi`, under "Latest scan results:". */
    private static String scanSection(String dump) {
        int from = dump.indexOf("Latest scan results:");
        if (from < 0) return "";
        int to = dump.indexOf("\n\n", from);
        return dump.substring(from, to < 0 ? dump.length() : to);
    }

    /** BSSID, frequency, RSSI(…), age, SSID (may hold spaces, or be empty when hidden), [flags]. */
    private static final Pattern ROW = Pattern.compile(
            "^\\s*[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5}\\s+\\d+\\s+(-?\\d+)\\S*\\s+\\S+\\s+(.*?)\\s*((?:\\[[^\\]]*\\])*)\\s*$",
            Pattern.MULTILINE);

    /** The networks, once each (its strongest access point), the TV's own first, then by signal. */
    static List<Network> parse(String out) {
        Map<String, Network> best = new LinkedHashMap<>();
        Matcher m = ROW.matcher(out);
        while (m.find()) {
            String ssid = m.group(2).trim();
            if (ssid.isEmpty() || ssid.equals("<unknown ssid>")) continue;
            int rssi = Integer.parseInt(m.group(1));
            Network old = best.get(ssid);
            if (old == null || rssi > old.rssi) best.put(ssid, new Network(ssid, rssi, security(m.group(3))));
        }
        List<Network> list = new ArrayList<>(best.values());
        String on = current;
        Collections.sort(list, (a, b) -> a.ssid.equals(on) ? -1 : b.ssid.equals(on) ? 1 : Integer.compare(b.rssi, a.rssi));
        return list;
    }

    private static String security(String flags) {
        if (flags.contains("EAP")) return "eap";
        if (flags.contains("PSK")) return "wpa2";   // WPA3 transition networks take a WPA2 key too
        if (flags.contains("SAE")) return "wpa3";
        if (flags.contains("WEP")) return "wep";
        if (flags.contains("OWE")) return "owe";
        return "open";
    }

    /** Bars out of 4, as the TV's own settings count them. */
    static int level(int rssi) {
        if (rssi == 0) return 4;   // unknown: full
        return rssi >= -55 ? 4 : rssi >= -66 ? 3 : rssi >= -77 ? 2 : rssi >= -88 ? 1 : 0;
    }
}
