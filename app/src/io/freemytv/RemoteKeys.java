package io.freemytv;

import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.view.InputDevice;
import android.view.KeyEvent;

/**
 * The remote's app buttons on TCL TVs. The stock home screen opens the apps for Netflix, YouTube and Prime (and TCL
 * Channel); with it off, the keys still arrive, so we open the app the person picked (Settings > Remote Buttons).
 * Disney and Globoplay are opened by the system itself, so they are not listed. The YouTube button (BUTTON_3) never
 * reaches an app's window; only the accessibility service's key filter (RemoteKeyService) sees it.
 * On Vestel TVs and Formovie (Xiaomi) projectors only the Netflix button is ours: the system opens Netflix itself (a
 * global key, after the filter).
 * On KTC TVs (NextStar) only the Tabii button is ours, a global key too: the TV's hot-key receiver opens Tabii with it.
 * On Sony TVs, the app buttons (and the Google Play button of older remotes) are global keys for Sony's own receiver;
 * the filter sees them first, the same way.
 * On Xiaomi TVs whose remote has no Home button, its Mi button is BUTTON_4, a global key that Xiaomi's GlobalKeyReceiver
 * (partnercustomizer) turns into PatchWall. The filter sees it first, and by default it opens the home screen.
 * On Xiaomi TVs sold in Russia, the Okko, VK, Rutube, KION, Wink and IVI buttons are global keys too, for Xiaomi's
 * hot-key receiver (partnercustomizer's GlobalKeyReceiver).
 * On every other TV (Philips and the rest; not Fire TV), the buttons are found by themselves: when a button we don't
 * know is pressed and an app opens right after (the TV's own Netflix button opening Netflix), the button is noted with
 * that app (Prefs.noticeKey) and shows in Settings > Remote Buttons, where the person can make it open another app.
 * A button that opens nothing can be taught there (Learn a button, then a press on it). A button is known by its scan
 * code (the remote's own code, which tells apart buttons that share a key code), or its key code when it has none.
 */
final class RemoteKeys {
    private RemoteKeys() {}

    /** Some TCL remotes (BT_RC833) send TCL's own YouTube key instead of BUTTON_3: the same button. */
    private static final int KEY_YOUTUBE_TCL = 4065;

    /**
     * The key codes are the brand's own: on other brands they can be other buttons, so nothing here runs there.
     * Vestel builds TVs sold under other brands (F&U...), so BRAND varies: check the maker too.
     */
    static final boolean TCL = is("TCL"), VESTEL = is("Vestel"), KTC = is("KTC"), SONY = is("Sony"), FORMOVIE = is("Formovie");
    /**
     * Xiaomi TVs (models MiTV-M…; MiTV-MSSP3, MediaTek, Android TV 9 tested). Not the Mi Box or Mi TV Stick, the slowest
     * devices we run on, whose remotes have a Home button; Formovie's Xiaomi projectors have their own remote.
     */
    static final boolean XIAOMI = is("Xiaomi") && !FORMOVIE && Build.MODEL.startsWith("MiTV-M");
    /** Brands whose Netflix button is STEM_1, a global key (MediaTek): only that button is ours. */
    static final boolean STEM_NETFLIX = VESTEL || FORMOVIE;

    /** Brands whose app buttons FrontEndTV knows by their codes. */
    static final boolean KNOWN = TCL || STEM_NETFLIX || KTC || SONY || XIAOMI;
    /** Every other TV: its buttons are learned (not Fire TV, where Amazon keeps the keys). */
    static final boolean LEARNED = !KNOWN && !is("Amazon");
    /** A learned button with a scan code: SCAN_KEY + scan code. */
    static final int SCAN_KEY = 300_000;

    static boolean supported() {
        return KNOWN || LEARNED;
    }

    /** What a learned button is known by. */
    static int id(KeyEvent e) {
        return e.getScanCode() > 0 ? SCAN_KEY + e.getScanCode() : e.getKeyCode();
    }

    /** Buttons that can be learned: not the ones that move around, go back or home, or set the volume or the power. */
    static boolean learnable(KeyEvent e) {
        switch (e.getKeyCode()) {
            case KeyEvent.KEYCODE_DPAD_UP: case KeyEvent.KEYCODE_DPAD_DOWN: case KeyEvent.KEYCODE_DPAD_LEFT:
            case KeyEvent.KEYCODE_DPAD_RIGHT: case KeyEvent.KEYCODE_DPAD_CENTER: case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER: case KeyEvent.KEYCODE_BACK: case KeyEvent.KEYCODE_ESCAPE:
            case KeyEvent.KEYCODE_HOME: case KeyEvent.KEYCODE_MENU: case KeyEvent.KEYCODE_VOLUME_UP:
            case KeyEvent.KEYCODE_VOLUME_DOWN: case KeyEvent.KEYCODE_VOLUME_MUTE: case KeyEvent.KEYCODE_MUTE:
            case KeyEvent.KEYCODE_POWER: case KeyEvent.KEYCODE_TV_POWER: case KeyEvent.KEYCODE_SLEEP:
            case KeyEvent.KEYCODE_WAKEUP: case KeyEvent.KEYCODE_UNKNOWN:
                return false;
            default:
                return true;
        }
    }

    // Learning: the settings (process :home) leave a file while they wait for a press; the key service (process :keys,
    // which sees the buttons first, even those the TV keeps for itself, like Netflix's) writes the one it heard.

    private static java.io.File learnFile(Context c) { return new java.io.File(c.getFilesDir(), "remote-learn"); }
    private static java.io.File heardFile(Context c) { return new java.io.File(c.getFilesDir(), "remote-heard"); }

    static void startLearning(Context c) {
        //noinspection ResultOfMethodCallIgnored
        heardFile(c).delete();
        try (java.io.FileOutputStream out = new java.io.FileOutputStream(learnFile(c))) {
            out.write('1');
        } catch (java.io.IOException ignored) {
        }
    }

    static void stopLearning(Context c) {
        //noinspection ResultOfMethodCallIgnored
        learnFile(c).delete();
    }

    /** The button heard while learning (its id), or 0. */
    static int heard(Context c) {
        java.io.File f = heardFile(c);
        if (!f.exists()) return 0;
        try (java.io.BufferedReader r = new java.io.BufferedReader(new java.io.FileReader(f))) {
            return Integer.parseInt(r.readLine().trim());
        } catch (java.io.IOException | RuntimeException e) {
            return 0;
        } finally {
            //noinspection ResultOfMethodCallIgnored
            f.delete();
        }
    }

    /** Waiting for a press (for a minute at most, in case the settings were left without stopping). */
    private static boolean learning(Context c) {
        java.io.File f = learnFile(c);
        long age = System.currentTimeMillis() - f.lastModified();
        return f.exists() && age >= 0 && age < 60_000;
    }

    // MARK: - Buttons found by themselves (the TV opens an app right after a button)

    private static volatile int lastId;
    private static volatile long lastAt;

    /** A window of `pkg` opened: within 2.5 s of a button press, that button opens that app. */
    static void onWindow(Context c, CharSequence pkg) {
        int id = lastId;
        if (!LEARNED || id == 0 || pkg == null || android.os.SystemClock.uptimeMillis() - lastAt > 2500) return;
        lastId = 0;
        String p = pkg.toString();
        // Not this app, not the system's own windows (volume, notices), only apps that open from the home screen
        if (p.equals(c.getPackageName()) || p.equals("android") || p.startsWith("com.android.systemui")) return;
        if (launchIntent(c.getPackageManager(), p) == null) return;
        Prefs.noticeKey(id, p);
    }

    private static void hear(Context c, int id) {
        try (java.io.FileOutputStream out = new java.io.FileOutputStream(heardFile(c))) {
            out.write(String.valueOf(id).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        } catch (java.io.IOException ignored) {
        }
        stopLearning(c);
    }

    private static boolean is(String maker) {
        return maker.equalsIgnoreCase(Build.MANUFACTURER) || maker.equalsIgnoreCase(Build.BRAND);
    }

    /** Opens the app for this key on its first press. True if the key is ours and in use (it is then consumed). */
    static boolean onKey(Context c, KeyEvent e) {
        if (!supported()) return false;
        // BUTTON_3 is also a game controller's button: keys from a gamepad or a joystick are left alone.
        int source = e.getSource();
        if ((source & InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD
                || (source & InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK) return false;
        int keyCode = e.getKeyCode() == KEY_YOUTUBE_TCL ? Prefs.KEY_YOUTUBE : e.getKeyCode();
        if (LEARNED) {
            if (!learnable(e)) return false;   // the arrows, OK, Back... are never touched
            if (learning(c)) {
                if (e.getAction() == KeyEvent.ACTION_DOWN && e.getRepeatCount() == 0) hear(c, id(e));
                return true;   // heard for the settings, nothing else happens
            }
            keyCode = id(e);
        }
        // Sony sends BUTTON_9 for five app buttons (BUTTON_4 for Netflix, BUTTON_8 for Google Play): only the scan code
        // tells them apart.
        if (SONY && (keyCode == KeyEvent.KEYCODE_BUTTON_4 || keyCode == KeyEvent.KEYCODE_BUTTON_8
                || keyCode == KeyEvent.KEYCODE_BUTTON_9)) {
            keyCode = Prefs.SONY_KEY + e.getScanCode();
        }
        boolean ours = false;
        for (int key : Prefs.keys()) ours |= key == keyCode;
        String pkg = ours ? Prefs.keyApp(keyCode) : "";
        if (pkg.isEmpty()) {
            // Left to the TV: if an app opens right after, it's this button's (onWindow)
            if (LEARNED && e.getAction() == KeyEvent.ACTION_DOWN && e.getRepeatCount() == 0) {
                lastId = keyCode;
                lastAt = android.os.SystemClock.uptimeMillis();
            }
            return false;   // "Nothing": the key goes on as if we weren't here
        }
        if (e.getAction() == KeyEvent.ACTION_DOWN && e.getRepeatCount() == 0) {
            // Our own package means the home screen in use (Xiaomi's Mi button): ours, or the TV's own after the
            // watchdog handed it back. Not the settings app our launcher icon opens.
            Intent intent = pkg.equals(HomeMode.PKG)
                    ? new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
                    : launchIntent(c.getPackageManager(), pkg);
            AppLog.w("remote key " + e.getKeyCode() + " -> " + pkg + (intent == null ? " (not installed)" : ""));
            if (intent != null) c.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        }
        return true;
    }

    /** The app's TV launcher, or its plain one (kept here, not in HomeApps, so the key process stays small). */
    private static Intent launchIntent(PackageManager pm, String pkg) {
        Intent intent = pm.getLeanbackLaunchIntentForPackage(pkg);
        return intent != null ? intent : pm.getLaunchIntentForPackage(pkg);
    }
}
