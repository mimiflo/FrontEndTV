package io.freemytv;

import android.os.Handler;
import android.os.Looper;
import android.service.dreams.DreamService;

/**
 * The FrontEndTV screensaver (enabled in FrontEndTV mode via `settings put secure screensaver_components`). The system
 * still decides when it starts and when the TV turns off (e.g. after 10 min idle, then 2 h later), as before, unless
 * "Turn the screen off" (Settings > Screensaver) asks for sooner: then, after that many minutes of screensaver, the
 * Sleep key through the TV's ADB (SleepTimer.sleepNow). Any key exits.
 */
public class Screensaver extends DreamService {
    private DreamView view;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable screenOff = () -> SleepTimer.sleepNow(this);

    @Override public void onAttachedToWindow() {
        super.onAttachedToWindow();
        Watchdog.install(this);
        Ui.init(this);
        setInteractive(false);
        setFullscreen(true);
        setScreenBright(true);
        view = new DreamView(this);
        setContentView(view);
    }

    @Override public void onDreamingStarted() {
        super.onDreamingStarted();
        if (view != null) view.start();
        int minutes = Prefs.screenOff();
        // "Right away": a few seconds of screensaver first, so the TV doesn't seem to turn off at a press of a key.
        if (minutes >= 0 && Capabilities.adb(this)) handler.postDelayed(screenOff, Math.max(4_000, minutes * 60_000L));
    }

    @Override public void onDreamingStopped() {
        handler.removeCallbacks(screenOff);
        if (view != null) view.stop();
        super.onDreamingStopped();
    }

    @Override public void onDetachedFromWindow() {
        handler.removeCallbacks(screenOff);
        if (view != null) view.stop();
        super.onDetachedFromWindow();
    }
}
