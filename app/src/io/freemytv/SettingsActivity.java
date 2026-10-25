package io.freemytv;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.BlurMaskFilter;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Rect;
import android.graphics.RectF;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.provider.Settings;
import android.text.TextPaint;
import android.text.TextUtils;
import android.view.Choreographer;
import android.view.KeyEvent;
import android.view.View;
import android.view.accessibility.AccessibilityNodeProvider;
import android.view.inputmethod.EditorInfo;
import android.widget.EditText;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * FrontEndTV's settings (the app's icon, "All Apps", or the gear on the home screen): a sidebar with every page on the
 * left, the page on the right (the settings as cards, or a list with the focused row framed in violet). "Home screen"
 * opens the FrontEndTV / Original choice (with a check on the current one); the switch runs in the main process
 * (HomeMode) and this screen only asks for it and follows the state file.
 */
public class SettingsActivity extends Activity {
    private SettingsView view;
    private DreamView preview;   // "Screensaver": the screensaver right here, without changing the system setting

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (openFireHome(getIntent())) return;
        Watchdog.install(this);
        Ui.init(this);
        SleepTimer.listen(this);   // :home may start here, from the launcher icon
        SleepTimer.check(this);
        installerValues(getIntent());
        view = new SettingsView(this);
        setContentView(view);
    }

    /**
     * Values sent by the installer: adb shell am start -n io.freemytv/.SettingsActivity --es tmdb_key KEY
     * --es tmdb_region FR --es user_name Florian --es switch free (each optional).
     */
    private void installerValues(Intent intent) {
        if (intent == null) return;
        String key = intent.getStringExtra("tmdb_key"), region = intent.getStringExtra("tmdb_region"),
                name = intent.getStringExtra("user_name"), mode = intent.getStringExtra("switch");
        if (key != null || region != null || name != null) {
            Prefs.setInstallerValues(key, region, name);
            try { Recs.get(this).refreshIfStale(); } catch (RuntimeException ignored) {}
        }
        // The installer can also switch the home screen (--es switch free | original): the same as picking it here.
        if ("free".equals(mode)) HomeMode.request(this, HomeMode.FREE);
        else if ("original".equals(mode)) HomeMode.request(this, HomeMode.ORIGINAL);
    }

    /**
     * Fire TV: Amazon keeps the Home button, so the app's icon opens the home screen (FireHomeActivity) directly;
     * the gear on the home screen opens these settings (no launcher category).
     */
    private boolean openFireHome(Intent intent) {
        if (!HomeMode.AMAZON || intent == null || !(intent.hasCategory(Intent.CATEGORY_LEANBACK_LAUNCHER)
                || intent.hasCategory(Intent.CATEGORY_LAUNCHER)) || intent.getBooleanExtra("screensaver", false)) {
            return false;
        }
        startActivity(new Intent(this, FireHomeActivity.class));
        finish();
        return true;
    }

    @Override protected void onResume() {
        super.onResume();
        if (view == null) return;
        view.resume();
        // From ADB: am start -n io.freemytv/.SettingsActivity --ez screensaver true (straight to the preview).
        if (preview == null && getIntent().getBooleanExtra("screensaver", false)) {
            getIntent().removeExtra("screensaver");
            view.pause();
            showPreview();
        }
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        installerValues(intent);
        if (openFireHome(intent)) return;
        setIntent(intent);
    }

    @Override protected void onPause() {
        closePreview();     // before pausing the view: closing the preview resumes it (view.resume)
        if (view != null) view.pause();
        super.onPause();
    }

    @Override public boolean dispatchKeyEvent(KeyEvent event) {
        if (preview != null) {
            // Like the real screensaver: arrows, OK or Back close it (other keys go on to the system).
            switch (event.getKeyCode()) {
                case KeyEvent.KEYCODE_DPAD_UP:
                case KeyEvent.KEYCODE_DPAD_DOWN:
                case KeyEvent.KEYCODE_DPAD_LEFT:
                case KeyEvent.KEYCODE_DPAD_RIGHT:
                case KeyEvent.KEYCODE_DPAD_CENTER:
                case KeyEvent.KEYCODE_ENTER:
                case KeyEvent.KEYCODE_BACK:
                    if (event.getAction() == KeyEvent.ACTION_UP) closePreview();
                    return true;
                default:
                    return super.dispatchKeyEvent(event);
            }
        }
        if (view.onKey(event)) return true;
        return super.dispatchKeyEvent(event);
    }

    void showPreview() {
        preview = new DreamView(this);
        setContentView(preview);
        preview.start();
    }

    private void closePreview() {
        if (preview == null) return;
        preview.stop();
        preview = null;
        setContentView(view);
        view.resume();
    }

    static final class SettingsView extends View implements Choreographer.FrameCallback {
        private static final int MAIN = 0, HOME_CHOICE = 1, HOME_SETTINGS = 2, REMOTE_KEYS = 3, PICKER = 4, SHELF_EDIT = 5,
                POWER = 6;
        private static final float LIST_ROW_H = 68, LIST_ROW_GAP = 6;
        private static final int PICK_VISIBLE = 9;
        // The layout: a sidebar on the left (logo, version, every page, the one open highlighted) and the page on the
        // right. The main page ("Home") shows the nine settings as cards on two columns, in three groups, each with
        // what it's for, and an "About" card (the picks' status and TMDB's credit). What's going on (a switch, an
        // error, a new version) shows in the card it concerns.
        private static final float SIDE_W = 480, CONTENT_L = 532, CONTENT_R = 1864, COL_GAP = 24, CARD_H = 104;
        private static final int UPDATE_ROW = 8;
        /** The other pages: a list in the page's place. */
        private static final float LIST_X = CONTENT_L, LIST_R = CONTENT_R, LIST_TOP = 186, ROW_GAP = 8, CHOICE_H = 104;
        /** The cards, row by row (two columns; a row's only card takes both). */
        private static final int[][] GRID = {{0, 1}, {2, 3}, {4}, {5, 6}, {7, 8}};
        private static final int AMBER = 0xFFFFC266, INK = 0xFFFFFFFF;

        private final SettingsActivity activity;
        private final Scene scene = new Scene();
        private final Handler handler = new Handler(Looper.getMainLooper());
        private final Spring[] pop = new Spring[12];
        private final List<String> appPkgs = new ArrayList<>(), appLabels = new ArrayList<>();
        // The list picker (the app of a remote button): what it shows, which one is in use, where we are.
        private final List<String> pickLabels = new ArrayList<>();
        // The shelf editor (also the top buttons' one, with editingTop): what it can hold (selected ones first, in
        // order) and the selection.
        private final List<String[]> shelfRows = new ArrayList<>();
        private final List<String> shelfOrder = new ArrayList<>();
        private boolean editingTop;
        private int pickFor, pickCurrent, pickScroll;   // pickFor: the remote button (index + 1)
        private static final int PICK_CITY = 0, PICK_LANGUAGE = -1, PICK_SLEEP = -2, PICK_PLACE = -3;
        /** Sleep Timer's pickers (PICK_SLEEP and below): their choices as {value, name}, and the value now. */
        private final List<String[]> choices = new ArrayList<>();
        private String choiceNow = "";
        private final List<String[]> cities = new ArrayList<>();
        private String cityStatus = "";
        private final RectF tmp = new RectF(), rowRect = new RectF(), focusBox = new RectF();
        private JSONObject state = new JSONObject(), recs = new JSONObject();
        private boolean hasKey;
        private int screen = MAIN, focus;
        private boolean running, okDown, backDown, wasBusy;
        private Toast whyToast;   // why the focused option is unavailable
        private String[] whyRows;  // each row's reason, worked out once per screen (null: again on the next frame)
        private int whyScreen = -1, whyScroll = -1;
        private int sleepLeft;     // the sleep timer's minutes left, read with the state, not on every frame
        private int whyState = -1;
        private final Spoken spoken;
        private long startedAt, lastNanos, pendingAt, lastSky;
        private int w, h;
        private float k = 1;
        private Paint rowLabel, rowValue, rowSub, header, cellLabel, cellValue, warn, spinner, white, shadow;
        private Paint pageTitle, pageSub, cardTitle, cardDesc, cardValue, infoPaint, infoSmall, creditPaint, sideBrand,
                sideVersion, sideHeader, sideItem, cornerTime, cornerDate;
        private final Paint side = new Paint(), wifi = new Paint(Paint.ANTI_ALIAS_FLAG);

        private final Runnable poll = new Runnable() {
            @Override public void run() {
                if (!running) return;
                state = HomeMode.state(getContext());
                if (learning) {
                    int heard = RemoteKeys.heard(getContext());
                    if (heard != 0) learned(heard);
                    else if (SystemClock.uptimeMillis() - learnAt > 45_000) stopLearning();
                }
                sleepLeft = SleepTimer.minutesLeft(getContext());
                readNetwork();
                hasKey = Recs.hasKey(getContext());
                recs = Recs.status(getContext());
                boolean nowBusy = !busyText().isEmpty();
                if (nowBusy) pendingAt = 0;
                // What can change the reasons away from a key: a switch (or the watchdog's revert) and the overlay's check.
                int whyNow = (freeActive() ? 1 : 0) | (Capabilities.refused(getContext(), Capabilities.OVERLAY) ? 2 : 0);
                if (whyNow != whyState) whyRows = null;
                whyState = whyNow;
                // A switch just ended: TalkBack reads how it went (the newest notice).
                JSONArray notices = state.optJSONArray("notices");
                if (wasBusy && !nowBusy && notices != null && notices.length() > 0) {
                    JSONObject newest = notices.optJSONObject(0);
                    if (newest != null) announceForAccessibility(newest.optString("text"));
                }
                wasBusy = nowBusy;
                invalidate();
                handler.postDelayed(this, learning ? 250 : busy() || Updater.working() ? 500 : 2000);
            }
        };

        SettingsView(SettingsActivity a) {
            super(a);
            activity = a;
            for (int i = 0; i < pop.length; i++) pop[i] = new Spring(0.28f, 0.85f, 0);
            HomeData.Weather wx = HomeData.get(a).weather;
            Shaders.sky(scene.palette, System.currentTimeMillis(), wx.sunrise, wx.sunset);
            scene.setMotion(0.5f, 0.2f);
            scene.applyPalette();
            setFocusable(true);
            setContentDescription("FrontEndTV");
            // TalkBack: one item per row, id = row + 1.
            spoken = new Spoken(this, new Spoken.Source() {
                @Override public void items(List<Integer> out) { for (int i = 1; i <= rowCount(); i++) out.add(i); }
                @Override public String text(int id) { return id >= 1 && id <= rowCount() ? rowText(id - 1) : ""; }
                @Override public boolean bounds(int id, Rect out) {
                    if (id < 1 || id > rowCount()) return false;
                    rowBox(id - 1).round(out);
                    return true;
                }
                @Override public int focused() { return focus + 1; }
                @Override public void focus(int id) {
                    focus = id - 1;
                    invalidate();
                }
                @Override public void click(int id) { activate(); }
            });
        }

        @Override public AccessibilityNodeProvider getAccessibilityNodeProvider() {
            return spoken;
        }

        /** A row as TalkBack reads it: its name and its value, as on screen, and why where it's unavailable. */
        private String rowText(int row) {
            String why = unavailable(row);
            return why == null ? rowValueText(row) : rowValueText(row) + ", " + Ui.tr("Unavailable") + ". " + why;
        }

        private String rowValueText(int row) {
            switch (screen) {
                case MAIN: {
                    if (row == 0) {
                        String busyText = busyText();
                        String value = busy() ? (busyText.isEmpty() ? Ui.tr("Starting…") : busyText)
                                : freeActive() ? "FrontEndTV" : Ui.tr("Original");
                        return Ui.trf("Home Screen, {mode}", "mode", value);
                    }
                    if (row == UPDATE_ROW) return Ui.tr("Update") + ", " + Updater.label();
                    String[] names = {Ui.tr("Screensaver, preview"), Ui.tr("Home Settings"),
                            Ui.tr("Shelf"), Ui.tr("Top Buttons"), Ui.tr("Remote Buttons"), Ui.tr("Sleep Timer"),
                            Ui.tr("TV Settings")};
                    return row - 1 < names.length ? names[row - 1] : "";
                }
                case HOME_CHOICE: {
                    String name = row == 0 ? "FrontEndTV, " + Ui.tr("no ads, with the new screensaver")
                            : "Original, " + Ui.tr("the TV's factory home screen");
                    boolean inUse = (row == 0) == freeActive();
                    return name + (inUse ? Ui.tr(". In use") : "") + ", " + Ui.trf("{i} of {n}", "i", row + 1, "n", 2);
                }
                case HOME_SETTINGS:
                case REMOTE_KEYS:
                case POWER: {
                    boolean keys = screen == REMOTE_KEYS;
                    String[] labels = listLabels(), values = listValues();
                    int at = keys ? row : pickScroll + row;
                    return at < labels.length ? labels[at] + ", " + values[at] : "";
                }
                case PICKER: {
                    int at = pickScroll + row;
                    if (at >= pickLabels.size()) return "";
                    return pickLabels.get(at) + (at == pickCurrent ? Ui.tr(", selected") : "")
                            + ", " + Ui.trf("{i} of {n}", "i", at + 1, "n", pickLabels.size());
                }
                case SHELF_EDIT: {
                    int at = pickScroll + row;
                    if (at >= shelfRows.size()) return "";
                    String[] app = shelfRows.get(at);
                    int place = shelfOrder.indexOf(app[0]);
                    if (editingTop) {
                        return app[1] + ", " + (place >= 0 ? Ui.trf("in the top buttons, place {n}", "n", place + 1)
                                : Ui.tr("not in the top buttons"));
                    }
                    return app[1] + ", " + (place >= 0 ? Ui.trf("on the shelf, place {n}", "n", place + 1)
                            : Ui.tr("not on the shelf"));
                }
                default:
                    return "";
            }
        }

        private RectF rowBox(int row) {
            float top, height;
            if (screen == MAIN) {
                focusBox.set(cardRect(row));
                return focusBox;
            } else if (screen == HOME_CHOICE) {
                top = LIST_TOP + 20 + row * (CHOICE_H + ROW_GAP);
                height = CHOICE_H;
            } else {
                top = LIST_TOP + 20 + row * (LIST_ROW_H + LIST_ROW_GAP);
                height = LIST_ROW_H;
            }
            focusBox.set(LIST_X * k, top * k, LIST_R * k, (top + height) * k);
            return focusBox;
        }

        void resume() {
            if (running) return;
            running = true;
            whyRows = null;
            startedAt = SystemClock.uptimeMillis();
            lastNanos = 0;
            handler.post(poll);
            Choreographer.getInstance().postFrameCallback(this);
            spoken.readAgain();
            Photos.importInbox(getContext(), this::invalidate);   // photos a computer just sent
            Updater.checkIfStale(getContext());   // a new version shows on the Update row
        }

        void pause() {
            if (learning) stopLearning();
            running = false;
            handler.removeCallbacks(poll);
            Choreographer.getInstance().removeFrameCallback(this);
        }

        @Override public void doFrame(long frameTimeNanos) {
            if (!running) return;
            boolean moving = busy() || Updater.working();
            for (Spring s : pop) moving |= !s.settled();
            if (moving) invalidate();
            Choreographer.getInstance().postFrameCallback(this);
        }

        @Override protected void onSizeChanged(int width, int height, int oldWidth, int oldHeight) {
            w = width;
            h = height;
            k = w / 1920f;
            scene.setSize(w, h);
            backdrop = null;
            Symbols.warm(getContext(), "sparkles", 150 * k);
            Symbols.warm(getContext(), "chevron_right", 22 * k);
            Symbols.warm(getContext(), "check", 26 * k);
            rowLabel = Ui.text(Ui.medium, 30 * k, 0xF2FFFFFF);
            rowValue = Ui.text(Ui.regular, 28 * k, 0x8CFFFFFF);
            rowValue.setTextAlign(Paint.Align.RIGHT);
            rowSub = Ui.text(Ui.regular, 23 * k, 0x8CFFFFFF);
            cellLabel = Ui.text(Ui.regular, 25 * k, 0xD9FFFFFF);
            cellValue = Ui.numbers(Ui.regular, 25 * k, 0xD9FFFFFF);
            cellValue.setTextAlign(Paint.Align.RIGHT);
            warn = Ui.text(Ui.regular, 23 * k, AMBER);
            spinner = new Paint(Paint.ANTI_ALIAS_FLAG);
            spinner.setStyle(Paint.Style.STROKE);
            spinner.setStrokeCap(Paint.Cap.ROUND);
            spinner.setStrokeWidth(3.5f * k);
            white = new Paint(Paint.ANTI_ALIAS_FLAG);
            white.setColor(0xFFF5F5F7);
            shadow = new Paint(Paint.ANTI_ALIAS_FLAG);
            shadow.setMaskFilter(new BlurMaskFilter(22 * k, BlurMaskFilter.Blur.NORMAL));
            pageTitle = Ui.text(Ui.displaySemibold, 56 * k, 0xFFFFFFFF);
            pageSub = Ui.text(Ui.regular, 26 * k, 0xFF9AA3B5);
            cardTitle = Ui.text(Ui.semibold, 28 * k, 0xFFFFFFFF);
            cardDesc = Ui.text(Ui.regular, 22 * k, 0xFF9AA3B5);
            cardValue = Ui.text(Ui.medium, 24 * k, 0xFFB9A6FF);
            cardValue.setTextAlign(Paint.Align.RIGHT);
            infoPaint = Ui.text(Ui.semibold, 26 * k, 0xFF141B29);
            infoPaint.setTextAlign(Paint.Align.CENTER);
            infoSmall = Ui.text(Ui.semibold, 20 * k, 0xFF0B101A);
            infoSmall.setTextAlign(Paint.Align.CENTER);
            creditPaint = Ui.text(Ui.regular, 19 * k, 0xFF717A8C);
            sideBrand = Ui.text(Ui.displaySemibold, 34 * k, 0xFFFFFFFF);
            sideVersion = Ui.text(Ui.regular, 22 * k, 0xFF9AA3B5);
            sideHeader = Ui.text(Ui.semibold, 19 * k, 0xFF8A93A6);
            sideHeader.setLetterSpacing(0.08f);
            sideItem = Ui.text(Ui.medium, 27 * k, 0xFFC9CFDB);
            cornerTime = Ui.numbers(Ui.semibold, 34 * k, 0xFFFFFFFF);
            cornerTime.setTextAlign(Paint.Align.RIGHT);
            cornerDate = Ui.text(Ui.regular, 20 * k, 0xFF9AA3B5);
            cornerDate.setTextAlign(Paint.Align.RIGHT);
            header = Ui.text(Ui.semibold, 20 * k, 0xFF8A93A6);
            header.setLetterSpacing(0.08f);
        }

        private boolean busy() {
            return !busyText().isEmpty() || (pendingAt != 0 && SystemClock.uptimeMillis() - pendingAt < 10_000);
        }

        private String busyText() {
            long at = state.optLong("busyAt"), now = SystemClock.elapsedRealtime();
            boolean stale = now < at || now - at > 10 * 60_000L;
            return stale ? "" : state.optString("busy");
        }

        private boolean freeActive() {
            return HomeMode.FREE.equals(state.optString("mode")) && !state.optBoolean("homeReverted");
        }

        private int rowCount() {
            switch (screen) {
                case MAIN: return 9;
                case HOME_CHOICE: return 2;
                case HOME_SETTINGS: return Math.min(SETTINGS_VISIBLE, settingLabels().length);
                case PICKER: return Math.min(PICK_VISIBLE, pickLabels.size());
                case SHELF_EDIT: return Math.min(PICK_VISIBLE, shelfRows.size());
                case POWER: return powerRows().length;
                default: return Prefs.keys().length + (RemoteKeys.LEARNED ? 1 : 0);   // + "Learn a button"
            }
        }

        private android.graphics.Bitmap brandIcon;
        private final android.graphics.RectF brandRect = new android.graphics.RectF();
        private final android.graphics.Paint brandPaint = new android.graphics.Paint(android.graphics.Paint.FILTER_BITMAP_FLAG | android.graphics.Paint.ANTI_ALIAS_FLAG);

        // MARK: - FrontEndTV look: deep navy, dark cards, violet focus

        private android.graphics.Bitmap backdrop;
        private final android.graphics.RectF screenRect = new android.graphics.RectF();
        private final Paint backdropPaint = new Paint(Paint.FILTER_BITMAP_FLAG | Paint.DITHER_FLAG);
        private final Paint pill = new Paint(Paint.ANTI_ALIAS_FLAG), card = new Paint(Paint.ANTI_ALIAS_FLAG),
                cardEdge = new Paint(Paint.ANTI_ALIAS_FLAG);

        /** The background (deep navy), drawn once at a quarter of the screen size and scaled up (it is all soft gradients). */
        private android.graphics.Bitmap makeBackdrop() {
            int bw = Math.max(16, w / 4), bh = Math.max(9, h / 4);
            android.graphics.Bitmap b = android.graphics.Bitmap.createBitmap(bw, bh, android.graphics.Bitmap.Config.ARGB_8888);
            Canvas bc = new Canvas(b);
            Paint p = new Paint(Paint.ANTI_ALIAS_FLAG | Paint.DITHER_FLAG);
            p.setShader(new android.graphics.LinearGradient(0, 0, bw, bh, 0xFF0F1524, 0xFF080B13, android.graphics.Shader.TileMode.CLAMP));
            bc.drawRect(0, 0, bw, bh, p);
            // Top right, behind the page: a faint violet light.
            p.setShader(new android.graphics.RadialGradient(bw * 0.72f, bh * 0.05f, bw * 0.5f, new int[] {0x267C4DFF, 0x007C4DFF},
                    null, android.graphics.Shader.TileMode.CLAMP));
            bc.drawRect(0, 0, bw, bh, p);
            screenRect.set(0, 0, w, h);
            return b;
        }

        @Override protected void onDraw(Canvas c) {
            if (w == 0) return;
            long now = System.nanoTime();
            float dt = lastNanos == 0 ? 1 / 60f : Math.min(0.05f, (now - lastNanos) / 1e9f);
            lastNanos = now;
            for (int i = 0; i < pop.length; i++) {
                pop[i].target = i == focus && i < rowCount() ? 1 : 0;
                pop[i].step(dt);
            }
            long up = SystemClock.uptimeMillis();
            float seconds = (up - startedAt) / 1000f;
            lastSky = up;
            if (backdrop == null) backdrop = makeBackdrop();
            c.drawBitmap(backdrop, null, screenRect, backdropPaint);

            drawSidebar(c);
            drawClockCorner(c);

            if (screen == MAIN) drawMain(c, seconds); else if (screen == HOME_CHOICE) drawChoice(c); else if (screen == PICKER) drawPicker(c); else if (screen == SHELF_EDIT) drawShelfEditor(c);
            else drawList(c);
        }

        private static final String[] MAIN_ICONS = {"live_tv", "sparkles", "settings", "apps", "tv", "gamepad", "bedtime", "tv_settings", "sync"};

        private String[] mainLabels() {
            return new String[] {Ui.tr("Home Screen"), Ui.tr("Screensaver"), Ui.tr("Home Settings"), Ui.tr("Shelf"),
                    Ui.tr("Top Buttons"), Ui.tr("Remote Buttons"), Ui.tr("Sleep Timer"), Ui.tr("TV Settings"), Ui.tr("Update")};
        }

        /** Card `i` of the main page, in pixels. */
        private RectF cardRect(int i) {
            float y = 176, colW = (CONTENT_R - CONTENT_L - COL_GAP) / 2;
            for (int r = 0; r < GRID.length; r++) {
                for (int col = 0; col < GRID[r].length; col++) {
                    if (GRID[r][col] == i) {
                        float x = CONTENT_L + col * (colW + COL_GAP), right = GRID[r].length == 1 ? CONTENT_R : x + colW;
                        cardBox.set(x * k, y * k, right * k, (y + CARD_H) * k);
                        return cardBox;
                    }
                }
                y += CARD_H + (r == 2 || r == 3 ? 56 : 16);   // a group title after the third and fourth rows
            }
            cardBox.setEmpty();
            return cardBox;
        }

        private final RectF cardBox = new RectF();

        /** The main page: its title, then the cards in three groups and "About". */
        private void drawMain(Canvas c, float seconds) {
            c.drawText(Ui.tr("Home"), CONTENT_L * k, 112 * k, pageTitle);
            c.drawText(Ui.tr("Manage the settings of your FrontEndTV"), CONTENT_L * k, 154 * k, pageSub);
            boolean working = busy();
            String busyText = busyText(), error = state.optString("error");
            String[] labels = mainLabels();
            String[] about = {Ui.tr("FrontEndTV or the TV's original home screen"), Ui.tr("Photos and the time while the TV rests"),
                    Ui.tr("Language, weather, city, photos and widgets"), Ui.tr("The apps in the bar at the bottom"),
                    Ui.tr("The TV, HDMI and settings buttons at the top"), Ui.tr("What the remote's buttons open"),
                    Ui.tr("Turns the TV off after a while"), Ui.tr("Network, picture, sound and the rest of the TV"),
                    Ui.tr("Looked for every 30 minutes")};
            // What's going on, in the card it concerns (amber): the switch's step to do on the TV or its error, a new version
            String[] notes = new String[labels.length];
            if (working && busyText.equals(Ui.tr("Accept “Allow USB debugging?” on the TV (tick “Always allow”)."))) {
                notes[0] = Ui.tr("On the TV: tick “Always allow” and press OK.");
            } else if (!working && !error.isEmpty()) {
                notes[0] = error;
            }
            int st = Updater.state();
            if (st == Updater.AVAILABLE) notes[UPDATE_ROW] = Ui.tr("Press OK to install it.");
            else if (st == Updater.FAILED) notes[UPDATE_ROW] = Updater.label();
            // The values some cards show before their arrow
            String[] values = new String[labels.length];
            values[0] = working ? (busyText.isEmpty() ? Ui.tr("Starting…") : busyText) : freeActive() ? "FrontEndTV" : Ui.tr("Original");
            values[1] = Ui.tr("Preview");
            if (st != Updater.IDLE && st != Updater.FAILED) values[UPDATE_ROW] = Updater.label();

            String[] groups = {Ui.tr("REMOTE AND SLEEP"), Ui.tr("TV AND UPDATES"), Ui.tr("RECOMMENDATIONS")};
            int[] groupBefore = {5, 7, -1};
            for (int g = 0; g < groups.length; g++) {
                float top = groupBefore[g] >= 0 ? cardRect(groupBefore[g]).top : aboutRect().top;
                c.drawText(groups[g], CONTENT_L * k, top - 18 * k, header);
            }
            for (int i = 0; i < labels.length; i++) {
                RectF r = new RectF(cardRect(i));
                float f = Ui.clamp(pop[i].value);
                boolean off = unavailable(i) != null;
                drawCard(r, c, f);
                Symbols.draw(getContext(), c, MAIN_ICONS[i], r.left + 58 * k, r.centerY(), 40 * k, off ? 0x59FFFFFF : 0xFFEDEFF5);
                float x = r.left + 112 * k, right = r.right - 40 * k, w = right - 30 * k - x;
                // Under the name: what it's for, or (amber) what's going on, on two lines at most (the name goes up)
                String line = notes[i] != null ? notes[i] : off ? Ui.tr("Unavailable") + " · " + about[i] : about[i];
                boolean twoLines = notes[i] != null && cardDesc.measureText(line) > w;
                float base = r.top + (twoLines ? 38 : 48) * k;
                // On the right: the arrow, and before it the card's value (or why it can't be used, or a spinner)
                float valueLeft = right - 24 * k;
                if (!off) {
                    float cw = Symbols.width(getContext(), "chevron_right", 24 * k);
                    Symbols.draw(getContext(), c, "chevron_right", right - cw / 2, r.centerY(), 24 * k, 0xFFB8C0D0);
                    if (values[i] != null) {
                        boolean news = i == UPDATE_ROW && st == Updater.AVAILABLE;
                        cardValue.setColor(news ? 0xFFFFD596 : 0xFFB9A6FF);
                        String v = fit(values[i], cardValue, 330 * k);
                        float vr = right - cw - 18 * k;
                        c.drawText(v, vr, base, cardValue);
                        valueLeft = vr - cardValue.measureText(v) - 24 * k;
                        if ((i == 0 && working) || (i == UPDATE_ROW && Updater.working())) {
                            spinner.setColor(0xFFFFFFFF);
                            Ui.spinner(c, valueLeft + 8 * k, base - 8 * k, 11 * k, seconds * 1.3f, spinner);
                            valueLeft -= 26 * k;
                        } else if (news) {
                            dot.setColor(0xFFFFC266);
                            c.drawCircle(valueLeft + 10 * k, base - 8 * k, 6 * k, dot);
                        }
                    }
                }
                cardTitle.setColor(off ? 0x80FFFFFF : 0xFFFFFFFF);
                c.drawText(fit(labels[i], cardTitle, valueLeft - x), x, base, cardTitle);
                cardDesc.setColor(notes[i] != null ? AMBER : off ? 0x66FFFFFF : 0xFF9AA3B5);
                if (twoLines) {
                    int n = cardDesc.breakText(line, true, w, null);
                    int cut = line.lastIndexOf(' ', n);
                    if (cut <= 0) cut = n;
                    c.drawText(line.substring(0, cut), x, r.top + 66 * k, cardDesc);
                    c.drawText(fit(line.substring(cut).trim(), cardDesc, w), x, r.top + 92 * k, cardDesc);
                } else {
                    c.drawText(fit(line, cardDesc, w), x, r.top + 80 * k, cardDesc);
                }
            }
            drawAbout(c);
        }

        /** A card: dark glass with a thin edge; focused (f), a violet tint and a violet frame with a soft glow. */
        private void drawCard(RectF r, Canvas c, float f) {
            float rad = 18 * k;
            if (f > 0.01f) {
                shadow.setColor(Ui.alpha(0xFF6B4DFF, 0.35f * f));
                tmp.set(r);
                tmp.inset(-4 * k, -4 * k);
                c.drawRoundRect(tmp, rad, rad, shadow);
            }
            card.setColor(blend(0xFF141B29, 0xFF221F52, f));
            c.drawRoundRect(r, rad, rad, card);
            cardEdge.setStyle(Paint.Style.STROKE);
            cardEdge.setStrokeWidth((1.5f + 1.5f * f) * k);
            cardEdge.setColor(blend(0xFF232C3D, 0xFF7B61FF, f));
            c.drawRoundRect(r, rad, rad, cardEdge);
        }

        private RectF aboutRect() {
            RectF last = cardRect(7);
            aboutBox.set(CONTENT_L * k, last.bottom + 56 * k, CONTENT_R * k, last.bottom + 56 * k + CARD_H * k);
            return aboutBox;
        }

        private final RectF aboutBox = new RectF();

        /** "About": the picks' status and the credit TMDB asks for (a card to read, not to open). */
        private void drawAbout(Canvas c) {
            RectF r = new RectF(aboutRect());
            drawCard(r, c, 0);
            float cx = r.left + 58 * k, cy = r.centerY();
            dot.setColor(0xFFEDEFF5);
            c.drawCircle(cx, cy, 18 * k, dot);
            infoPaint.setColor(0xFF141B29);
            c.drawText("i", cx, cy + Ui.halfCap(infoPaint), infoPaint);
            String line;
            String error = recs.optString("error");
            int count = recs.optInt("count");
            if (!hasKey) line = Ui.tr("No TMDB key: add one with the installer on your computer.");
            else if (error.equals(Recs.NO_KNOWN_APPS)) line = Ui.tr("None of the TV's streaming apps can give picks.");
            else if (error.equals(Recs.NO_APPS)) line = Ui.trf("TMDB has none of the TV's apps for tmdb_region={region}.", "region", Recs.region());
            else if (!error.isEmpty()) line = Ui.trf("Error: {error}.", "error", error);
            else if (count > 0) line = Ui.trf("On · {n} titles from the TV's apps, 3 new ones every hour.", "n", count);
            else line = Ui.tr("Fetching titles…");
            float x = r.left + 112 * k, w = r.right - 40 * k - x;
            cardTitle.setColor(0xFFFFFFFF);
            c.drawText(Ui.tr("About"), x, r.top + 38 * k, cardTitle);
            cardDesc.setColor(0xFF9AA3B5);
            c.drawText(fit(line, cardDesc, w), x, r.top + 67 * k, cardDesc);
            String credit = Ui.tr("This product uses the TMDB API but is not endorsed or certified by TMDB.");
            if (!Config.WATCHMODE_KEY.trim().isEmpty()) credit += " " + Ui.tr("Links to the titles by Watchmode.");
            creditPaint.setColor(0xFF717A8C);
            c.drawText(fit(credit, creditPaint, w), x, r.top + 92 * k, creditPaint);
        }

        // MARK: - Sidebar and clock

        /** The sidebar's rows: -1 the main page ("Home"), 0-8 the settings, -2 "About"; a negative row under 100 is a group title. */
        private static final int[] SIDE = {-1, 0, 1, 2, 3, 4, -100, 5, 6, -101, 7, 8, -102, -2};

        /** The sidebar row of the page open now. */
        private int sideCurrent() {
            switch (screen) {
                case HOME_CHOICE: return 0;
                case HOME_SETTINGS: return 2;
                case SHELF_EDIT: return editingTop ? 4 : 3;
                case REMOTE_KEYS: return 5;
                case POWER: return 6;
                case PICKER: return pickFor == PICK_CITY || pickFor == PICK_LANGUAGE ? 2 : pickFor <= PICK_SLEEP ? 6 : 5;
                default: return -1;
            }
        }

        private void drawSidebar(Canvas c) {
            side.setColor(0xFF0B101A);
            c.drawRect(0, 0, SIDE_W * k, h, side);
            side.setColor(0xFF1C2432);
            c.drawRect(SIDE_W * k - 1.5f * k, 0, SIDE_W * k, h, side);
            // Logo, name (TV in violet), version
            if (brandIcon == null) brandIcon = android.graphics.BitmapFactory.decodeResource(getResources(), R.drawable.icon);
            if (brandIcon != null) {
                brandRect.set(52 * k, 46 * k, 140 * k, 134 * k);
                c.drawBitmap(brandIcon, null, brandRect, brandPaint);
            }
            sideBrand.setColor(0xFFFFFFFF);
            c.drawText("FrontEnd", 162 * k, 96 * k, sideBrand);
            sideBrand.setColor(0xFF9B7BFF);
            c.drawText("TV", 162 * k + sideBrand.measureText("FrontEnd") + 1 * k, 96 * k, sideBrand);
            c.drawText(versionName(getContext()), 162 * k, 130 * k, sideVersion);

            String[] labels = mainLabels();
            String[] groups = {Ui.tr("REMOTE AND SLEEP"), Ui.tr("TV AND UPDATES"), Ui.tr("RECOMMENDATIONS")};
            int current = sideCurrent(), hover = screen == MAIN ? focus : -99;
            float y = 186 * k;
            for (int item : SIDE) {
                if (item <= -100) {   // a line, then the group's title
                    side.setColor(0xFF1C2432);
                    c.drawRect(44 * k, y + 14 * k, (SIDE_W - 44) * k, y + 15.5f * k, side);
                    c.drawText(groups[-100 - item], 52 * k, y + 50 * k, sideHeader);
                    y += 68 * k;
                    continue;
                }
                float rowH = item == -1 ? 66 * k : 56 * k, cy = y + rowH / 2;
                boolean on = item == current;
                if (on) {
                    tmp.set(30 * k, y + 2 * k, (SIDE_W - 30) * k, y + rowH - 2 * k);
                    pill.setShader(new android.graphics.LinearGradient(tmp.left, 0, tmp.right, 0, 0xFF5B44E0, 0xFF6E4FF0,
                            android.graphics.Shader.TileMode.CLAMP));
                    pill.setAlpha(255);
                    c.drawRoundRect(tmp, 14 * k, 14 * k, pill);
                } else if (item == hover) {   // the card focused on the main page: a violet mark beside its row
                    tmp.set(30 * k, cy - 14 * k, 35 * k, cy + 14 * k);
                    dot.setColor(0xFF8B6CFF);
                    c.drawRoundRect(tmp, 2.5f * k, 2.5f * k, dot);
                }
                String name = item == -1 ? Ui.tr("Home") : item == -2 ? Ui.tr("About") : labels[item];
                if (item == 5 && sideItem.measureText(name) > (SIDE_W - 160) * k) name = Ui.tr("Remote control");
                String icon = item == -1 ? "home" : item == -2 ? null : MAIN_ICONS[item];
                int ink = on || item == hover ? 0xFFFFFFFF : 0xFFC9CFDB;
                if (icon != null) Symbols.draw(getContext(), c, icon, 82 * k, cy, 30 * k, ink);
                else {   // "About": an i in a circle
                    dot.setColor(ink);
                    c.drawCircle(82 * k, cy, 14 * k, dot);
                    infoSmall.setColor(0xFF0B101A);
                    c.drawText("i", 82 * k, cy + Ui.halfCap(infoSmall), infoSmall);
                }
                sideItem.setColor(ink);
                c.drawText(fit(name, sideItem, (SIDE_W - 160) * k), 132 * k, cy + Ui.halfCap(sideItem), sideItem);
                y += rowH + (item == -1 ? 6 * k : 0);
            }
        }

        private boolean onWifi = true, online = true;

        /** The network (read with the state, every couple of seconds). */
        private void readNetwork() {
            try {
                android.net.ConnectivityManager cm = (android.net.ConnectivityManager) getContext().getSystemService(Context.CONNECTIVITY_SERVICE);
                android.net.NetworkCapabilities nc = cm.getNetworkCapabilities(cm.getActiveNetwork());
                online = nc != null;
                onWifi = nc != null && nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI);
            } catch (RuntimeException e) {
                online = onWifi = false;
            }
        }

        /** Top right: the Wi-Fi (crossed out without a network), the time and the date. */
        private void drawClockCorner(Canvas c) {
            long now = System.currentTimeMillis();
            String time = Ui.clock(now);
            String date = java.text.DateFormat.getDateInstance(java.text.DateFormat.MEDIUM, Ui.locale()).format(new java.util.Date(now));
            float right = CONTENT_R * k;
            c.drawText(time, right, 92 * k, cornerTime);
            c.drawText(date, right, 122 * k, cornerDate);
            float left = right - Math.max(cornerTime.measureText(time), cornerDate.measureText(date)) - 34 * k;
            if (onWifi || !online) drawWifi(c, left - 22 * k, 92 * k, 22 * k, online ? 0xFFFFFFFF : 0x66FFFFFF, !online);
        }

        /** The Wi-Fi sign: three arcs over a dot, `r` its radius, (cx, by) the dot. */
        private void drawWifi(Canvas c, float cx, float by, float r, int color, boolean crossed) {
            wifi.setColor(color);
            wifi.setStyle(Paint.Style.STROKE);
            wifi.setStrokeCap(Paint.Cap.ROUND);
            wifi.setStrokeWidth(r * 0.17f);
            for (int i = 1; i <= 3; i++) {
                float rr = r * (0.33f * i + 0.05f);
                tmp.set(cx - rr, by - rr, cx + rr, by + rr);
                c.drawArc(tmp, -135, 90, false, wifi);
            }
            wifi.setStyle(Paint.Style.FILL);
            c.drawCircle(cx, by, r * 0.12f, wifi);
            if (crossed) {
                wifi.setStyle(Paint.Style.STROKE);
                c.drawLine(cx - r, by - r * 1.15f, cx + r, by + r * 0.1f, wifi);
            }
        }

        private final Paint dot = new Paint(Paint.ANTI_ALIAS_FLAG);

        // MARK: - Learning a remote button (TVs whose buttons FrontEndTV doesn't know: Philips...)

        private boolean learning;
        private long learnAt;

        private void startLearning() {
            learning = true;
            learnAt = SystemClock.uptimeMillis();
            RemoteKeys.startLearning(getContext());
            handler.removeCallbacks(poll);
            handler.post(poll);
            invalidate();
        }

        private void stopLearning() {
            learning = false;
            RemoteKeys.stopLearning(getContext());
            invalidate();
        }

        /** A button was pressed: it joins the list, and the app picker opens for it. */
        private void learned(int id) {
            stopLearning();
            Prefs.learnKey(id);
            int[] keys = Prefs.keys();
            for (int i = 0; i < keys.length; i++) {
                if (keys[i] == id) {
                    openPicker(i + 1);
                    break;
                }
            }
            for (Spring s : pop) s.snap(0);
            invalidate();
        }

        /** `text` cut to fit `width`, with "…" where it was cut. */
        private static String fit(String text, Paint p, float width) {
            if (p.measureText(text) <= width) return text;
            int n = p.breakText(text, true, Math.max(0, width - p.measureText("…")), null);
            return n <= 0 ? "" : text.substring(0, n).trim() + "…";
        }

        /** List row: no background; focused, like a focused card (violet tint and frame). Returns how focused it is. */
        private float drawRowBackground(Canvas c, int i) {
            float f = Ui.clamp(pop[i].value);
            if (f > 0.01f) {
                float r = 16 * k;
                shadow.setColor(Ui.alpha(0xFF6B4DFF, 0.3f * f));
                c.drawRoundRect(rowRect, r, r, shadow);
                card.setColor(Ui.alpha(0xFF221F52, f));
                c.drawRoundRect(rowRect, r, r, card);
                cardEdge.setStyle(Paint.Style.STROKE);
                cardEdge.setStrokeWidth(3 * k);
                cardEdge.setColor(Ui.alpha(0xFF7B61FF, f));
                c.drawRoundRect(rowRect, r, r, cardEdge);
            }
            return f;
        }

        private float drawComparison(Canvas c, float y) {
            float x = LIST_X * k + 30 * k, colA = LIST_R * k - 220 * k, colB = LIST_R * k - 30 * k;
            c.drawText(Ui.tr("COMPARISON"), x, y, header);
            cellValue.setColor(0x73FFFFFF);
            c.drawText(Ui.tr("Original"), colA, y, cellValue);
            c.drawText("FrontEndTV", colB, y, cellValue);
            cellValue.setColor(0xD9FFFFFF);
            JSONObject metrics = state.optJSONObject("metrics");
            JSONObject a = metrics == null ? null : metrics.optJSONObject(HomeMode.ORIGINAL);
            JSONObject b = metrics == null ? null : metrics.optJSONObject(HomeMode.FREE);
            String[][] rows = {{Ui.tr("Free memory"), "memAvailableMB"}, {Ui.tr("Home screen memory"), "homePssMB"}};
            for (int i = 0; i < rows.length; i++) {
                float base = y + (46 + 40 * i) * k;
                c.drawText(rows[i][0], x, base, cellLabel);
                c.drawText(metric(a, rows[i][1]), colA, base, cellValue);
                c.drawText(metric(b, rows[i][1]), colB, base, cellValue);
            }
            return y + (46 + 40 * rows.length) * k;
        }

        /** Home screen choice: FrontEndTV or the original, with a check on the one in use. */
        private void drawChoice(Canvas c) {
            c.drawText(Ui.tr("Home Screen"), CONTENT_L * k, 112 * k, pageTitle);
            String[][] options = {{"FrontEndTV", Ui.tr("No ads, with the new screensaver")},
                    {Ui.tr("Original"), Ui.tr("The TV's factory home screen")}};
            int current = freeActive() ? 0 : 1;
            for (int i = 0; i < 2; i++) {
                float y = (LIST_TOP + 20 + i * (CHOICE_H + ROW_GAP)) * k;
                rowRect.set(LIST_X * k, y, LIST_R * k, y + CHOICE_H * k);
                float f = drawRowBackground(c, i);
                rowLabel.setColor(blend(0xF2FFFFFF, INK, f));
                rowSub.setColor(blend(0x8CFFFFFF, 0x99FFFFFF, f));
                c.drawText(options[i][0], rowRect.left + 30 * k, rowRect.top + 44 * k, rowLabel);
                c.drawText(options[i][1], rowRect.left + 30 * k, rowRect.top + 80 * k, rowSub);
                if (i == current) {
                    float cw = Symbols.width(getContext(), "check", 26 * k);
                    Symbols.draw(getContext(), c, "check", rowRect.right - 30 * k - cw / 2, rowRect.centerY(), 26 * k, blend(0xFFFFFFFF, INK, f));
                }
            }
            Paint hint = Ui.text(Ui.regular, 23 * k, 0x8CFFFFFF);
            float y = (LIST_TOP + 20 + 2 * (CHOICE_H + ROW_GAP) + 40) * k;
            c.drawText(Ui.tr("Switching takes a few seconds."), LIST_X * k + 30 * k, y, hint);
            // How the two compare (memory, measured two minutes after each switch), in a soft glass card
            float top = y + 50 * k, cardH = (36 + 126) * k;
            tmp.set(LIST_X * k, top, LIST_R * k, top + cardH);
            card.setColor(0x0FFFFFFF);
            c.drawRoundRect(tmp, 22 * k, 22 * k, card);
            cardEdge.setStyle(Paint.Style.STROKE);
            cardEdge.setStrokeWidth(1.5f * k);
            cardEdge.setColor(0x1FB8A6FF);
            c.drawRoundRect(tmp, 22 * k, 22 * k, cardEdge);
            drawComparison(c, top + 46 * k);
        }

        private static int blend(int a, int b, float f) {
            int aa = a >>> 24, ar = (a >> 16) & 255, ag = (a >> 8) & 255, ab = a & 255;
            int ba = b >>> 24, br = (b >> 16) & 255, bg = (b >> 8) & 255, bb = b & 255;
            return (Math.round(aa + (ba - aa) * f) << 24) | (Math.round(ar + (br - ar) * f) << 16)
                    | (Math.round(ag + (bg - ag) * f) << 8) | Math.round(ab + (bb - ab) * f);
        }

        private static String metric(JSONObject m, String key) {
            if (m == null || !m.has(key)) return "—";
            long v = m.optLong(key);
            if (key.equals("memAvailableMB") && v >= 1024) return Ui.trf("{n} GB", "n", String.format(Ui.locale(), "%.2f", v / 1024.0));
            return Ui.trf("{n} MB", "n", v);
        }

        private static String versionName(Context c) {
            try {
                return "Version " + c.getPackageManager().getPackageInfo(c.getPackageName(), 0).versionName;
            } catch (android.content.pm.PackageManager.NameNotFoundException e) {
                return "FrontEndTV";
            }
        }

        /** The main page's arrows: up and down change row (same column, or the row's only card), left and right column. */
        private static int gridMove(int from, int rows, int cols) {
            int r = 0, col = 0;
            for (int i = 0; i < GRID.length; i++) {
                for (int j = 0; j < GRID[i].length; j++) if (GRID[i][j] == from) { r = i; col = j; }
            }
            if (cols != 0) {
                int j = col + cols;
                return j >= 0 && j < GRID[r].length ? GRID[r][j] : from;
            }
            int to = r + rows;
            if (to < 0 || to >= GRID.length) return from;
            return GRID[to][Math.min(col, GRID[to].length - 1)];
        }

        boolean onKey(KeyEvent e) {
            boolean down = e.getAction() == KeyEvent.ACTION_DOWN;
            if (learning) {
                if (e.getKeyCode() == KeyEvent.KEYCODE_BACK) {
                    if (!down) stopLearning();
                } else if (RemoteKeys.learnable(e) && down && e.getRepeatCount() == 0) {
                    learned(RemoteKeys.id(e));
                }
                return true;
            }
            switch (e.getKeyCode()) {
                case KeyEvent.KEYCODE_DPAD_UP:
                case KeyEvent.KEYCODE_DPAD_DOWN:
                    if (screen == MAIN) {
                        if (down) focus = gridMove(focus, e.getKeyCode() == KeyEvent.KEYCODE_DPAD_DOWN ? 1 : -1, 0);
                        invalidate();
                        spoken.focusMoved();
                        return true;
                    }
                    if (e.getKeyCode() == KeyEvent.KEYCODE_DPAD_DOWN) {
                        if (down && focus < rowCount() - 1) focus++;
                        else if (down && scrolling() && pickScroll + rowCount() < listSize()) pickScroll++;
                        invalidate();
                        spoken.focusMoved();
                        return true;
                    }
                    if (down && focus > 0) focus--;
                    else if (down && scrolling() && pickScroll > 0) pickScroll--;
                    invalidate();
                    spoken.focusMoved();
                    return true;
                case KeyEvent.KEYCODE_DPAD_LEFT:
                case KeyEvent.KEYCODE_DPAD_RIGHT:
                    if (screen == MAIN) {
                        if (down) focus = gridMove(focus, 0, e.getKeyCode() == KeyEvent.KEYCODE_DPAD_RIGHT ? 1 : -1);
                        invalidate();
                        spoken.focusMoved();
                        return true;
                    }
                    if (down && screen == SHELF_EDIT) {
                        moveShelfApp(e.getKeyCode() == KeyEvent.KEYCODE_DPAD_RIGHT ? 1 : -1);
                    } else if (down && (screen == HOME_SETTINGS || screen == REMOTE_KEYS || screen == POWER)) {
                        adjust(e.getKeyCode() == KeyEvent.KEYCODE_DPAD_RIGHT ? 1 : -1);
                    }
                    whyRows = null;   // a value or the rows' order changed: what each row needs may have too
                    invalidate();
                    return true;
                case KeyEvent.KEYCODE_DPAD_CENTER:
                case KeyEvent.KEYCODE_ENTER:
                case KeyEvent.KEYCODE_NUMPAD_ENTER:
                    if (down) {
                        if (e.getRepeatCount() == 0) okDown = true;
                    } else {
                        if (okDown && !e.isCanceled() && spoken.keyClick()) {
                            activate();
                            whyRows = null;   // the same after OK
                        }
                        okDown = false;
                    }
                    return true;
                case KeyEvent.KEYCODE_BACK:
                    if (screen == MAIN) return false;   // leaves the settings
                    if (down) {
                        if (e.getRepeatCount() == 0) backDown = true;
                    } else {
                        if (backDown && !e.isCanceled()) {
                            if (screen == PICKER && pickFor <= PICK_SLEEP) {
                                focus = powerFocus(pickFor == PICK_SLEEP ? P_SLEEP : P_WHERE);
                                pickScroll = 0;   // the picker's scroll isn't this short list's
                                screen = POWER;
                            } else if (screen == PICKER && (pickFor == PICK_CITY || pickFor == PICK_LANGUAGE)) {
                                focus = pickFor == PICK_CITY ? CITY_ROW : 0;
                                pickScroll = 0;   // both rows are on the list's first screen
                                screen = HOME_SETTINGS;
                            } else if (screen == PICKER) {
                                focus = pickFor - 1;
                                screen = REMOTE_KEYS;
                            } else {
                                focus = screen == HOME_SETTINGS ? 2 : screen == SHELF_EDIT ? (editingTop ? 4 : 3)
                                        : screen == REMOTE_KEYS ? 5 : screen == POWER ? 6 : 0;
                                screen = MAIN;
                            }
                        }
                        backDown = false;
                    }
                    invalidate();
                    spoken.focusMoved();
                    return true;
                default:
                    return false;
            }
        }

        // MARK: - Home Settings and Remote Buttons

        private static final String[] UNITS = {"auto", "C", "F"}, THEMES = {"dark"};
        /** Home Settings: language, temperature, theme, my photos, sounds, weather, city, quotes, then one row per coin. */
        private static final int PHOTOS_ROW = 3, CITY_ROW = 6, COIN_ROWS = 8;
        /** Home Settings rows on screen at once (what fits above the hints at 1080p); a longer list scrolls. */
        private static final int SETTINGS_VISIBLE = 10;

        private boolean dollar() {
            return "BRL".equalsIgnoreCase(Config.CURRENCY.trim());
        }

        private String[] settingLabels() {
            List<String> l = new ArrayList<>();
            l.add(Ui.tr("Language"));
            l.add(Ui.tr("Temperature"));
            l.add(Ui.tr("Theme"));
            l.add(Ui.tr("My Photos"));
            l.add(Ui.tr("Navigation sounds"));
            l.add(Ui.tr("Weather widget"));
            l.add(Ui.tr("City"));
            l.add(Ui.tr("Quotes widget"));
            l.add("Bitcoin");
            l.add("Ethereum");
            l.add("Solana");
            if (dollar()) l.add(Ui.tr("Dollar"));
            return l.toArray(new String[0]);
        }

        private String[] settingValues() {
            String[] labels = settingLabels();
            String[] v = new String[labels.length];
            String lang = Prefs.language();
            v[0] = lang.equals("auto") ? Ui.tr("Automatic") : Ui.languageName(lang);
            String unit = Prefs.temperature();
            v[1] = unit.equals("C") ? "°C" : unit.equals("F") ? "°F" : Ui.tr("unit|Automatic");
            String theme = Prefs.theme();
            v[2] = theme.equals("light") ? Ui.tr("Light") : theme.equals("dark") ? Ui.tr("Dark") : Ui.tr("Automatic");
            int photos = Photos.files(getContext()).size();
            v[PHOTOS_ROW] = photos == 0 ? Ui.tr("photos|None")
                    : Prefs.photos() ? Ui.plural("plural|one:{n} photo|other:{n} photos", photos) : Ui.tr("Off");
            v[4] = onOff(Prefs.sounds());
            v[5] = onOff(Prefs.weather());
            v[CITY_ROW] = !cityStatus.isEmpty() ? cityStatus : Prefs.city().isEmpty() ? "—" : Prefs.city();
            v[7] = onOff(Prefs.quotes());
            // No quotes widget, nowhere to show prices: the coins read as off (and stay locked) until it is back on.
            for (int i = COIN_ROWS; i < v.length; i++) v[i] = onOff(Prefs.quotes() && Prefs.coin(i - COIN_ROWS));
            return v;
        }

        /** "auto", then English and every language with a catalogue. */
        private static String[] languageChoices() {
            String[] all = Ui.languages();
            String[] v = new String[all.length + 1];
            v[0] = "auto";
            System.arraycopy(all, 0, v, 1, all.length);
            return v;
        }

        /**
         * What the focused screen's row `i` needs from the TV (Capabilities.NEED_…), or null. A new option that needs
         * something adds its line here; drawing, keys and TalkBack then treat it as unavailable where the TV lacks it.
         */
        private String need(int i) {
            switch (screen) {
                case MAIN:
                    return i == 5 ? Capabilities.NEED_REMOTE_BUTTONS : i == 6 ? Capabilities.NEED_ADB : null;
                case POWER: {
                    int id = powerRows()[i];
                    return id == P_WHERE ? Capabilities.NEED_OVERLAY : null;
                }
                case SHELF_EDIT: {   // only to add it: one already among the buttons can still move or go
                    String key = shelfRows.get(pickScroll + i)[0];
                    return editingTop && HomeApps.TOP_LIVE.equals(key) && !shelfOrder.contains(key) ? Capabilities.NEED_LIVE_TV : null;
                }
                default:
                    return null;
            }
        }

        /** Why row `i` can't be used here, for the person to read; null where it works. */
        private String unavailable(int i) {
            int n = rowCount();
            if (i < 0 || i >= n) return null;
            if (whyRows == null || whyRows.length != n || whyScreen != screen || whyScroll != pickScroll) {
                whyRows = new String[n];
                whyScreen = screen;
                whyScroll = pickScroll;
                for (int r = 0; r < n; r++) {
                    String need = need(r);
                    whyRows[r] = screen == HOME_SETTINGS && pickScroll + r >= COIN_ROWS && !Prefs.quotes()
                            ? Ui.tr("Turn on the Quotes widget to pick its coins.")
                            : need == null ? null : Capabilities.missing(getContext(), need);
                }
            }
            return whyRows[i];
        }

        /** OK on an unavailable row: a toast says why; false if it isn't unavailable. */
        private boolean tellWhy() {
            String why = unavailable(focus);
            if (whyToast != null) whyToast.cancel();
            whyToast = why == null ? null : Toast.makeText(getContext(), why, Toast.LENGTH_LONG);
            if (whyToast != null) whyToast.show();
            return why != null;
        }

        /**
         * Sleep Timer's rows, by what they set: the timer, whether it shows on screen and (where it shows over other
         * apps) where. Values are picked from lists (OK), as on the TV's own settings; ← → step them.
         */
        private static final int P_SLEEP = 0, P_SHOW = 1, P_WHERE = 2;

        private int[] powerRows() {
            return Prefs.sleepShown() ? new int[] {P_SLEEP, P_SHOW, P_WHERE} : new int[] {P_SLEEP, P_SHOW};
        }

        private int powerRow() {
            int[] rows = powerRows();
            return rows[Math.min(focus, rows.length - 1)];
        }

        private int powerFocus(int id) {
            int[] rows = powerRows();
            for (int i = 0; i < rows.length; i++) if (rows[i] == id) return i;
            return 0;
        }

        private String[] powerLabels() {
            int[] rows = powerRows();
            String[] out = new String[rows.length];
            for (int i = 0; i < rows.length; i++) {
                switch (rows[i]) {
                    case P_SLEEP: out[i] = Ui.tr("Sleep Timer"); break;
                    case P_SHOW: out[i] = Ui.tr("Show Time Left"); break;
                    default: out[i] = Ui.tr("Position");
                }
            }
            return out;
        }

        private String[] powerValues() {
            int[] rows = powerRows();
            String[] out = new String[rows.length];
            for (int i = 0; i < rows.length; i++) {
                switch (rows[i]) {
                    case P_SLEEP: out[i] = sleepLeft == 0 ? Ui.tr("Off") : Ui.trf("{time} left", "time", SleepTimer.duration(sleepLeft)); break;
                    case P_SHOW: out[i] = onOff(Prefs.sleepShown()); break;
                    default: out[i] = SleepOverlay.placeName(Prefs.sleepPlace());
                }
            }
            return out;
        }

        /** OK on a Sleep Timer row: its list of choices, or (on a switch) the other value. */
        private void activatePower() {
            Context c = getContext();
            List<String[]> list = new ArrayList<>();
            switch (powerRow()) {
                case P_SLEEP:
                    list.add(new String[] {"0", Ui.tr("Off")});
                    for (int m : new int[] {5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240}) {
                        list.add(new String[] {String.valueOf(m), SleepTimer.duration(m)});
                    }
                    int left = SleepTimer.minutesLeft(c);
                    openChoices(PICK_SLEEP, list, String.valueOf(left));
                    return;
                case P_WHERE:
                    for (int i = 0; i < SleepOverlay.PLACES; i++) list.add(new String[] {String.valueOf(i), SleepOverlay.placeName(i)});
                    openChoices(PICK_PLACE, list, String.valueOf(Prefs.sleepPlace()));
                    return;
                default:
                    adjust(1);
            }
        }

        private void openChoices(int which, List<String[]> list, String now) {
            choices.clear();
            choices.addAll(list);
            choiceNow = now;
            openPicker(which);
        }

        /** OK in a Sleep Timer picker: applied, and back to its row. */
        private void pickPower() {
            Context c = getContext();
            String[] choice = choices.get(pickScroll + focus);
            int row = pickFor == PICK_SLEEP ? P_SLEEP : P_WHERE;
            if (pickFor == PICK_SLEEP) {
                SleepTimer.set(c, Integer.parseInt(choice[0]));
                sleepLeft = SleepTimer.minutesLeft(c);
            } else {
                Prefs.setSleepPlace(Integer.parseInt(choice[0]));
                SleepOverlay.update(c);
            }
            pickScroll = 0;
            screen = POWER;
            focus = powerFocus(row);
        }

        private String[] listLabels() {
            return screen == REMOTE_KEYS ? keyLabels() : screen == POWER ? powerLabels() : settingLabels();
        }

        private String[] listValues() {
            return screen == REMOTE_KEYS ? keyValues() : screen == POWER ? powerValues() : settingValues();
        }

        private static String onOff(boolean on) {
            return on ? Ui.tr("On") : Ui.tr("Off");
        }

        private String[] keyLabels() {
            if (RemoteKeys.LEARNED) {
                int[] keys = Prefs.keys();
                java.util.Map<Integer, String> found = Prefs.detectedKeys();
                int n = keys.length;
                String[] l = new String[n + 1];
                for (int i = 0; i < n; i++) {
                    // Named after the app the TV opens with it ("Netflix button"), else numbered
                    String tv = found.get(keys[i]);
                    l[i] = tv != null ? Ui.trf("{app} button", "app", appName(tv)) : Ui.trf("Button {n}", "n", i + 1);
                }
                l[n] = Ui.tr("+ Learn a button");
                return l;
            }
            if (RemoteKeys.STEM_NETFLIX) return new String[] {Ui.tr("Netflix button")};
            if (RemoteKeys.KTC) return new String[] {Ui.tr("Tabii button")};
            if (RemoteKeys.XIAOMI) return new String[] {Ui.tr("Mi button"), Ui.tr("Okko button"), Ui.tr("VK button"),
                    Ui.tr("Rutube button"), Ui.tr("KION button"), Ui.tr("Wink button"), Ui.tr("IVI button")};
            if (RemoteKeys.SONY) return new String[] {Ui.tr("BRAVIA CORE button"),
                    Ui.tr("Netflix button"), Ui.tr("Disney+ button"),
                    Ui.tr("Prime Video button"), Ui.tr("YouTube button"),
                    Ui.tr("Crunchyroll button"), Ui.tr("Google Play button")};
            return new String[] {Ui.tr("Netflix button"), Ui.tr("YouTube button"),
                    Ui.tr("Prime Video button"), Ui.tr("TCL Channel button")};
        }

        private String[] keyValues() {
            int[] keys = Prefs.keys();
            int n = keys.length;
            String[] v = new String[n + (RemoteKeys.LEARNED ? 1 : 0)];
            if (RemoteKeys.LEARNED) {
                v[n] = learning ? Ui.tr("Press it…") : "";
                java.util.Map<Integer, String> found = Prefs.detectedKeys();
                for (int i = 0; i < n; i++) {
                    String pkg = Prefs.keyApp(keys[i]), tv = found.get(keys[i]);
                    // Nothing picked: the TV does what it always did with it
                    v[i] = !pkg.isEmpty() ? appName(pkg) : tv != null ? Ui.tr("Handled by the TV") : Ui.tr("Nothing");
                }
                return v;
            }
            for (int i = 0; i < n; i++) {
                int at = appPkgs.indexOf(Prefs.keyApp(Prefs.keys()[i]));
                v[i] = at >= 0 ? appLabels.get(at) : Ui.tr("Not installed");
            }
            return v;
        }

        /** An app's name, as in the app list (its package if it isn't there). */
        private String appName(String pkg) {
            int at = appPkgs.indexOf(pkg);
            if (at >= 0) return appLabels.get(at);
            try {
                android.content.pm.PackageManager pm = getContext().getPackageManager();
                return pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString();
            } catch (android.content.pm.PackageManager.NameNotFoundException e) {
                return pkg;
            }
        }

        /** Installed TV apps, by name, after "Nothing": what a remote button can open. */
        private void loadApps() {
            List<String[]> found = new ArrayList<>();
            for (String[] app : HomeApps.launchable(getContext())) {
                if (!app[0].equals(getContext().getPackageName())) found.add(app);
            }
            Collections.sort(found, (a, b) -> a[1].toLowerCase(Locale.ROOT).compareTo(b[1].toLowerCase(Locale.ROOT)));
            appPkgs.clear();
            appLabels.clear();
            appPkgs.add("");
            // On Sony and Xiaomi, "nothing" leaves the key to the TV, which opens its usual app.
            appLabels.add(RemoteKeys.SONY ? Ui.tr("Sony default") : RemoteKeys.XIAOMI ? Ui.tr("Xiaomi default") : Ui.tr("Nothing"));
            // On Xiaomi the Mi button is the remote's Home button: it can open this home screen.
            if (RemoteKeys.XIAOMI) {
                appPkgs.add(HomeMode.PKG);
                appLabels.add("FrontEndTV");
            }
            for (String[] f : found) {
                appPkgs.add(f[0]);
                appLabels.add(f[1]);
            }
        }

        /** Changes the focused row's value one step (OK and → go forward, ← goes back). */
        private void adjust(int dir) {
            if (unavailable(focus) != null) return;
            if (screen == POWER) {
                int row = powerRow();
                if (row == P_SLEEP) {
                    SleepTimer.step(getContext(), dir);
                    sleepLeft = SleepTimer.minutesLeft(getContext());
                } else if (row == P_SHOW) {
                    Prefs.setSleepShown(!Prefs.sleepShown());
                    SleepOverlay.update(getContext());
                } else if (row == P_WHERE) {
                    Prefs.setSleepPlace((Prefs.sleepPlace() + dir + SleepOverlay.PLACES) % SleepOverlay.PLACES);
                    SleepOverlay.update(getContext());
                }
                focus = powerFocus(row);   // rows come and go with the switches above them
                return;
            }
            if (screen == REMOTE_KEYS) {
                if (focus >= Prefs.keys().length) return;   // the "Learn a button" row
                int n = appPkgs.size();
                int at = appPkgs.indexOf(Prefs.keyApp(Prefs.keys()[focus]));
                int next = at < 0 ? (dir > 0 ? 0 : n - 1) : (at + dir + n) % n;
                Prefs.setKeyApp(Prefs.keys()[focus], appPkgs.get(next));
                return;
            }
            int row = pickScroll + focus;
            switch (row) {
                case 0: break;   // OK opens the language picker.
                case 1: {
                    int at = java.util.Arrays.asList(UNITS).indexOf(Prefs.temperature());
                    Prefs.setTemperature(UNITS[(at + dir + UNITS.length) % UNITS.length]);
                    break;
                }
                case 2: {
                    int at = java.util.Arrays.asList(THEMES).indexOf(Prefs.theme());
                    Prefs.setTheme(THEMES[(at + dir + THEMES.length) % THEMES.length]);
                    SleepOverlay.update(getContext());   // the pill's glass follows
                    break;
                }
                case PHOTOS_ROW:
                    if (Photos.files(getContext()).isEmpty()) return;
                    Prefs.setPhotos(!Prefs.photos());
                    return;
                case 4: Prefs.setSounds(!Prefs.sounds()); break;
                case 5: Prefs.setWeather(!Prefs.weather()); break;
                case CITY_ROW: return;
                case 7: Prefs.setQuotes(!Prefs.quotes()); break;
                default:
                    if (!Prefs.quotes()) return;
                    Prefs.setCoin(row - COIN_ROWS, !Prefs.coin(row - COIN_ROWS));
                    break;
            }
            HomeData.get(getContext()).settingsChanged();
        }

        private void askCity() {
            if (Ui.tr("Searching…").equals(cityStatus)) return;
            EditText field = new EditText(activity);
            field.setSingleLine(true);
            field.setHint(Ui.tr("Type the city's name"));
            field.setImeOptions(EditorInfo.IME_ACTION_SEARCH);
            AlertDialog dialog = new AlertDialog.Builder(activity, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                    .setTitle(Ui.tr("City"))
                    .setView(field)
                    .setPositiveButton(Ui.tr("Search"), (d, which) -> searchCity(field.getText().toString()))
                    .setNegativeButton(Ui.tr("Cancel"), null)
                    .create();
            field.setOnEditorActionListener((v, action, event) -> {
                dialog.dismiss();
                searchCity(field.getText().toString());
                return true;
            });
            dialog.show();
        }

        // MARK: - My Photos (sent from a computer: tools/photos.py)

        /** OK on My Photos: asks, then deletes them all. */
        private void askRemovePhotos() {
            int n = Photos.files(getContext()).size();
            if (n == 0) return;
            AlertDialog dialog = new AlertDialog.Builder(activity, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                    .setTitle(Ui.plural("plural|one:Remove {n} photo?|other:Remove {n} photos?", n))
                    .setPositiveButton(Ui.tr("Remove"), (d, which) -> {
                        Photos.removeAll(getContext());
                        invalidate();
                        spoken.focusMoved();
                    })
                    .setNegativeButton(Ui.tr("Cancel"), null)
                    .show();
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).requestFocus();   // one stray OK keeps them
        }

        private void searchCity(String name) {
            if (name.trim().isEmpty()) return;
            cityStatus = Ui.tr("Searching…");
            invalidate();
            new Thread(() -> {
                List<String[]> found;
                String error;
                try {
                    found = HomeData.findCity(name);
                    error = found.isEmpty() ? Ui.tr("No city with that name.") : "";
                } catch (Exception e) {
                    found = new ArrayList<>();
                    error = Ui.trf("Couldn't search now: {error}", "error", e.getMessage());
                    AppLog.w("city search: " + e.getMessage());
                }
                List<String[]> result = found;
                String message = error;
                handler.post(() -> {
                    cityStatus = message;
                    if (!result.isEmpty() && screen == HOME_SETTINGS) {
                        cities.clear();
                        cities.addAll(result);
                        openPicker(PICK_CITY);
                        spoken.focusMoved();
                    }
                    invalidate();
                    if (!message.isEmpty()) handler.postDelayed(() -> { cityStatus = ""; invalidate(); }, 4000);
                });
            }).start();
        }

        private boolean scrolling() {
            return screen == PICKER || screen == SHELF_EDIT || screen == HOME_SETTINGS;
        }

        private int listSize() {
            return screen == SHELF_EDIT ? shelfRows.size() : screen == HOME_SETTINGS ? settingLabels().length : pickLabels.size();
        }

        // MARK: - Shelf editor

        private void openShelfEditor(boolean top) {
            editingTop = top;
            shelfOrder.clear();
            if (top) {
                shelfOrder.addAll(HomeApps.topButtonKeys());
                rebuildShelfRows(HomeApps.topCandidates());
            } else {
                // Only the apps the shelf shows: one that was uninstalled would hold a place (and count toward the
                // limit) without a row to take it off.
                PackageManager pm = getContext().getPackageManager();
                for (String pkg : HomeApps.shelfPackages(getContext())) if (HomeApps.launchIntent(pm, pkg) != null) shelfOrder.add(pkg);
                rebuildShelfRows(HomeApps.shelfCandidates(getContext()));
            }
            pickScroll = 0;
            focus = 0;
            screen = SHELF_EDIT;
        }

        /** Selected apps first, in shelf order, then the rest in the order they came. */
        private void rebuildShelfRows(List<String[]> candidates) {
            shelfRows.clear();
            for (String pkg : shelfOrder) {
                for (String[] cand : candidates) if (cand[0].equals(pkg)) { shelfRows.add(cand); break; }
            }
            for (String[] cand : candidates) if (!shelfOrder.contains(cand[0])) shelfRows.add(cand);
        }

        /** Same rows after a change: keeps the focus on the app that was focused. */
        private void refreshShelfRows(String focusedPkg) {
            List<String[]> all = new ArrayList<>(shelfRows);
            rebuildShelfRows(all);
            for (int i = 0; i < shelfRows.size(); i++) {
                if (!shelfRows.get(i)[0].equals(focusedPkg)) continue;
                int visible = rowCount();
                if (i < pickScroll) pickScroll = i;
                else if (i >= pickScroll + visible) pickScroll = i - visible + 1;
                focus = i - pickScroll;
                break;
            }
            if (editingTop) Prefs.setTopButtons(shelfOrder); else Prefs.setShelf(shelfOrder);
        }

        /** OK: puts the app on the shelf (at the end) or takes it off. */
        private void toggleShelfApp() {
            String pkg = shelfRows.get(pickScroll + focus)[0];
            if (shelfOrder.contains(pkg)) shelfOrder.remove(pkg);
            else if (editingTop || shelfOrder.size() < HomeApps.maxShelf(getContext())) shelfOrder.add(pkg);   // three buttons: no limit to reach
            else return;
            refreshShelfRows(pkg);
        }

        /** ← → : moves a shelf app one place earlier or later. */
        private void moveShelfApp(int dir) {
            String pkg = shelfRows.get(pickScroll + focus)[0];
            int at = shelfOrder.indexOf(pkg), to = at + dir;
            if (at < 0 || to < 0 || to >= shelfOrder.size()) return;
            shelfOrder.remove(at);
            shelfOrder.add(to, pkg);
            refreshShelfRows(pkg);
        }

        private void drawShelfEditor(Canvas c) {
            c.drawText(editingTop ? Ui.tr("Top Buttons") : Ui.tr("Shelf"), CONTENT_L * k, 112 * k, pageTitle);
            int rows = rowCount();
            for (int i = 0; i < rows; i++) {
                String[] app = shelfRows.get(pickScroll + i);
                int place = shelfOrder.indexOf(app[0]);
                float y = (LIST_TOP + 20 + i * (LIST_ROW_H + LIST_ROW_GAP)) * k;
                rowRect.set(LIST_X * k, y, LIST_R * k, y + LIST_ROW_H * k);
                float f = drawRowBackground(c, i);
                boolean off = unavailable(i) != null;
                rowLabel.setColor(off ? blend(0x73FFFFFF, 0x73FFFFFF, f)
                        : blend(place >= 0 ? 0xF2FFFFFF : 0x80FFFFFF, place >= 0 ? INK : 0x80FFFFFF, f));
                rowValue.setColor(off ? blend(0x59FFFFFF, 0x59FFFFFF, f) : blend(0xB3FFFFFF, 0xB3FFFFFF, f));
                float base = rowRect.centerY() + Ui.halfCap(rowLabel);
                c.drawText(TextUtils.ellipsize(app[1], new TextPaint(rowLabel), 520 * k, TextUtils.TruncateAt.END).toString(), rowRect.left + 30 * k, base, rowLabel);
                float right = rowRect.right - 30 * k;
                if (place >= 0 && !off) {
                    // On the shelf: a check, as in the pickers, after its place.
                    float cw = Symbols.width(getContext(), "check", 26 * k);
                    Symbols.draw(getContext(), c, "check", right - cw / 2, rowRect.centerY(), 26 * k, blend(0xFFFFFFFF, INK, f));
                    right -= cw + 16 * k;
                }
                c.drawText(off ? Ui.tr("Unavailable") : place >= 0 ? "#" + (place + 1) : Ui.tr("shelf|Off"), right, base, rowValue);
            }
            Paint hint = Ui.text(Ui.regular, 23 * k, 0x8CFFFFFF);
            float hy = (LIST_TOP + 20 + rows * (LIST_ROW_H + LIST_ROW_GAP) + 36) * k;
            if (!editingTop && HomeApps.lowRam(getContext())) {
                c.drawText(Ui.tr("This TV has little memory, so the shelf holds fewer apps."), LIST_X * k + 30 * k, hy + 38 * k, hint);
            }
            c.drawText(editingTop ? Ui.tr("OK adds or removes a button. ← → change the order.")
                    : HomeApps.maxShelf(getContext()) == Integer.MAX_VALUE ? Ui.tr("OK adds or removes an app. ← → change the order.")
                    : Ui.trf("OK adds or removes (up to {n}). ← → change the order.", "n", HomeApps.maxShelf(getContext())),
                    LIST_X * k + 30 * k, hy, hint);
        }

        private void openPicker(int which) {
            pickFor = which;
            pickLabels.clear();
            if (which == PICK_CITY) {
                for (String[] city : cities) pickLabels.add(city[0]);
                pickCurrent = -1;
            } else if (which == PICK_LANGUAGE) {
                String[] choices = languageChoices();
                for (String code : choices) {
                    pickLabels.add(code.equals("auto") ? Ui.tr("Automatic") : Ui.languageName(code));
                }
                pickCurrent = java.util.Arrays.asList(choices).indexOf(Prefs.language());
            } else if (which <= PICK_SLEEP) {
                pickCurrent = -1;
                for (int i = 0; i < choices.size(); i++) {
                    pickLabels.add(choices.get(i)[1]);
                    if (choices.get(i)[0].equals(choiceNow)) pickCurrent = i;
                }
            } else {
                pickLabels.addAll(appLabels);
                pickCurrent = appPkgs.indexOf(Prefs.keyApp(Prefs.keys()[which - 1]));
            }
            int visible = Math.min(PICK_VISIBLE, pickLabels.size());
            int at = Math.max(0, pickCurrent);
            pickScroll = Math.max(0, Math.min(at - 3, pickLabels.size() - visible));
            focus = at - pickScroll;
            screen = PICKER;
        }

        /** A long list (the apps): nine rows at a time, the one in use checked. */
        private void drawPicker(Canvas c) {
            String title = pickFor == PICK_CITY ? Ui.tr("City")
                    : pickFor == PICK_LANGUAGE ? Ui.tr("Language")
                    : pickFor == PICK_SLEEP ? Ui.tr("Sleep Timer") : pickFor == PICK_PLACE ? Ui.tr("Position")
                    : keyLabels()[pickFor - 1];
            c.drawText(title, CONTENT_L * k, 112 * k, pageTitle);
            int rows = rowCount();
            for (int i = 0; i < rows; i++) {
                int at = pickScroll + i;
                float y = (LIST_TOP + 20 + i * (LIST_ROW_H + LIST_ROW_GAP)) * k;
                rowRect.set(LIST_X * k, y, LIST_R * k, y + LIST_ROW_H * k);
                float f = drawRowBackground(c, i);
                boolean off = unavailable(i) != null;
                rowLabel.setColor(off ? blend(0x73FFFFFF, 0x73FFFFFF, f) : blend(0xF2FFFFFF, INK, f));
                c.drawText(pickLabels.get(at), rowRect.left + 30 * k, rowRect.centerY() + Ui.halfCap(rowLabel), rowLabel);
                if (off) {
                    rowValue.setColor(blend(0x59FFFFFF, 0x59FFFFFF, f));
                    c.drawText(Ui.tr("Unavailable"), rowRect.right - 30 * k, rowRect.centerY() + Ui.halfCap(rowLabel), rowValue);
                } else if (at == pickCurrent) {
                    float cw = Symbols.width(getContext(), "check", 26 * k);
                    Symbols.draw(getContext(), c, "check", rowRect.right - 30 * k - cw / 2, rowRect.centerY(), 26 * k, blend(0xFFFFFFFF, INK, f));
                }
            }
            Paint hint = Ui.text(Ui.regular, 23 * k, 0x8CFFFFFF);
            float hy = (LIST_TOP + 20 + rows * (LIST_ROW_H + LIST_ROW_GAP) + 36) * k;
            c.drawText((pickScroll + focus + 1) + " / " + pickLabels.size() + "  ·  " + Ui.tr("OK picks, Back cancels."),
                    LIST_X * k + 30 * k, hy, hint);
        }

        private void drawList(Canvas c) {
            boolean keys = screen == REMOTE_KEYS;
            String[] labels = listLabels();
            String[] values = listValues();
            c.drawText(keys ? Ui.tr("Remote Buttons") : screen == POWER ? Ui.tr("Sleep Timer") : Ui.tr("Home Settings"),
                    CONTENT_L * k, 112 * k, pageTitle);
            float pitch = LIST_ROW_H + LIST_ROW_GAP;
            int rows = keys ? labels.length : rowCount(), from = keys ? 0 : pickScroll;
            for (int i = 0; i < rows; i++) {
                float y = (LIST_TOP + 20 + i * pitch) * k;
                rowRect.set(LIST_X * k, y, LIST_R * k, y + LIST_ROW_H * k);
                float f = drawRowBackground(c, i);
                rowLabel.setColor(blend(0xF2FFFFFF, INK, f));
                boolean locked = unavailable(i) != null;
                if (locked) values[from + i] = Ui.tr("Unavailable");
                rowValue.setColor(blend(locked ? 0x59FFFFFF : 0xB3FFFFFF, locked ? 0x59FFFFFF : 0xB3FFFFFF, f));
                if (locked) rowLabel.setColor(blend(0x73FFFFFF, 0x73FFFFFF, f));
                float base = rowRect.centerY() + Ui.halfCap(rowLabel);
                c.drawText(labels[from + i], rowRect.left + 30 * k, base, rowLabel);
                c.drawText(values[from + i], rowRect.right - 30 * k, base, rowValue);
            }
            Paint hint = Ui.text(Ui.regular, 23 * k, 0x8CFFFFFF);
            float hy = (LIST_TOP + 20 + rows * pitch + 36) * k;
            // A list longer than the screen says where we are in it, as the app picker does. Photos say where they come
            // from; on the Sleep Timer screen, the timer and Position open a list on OK.
            String where = rows < labels.length ? (from + focus + 1) + " / " + labels.length + "  ·  " : "";
            if (keys && learning) {
                c.drawText(Ui.tr("Press the button on the remote. Back cancels."), LIST_X * k + 30 * k, hy, warn);
                return;
            }
            c.drawText(where + (keys && RemoteKeys.LEARNED && focus >= Prefs.keys().length
                    ? Ui.tr("OK, then press a button of the remote: you pick what it opens.") : screen == HOME_SETTINGS && from + focus == PHOTOS_ROW ? (Photos.files(getContext()).isEmpty()
                    ? Ui.tr("Send them from a computer: see “Your own photos” in the README.")
                    : Ui.tr("OK removes them; the arrows turn them on or off."))
                    : screen == POWER && powerRow() != P_SHOW && unavailable(focus) == null ? Ui.tr("OK opens the list. ← → change the value.")
                    : Ui.tr("OK or the arrows change the value.")), LIST_X * k + 30 * k, hy, hint);
            if (keys) {
                c.drawText(RemoteKeys.VESTEL ? Ui.tr("YouTube, Prime Video and Google Play: the TV itself controls those buttons.")
                        : RemoteKeys.KTC ? Ui.tr("Netflix, YouTube and Prime Video: the TV itself controls those buttons.")
                        : RemoteKeys.SONY ? Ui.tr("Sony default: the button opens what it always did.")
                        : RemoteKeys.FORMOVIE ? Ui.tr("The other buttons: the TV itself controls them.")
                        : RemoteKeys.XIAOMI ? Ui.tr("Xiaomi default: the button opens what it always did.")
                        : RemoteKeys.LEARNED ? Ui.tr("Buttons add themselves here once you use them.")
                        : RemoteKeys.supported() ? Ui.tr("Disney and Globoplay: the TV itself controls those buttons.")
                        : Ui.tr("TCL remote buttons: on this TV they work as always."), LIST_X * k + 30 * k, hy + 38 * k, hint);
            }
        }

        private void activate() {
            Context c = getContext();
            if (tellWhy()) return;
            if (screen == HOME_CHOICE) {
                boolean wantFree = focus == 0;
                boolean already = wantFree ? freeActive() : HomeMode.ORIGINAL.equals(state.optString("mode", HomeMode.ORIGINAL));
                if (!already && !busy()) {
                    HomeMode.request(c, wantFree ? HomeMode.FREE : HomeMode.ORIGINAL);
                    pendingAt = SystemClock.uptimeMillis();
                }
                screen = MAIN;
                focus = 0;
            } else if (screen == PICKER && pickFor == PICK_CITY) {
                String[] city = cities.get(pickScroll + focus);
                Prefs.setCity(city[1], city[2], city[3]);
                HomeData.get(c).settingsChanged();
                focus = CITY_ROW;
                pickScroll = 0;
                screen = HOME_SETTINGS;
            } else if (screen == PICKER && pickFor <= PICK_SLEEP) {
                pickPower();
            } else if (screen == PICKER && pickFor == PICK_LANGUAGE) {
                Prefs.setLanguage(languageChoices()[pickScroll + focus]);
                SleepOverlay.update(c);   // the pill speaks it too
                focus = 0;
                pickScroll = 0;
                screen = HOME_SETTINGS;
            } else if (screen == PICKER) {
                int at = pickScroll + focus;
                Prefs.setKeyApp(Prefs.keys()[pickFor - 1], appPkgs.get(at));
                screen = REMOTE_KEYS;
                focus = Math.min(pickFor - 1, rowCount() - 1);   // "Nothing" forgets a learned button: one row less
            } else if (screen == SHELF_EDIT) {
                toggleShelfApp();
                invalidate();
                return;
            } else if (screen == REMOTE_KEYS && focus >= Prefs.keys().length) {
                startLearning();
            } else if (screen == REMOTE_KEYS) {
                openPicker(focus + 1);
            } else if (screen == HOME_SETTINGS && pickScroll + focus == 0) {
                openPicker(PICK_LANGUAGE);
            } else if (screen == HOME_SETTINGS && pickScroll + focus == CITY_ROW) {
                askCity();
                return;
            } else if (screen == HOME_SETTINGS && pickScroll + focus == PHOTOS_ROW) {
                askRemovePhotos();
                return;
            } else if (screen == POWER) {
                activatePower();
            } else if (screen == HOME_SETTINGS) {
                adjust(1);
                invalidate();
                return;
            } else if (focus == 0) {
                if (busy()) return;
                screen = HOME_CHOICE;
                focus = freeActive() ? 0 : 1;
            } else if (focus == 1) {
                pause();
                activity.showPreview();
            } else if (focus == 2) {
                screen = HOME_SETTINGS;
                focus = 0;
                pickScroll = 0;
            } else if (focus == 3) {
                openShelfEditor(false);
            } else if (focus == 4) {
                openShelfEditor(true);
            } else if (focus == 5) {
                HomeMode.ensureRemoteService(c, 0);   // the buttons work from inside other apps through this service
                loadApps();
                screen = REMOTE_KEYS;
                focus = 0;
            } else if (focus == 6) {
                pickScroll = 0;
                screen = POWER;
                focus = 0;
            } else if (focus == UPDATE_ROW) {
                Updater.activate(c);
                invalidate();
                return;
            } else {
                activity.startActivity(new Intent(Settings.ACTION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            }
            for (Spring s : pop) s.snap(0);
            invalidate();
            spoken.focusMoved();
        }
    }
}
