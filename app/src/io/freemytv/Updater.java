package io.freemytv;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * FrontEndTV's own updates, from the latest release of its GitHub repository (Config.UPDATE_REPO, an APK attached to
 * the release): Settings → Update checks (also by itself every 6 hours), downloads, then installs it through the TV's
 * ADB, like the switch does, so Android shows no install screen. The new version must be signed with the same key and
 * have a higher versionCode; the update restarts the app (HomeReceiver then completes FrontEndTV mode).
 * State is only kept in the settings' process (:home), where it is shown.
 */
final class Updater {
    static final int IDLE = 0, CHECKING = 1, LATEST = 2, AVAILABLE = 3, DOWNLOADING = 4, INSTALLING = 5, FAILED = 6;
    private static final long STALE_MS = 6 * 3600_000L;

    private static volatile int state = IDLE, percent;
    private static volatile String newVersion = "", apkUrl = "", error = "";
    private static volatile boolean waitingApproval;

    private Updater() {}

    static int state() { return state; }

    /** Checking, downloading or installing (the screen redraws more often). */
    static boolean working() {
        int s = state;
        return s == CHECKING || s == DOWNLOADING || s == INSTALLING;
    }

    /** The Update row's value. */
    static String label() {
        switch (state) {
            case CHECKING: return Ui.tr("Checking…");
            case LATEST: return Ui.tr("Up to date");
            case AVAILABLE: return Ui.trf("Version {v} available", "v", newVersion);
            case DOWNLOADING: return Ui.trf("Downloading… {n} %", "n", percent);
            case INSTALLING: return waitingApproval ? Ui.tr("Accept “Allow USB debugging?” on the TV (tick “Always allow”).")
                    : Ui.tr("Installing…");
            case FAILED: return error;
            default: return Ui.tr("Check");
        }
    }

    /** OK on the Update row: installs the new version if there is one, else checks again. */
    static void activate(Context c) {
        if (working()) return;
        if (state == AVAILABLE) install(c.getApplicationContext()); else check(c.getApplicationContext());
    }

    /** When the settings open: checks if the last check is more than 6 hours old. */
    static void checkIfStale(Context c) {
        if (working() || state == AVAILABLE) return;
        long last = prefs(c).getLong("checkedAt", 0), now = System.currentTimeMillis();
        // A version seen earlier (the app restarted since): check again to get its file.
        boolean seen = !prefs(c).getString("available", "").isEmpty();
        if (seen || now < last || now - last > STALE_MS) check(c.getApplicationContext());
        else if (state == IDLE) state = LATEST;
    }

    private static void check(Context c) {
        state = CHECKING;
        new Thread(() -> {
            try {
                JSONObject release = new JSONObject(get("https://api.github.com/repos/" + Config.UPDATE_REPO.trim() + "/releases/latest"));
                String tag = release.optString("tag_name").replaceFirst("^[vV]", "").trim();
                String url = "";
                JSONArray assets = release.optJSONArray("assets");
                for (int i = 0; assets != null && i < assets.length(); i++) {
                    JSONObject a = assets.getJSONObject(i);
                    if (a.optString("name").toLowerCase(java.util.Locale.ROOT).endsWith(".apk")) {
                        url = a.optString("browser_download_url");
                        if (a.optString("name").equals("FrontEndTV.apk")) break;
                    }
                }
                prefs(c).edit().putLong("checkedAt", System.currentTimeMillis()).apply();
                if (!tag.isEmpty() && !url.isEmpty() && newer(tag, installedVersion(c))) {
                    newVersion = tag;
                    apkUrl = url;
                    prefs(c).edit().putString("available", tag).apply();
                    state = AVAILABLE;
                } else {
                    prefs(c).edit().putString("available", "").apply();
                    state = LATEST;
                }
            } catch (IOException | JSONException e) {
                fail(e.getMessage() != null && e.getMessage().startsWith("HTTP 404")
                        ? Ui.tr("No published version found (is the repository public?)")
                        : Ui.trf("Can't check: {error}", "error", String.valueOf(e.getMessage())));
            }
        }, "updater").start();
    }

    private static void install(Context c) {
        state = DOWNLOADING;
        percent = 0;
        new Thread(() -> {
            File apk = new File(c.getCacheDir(), "update.apk");
            try {
                download(apkUrl, apk);
                // The right app, and really newer: Android would refuse it later anyway, with a less clear message.
                PackageInfo info = c.getPackageManager().getPackageArchiveInfo(apk.getPath(), 0);
                PackageInfo mine = c.getPackageManager().getPackageInfo(c.getPackageName(), 0);
                if (info == null || !c.getPackageName().equals(info.packageName)) throw new IOException(Ui.tr("the file isn't FrontEndTV"));
                if (info.versionCode <= mine.versionCode) throw new IOException(Ui.tr("this version is already installed"));
                state = INSTALLING;
                AppLog.w("update: installing " + info.versionName);
                try (Adb adb = Adb.connect(c, 120_000, () -> waitingApproval = true)) {
                    waitingApproval = false;
                    // Android stops this app as it replaces it: when it works, this line is usually not reached.
                    String result = adb.install(apk, p -> {});
                    if (!result.contains("Success")) throw new IOException(result.trim());
                }
                state = LATEST;
            } catch (IOException | PackageManager.NameNotFoundException e) {
                AppLog.w("update failed: " + e.getMessage());
                fail(Ui.trf("Update failed: {error}", "error", String.valueOf(e.getMessage())));
            } finally {
                waitingApproval = false;
                //noinspection ResultOfMethodCallIgnored
                apk.delete();
            }
        }, "updater").start();
    }

    private static void fail(String message) {
        error = message;
        state = FAILED;
    }

    /** "1.10" is newer than "1.9"; missing parts count as 0. */
    static boolean newer(String candidate, String current) {
        String[] a = candidate.split("[^0-9]+"), b = current.split("[^0-9]+");
        for (int i = 0; i < Math.max(a.length, b.length); i++) {
            long x = part(a, i), y = part(b, i);
            if (x != y) return x > y;
        }
        return false;
    }

    private static long part(String[] parts, int i) {
        try { return i < parts.length && !parts[i].isEmpty() ? Long.parseLong(parts[i]) : 0; }
        catch (NumberFormatException e) { return 0; }
    }

    private static String installedVersion(Context c) {
        try {
            return c.getPackageManager().getPackageInfo(c.getPackageName(), 0).versionName;
        } catch (PackageManager.NameNotFoundException e) {
            return "0";
        }
    }

    private static SharedPreferences prefs(Context c) {
        return c.getSharedPreferences("updater", Context.MODE_PRIVATE);
    }

    private static HttpURLConnection open(String url) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        c.setConnectTimeout(10_000);
        c.setReadTimeout(30_000);
        c.setRequestProperty("User-Agent", "FrontEndTV");
        c.setRequestProperty("Accept", "application/vnd.github+json, application/octet-stream");
        c.setInstanceFollowRedirects(true);   // release files are served from another host
        return c;
    }

    private static String get(String url) throws IOException {
        HttpURLConnection c = open(url);
        try {
            if (c.getResponseCode() != 200) throw new IOException("HTTP " + c.getResponseCode());
            try (InputStream in = c.getInputStream()) {
                ByteArrayOutputStream out = new ByteArrayOutputStream();
                byte[] buffer = new byte[8192];
                int n;
                while ((n = in.read(buffer)) > 0) out.write(buffer, 0, n);
                return out.toString(StandardCharsets.UTF_8.name());
            }
        } finally {
            c.disconnect();
        }
    }

    private static void download(String url, File to) throws IOException {
        HttpURLConnection c = open(url);
        try {
            if (c.getResponseCode() != 200) throw new IOException("HTTP " + c.getResponseCode());
            long total = c.getContentLengthLong(), done = 0;
            try (InputStream in = c.getInputStream(); FileOutputStream out = new FileOutputStream(to)) {
                byte[] buffer = new byte[65536];
                int n;
                while ((n = in.read(buffer)) > 0) {
                    out.write(buffer, 0, n);
                    done += n;
                    if (total > 0) percent = (int) (done * 100 / total);
                }
            }
        } finally {
            c.disconnect();
        }
    }
}
