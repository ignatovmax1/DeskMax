package su.familyserver.deskmax;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.content.Context;
import android.graphics.Path;
import android.graphics.Rect;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.DisplayMetrics;
import android.view.WindowManager;
import java.util.concurrent.atomic.AtomicLong;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import org.json.JSONObject;

/** Explicitly enabled accessibility bridge; an approved live session is a second mandatory gate. */
public final class RemoteAccessibilityService extends AccessibilityService {
    private static volatile RemoteAccessibilityService instance;
    private static volatile boolean active;
    private static final AtomicLong sessionGeneration = new AtomicLong();
    private final Handler main = new Handler(Looper.getMainLooper());
    private Path drag;
    private float downX, downY;
    private boolean shift;
    public static boolean isEnabled(Context ignored) { return instance != null; }
    public static boolean isEnabled() { return instance != null; }
    static void setSessionActive(boolean enabled) {
        sessionGeneration.incrementAndGet();
        active = enabled;
        RemoteAccessibilityService service = instance;
        if (service != null) service.main.post(service::clearDrag);
    }
    static void apply(JSONObject input) {
        RemoteAccessibilityService service = instance;
        long token = sessionGeneration.get();
        if (active && service != null) service.main.post(() -> { if (active && token == sessionGeneration.get()) service.handle(input); });
    }
    @Override protected void onServiceConnected() { instance = this; active = false; }
    @Override public void onAccessibilityEvent(AccessibilityEvent event) { }
    @Override public void onInterrupt() { setSessionActive(false); clearDrag(); }
    @Override public void onDestroy() { setSessionActive(false); instance = null; clearDrag(); super.onDestroy(); }
    private void clearDrag() { drag = null; shift = false; }
    private void handle(JSONObject message) {
        String kind = message.optString("kind");
        if ("releaseAll".equals(kind)) { clearDrag(); return; }
        if ("text".equals(kind)) {
            Object value = message.opt("text");
            if (value instanceof String && ((String)value).length() > 0 && ((String)value).length() <= 1024) editText((String)value, -1);
            return;
        }
        DisplayMetrics metrics = new DisplayMetrics();
        getSystemService(WindowManager.class).getDefaultDisplay().getRealMetrics(metrics);
        int width = metrics.widthPixels;
        int height = metrics.heightPixels;
        double nx = message.optDouble("x", 0), ny = message.optDouble("y", 0);
        if (!Double.isFinite(nx) || !Double.isFinite(ny) || nx < 0 || nx > 1 || ny < 0 || ny > 1) return;
        float x = (float) (nx * (width - 1)), y = (float) (ny * (height - 1));
        switch (kind) {
            case "down":
                if (!"left".equals(message.optString("button")) || passwordAt(x, y)) return;
                drag = new Path(); drag.moveTo(x, y); downX = x; downY = y; break;
            case "move": if (drag != null) { if (passwordAt(x, y)) drag = null; else drag.lineTo(x, y); } break;
            case "up":
                if (!"left".equals(message.optString("button")) || drag == null) return;
                if (!passwordAt(x, y)) { drag.lineTo(x, y); gesture(drag, Math.hypot(x - downX, y - downY) < 8 ? 60 : 300); }
                drag = null; break;
            case "wheel":
                if (passwordAt(x, y)) return;
                int delta = message.optInt("delta"); if (delta == 0 || Math.abs(delta) > 1200) return;
                Path swipe = new Path(); swipe.moveTo(x, y);
                swipe.lineTo(x, Math.max(1, Math.min(height - 2, y + Math.signum(delta) * height * .28f)));
                gesture(swipe, 220); break;
            case "keyUp": if (message.optInt("key") == 16) shift = false; break;
            case "keyDown": key(message.optInt("key")); break;
            default: break;
        }
    }
    private boolean passwordAt(float x, float y) { return passwordAt(getRootInActiveWindow(), x, y); }
    private boolean passwordAt(AccessibilityNodeInfo node, float x, float y) {
        if (node == null) return false;
        try {
            Rect bounds = new Rect(); node.getBoundsInScreen(bounds);
            if (node.isPassword() && bounds.contains((int)x, (int)y)) return true;
            for (int i = 0; i < node.getChildCount(); i++) if (passwordAt(node.getChild(i), x, y)) return true;
            return false;
        } finally { node.recycle(); }
    }
    private void gesture(Path path, long duration) {
        if (!active) return;
        dispatchGesture(new GestureDescription.Builder().addStroke(new GestureDescription.StrokeDescription(path, 0, duration)).build(), null, main);
    }
    private void key(int key) {
        if (key == 16) { shift = true; return; }
        if (key == 27) { performGlobalAction(GLOBAL_ACTION_BACK); return; }
        editText(null, key);
    }
    private void editText(String supplied, int key) {
        AccessibilityNodeInfo root = getRootInActiveWindow(); if (root == null) return;
        AccessibilityNodeInfo focus = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        try {
            if (focus == null || focus.isPassword() || !focus.isEditable()) return;
            CharSequence current = focus.getText(); String text = current == null ? "" : current.toString();
            int start = Math.max(0, Math.min(text.length(), focus.getTextSelectionStart()));
            int end = Math.max(start, Math.min(text.length(), focus.getTextSelectionEnd()));
            if (key >= 37 && key <= 40) {
                int position = key == 37 ? Math.max(0, start - 1) : key == 39 ? Math.min(text.length(), end + 1) : key == 38 ? 0 : text.length();
                Bundle b = new Bundle(); b.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, position); b.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, position);
                focus.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, b); return;
            }
            String insertion;
            if (supplied != null) insertion = supplied;
            else if (key == 8) { if (start == end && start > 0) start = text.offsetByCodePoints(start, -1); insertion = ""; }
            else if (key == 13) insertion = "\n";
            else if (key == 32) insertion = " ";
            else if (key >= 65 && key <= 90) insertion = Character.toString((char)(shift ? key : key + 32));
            else if (key >= 48 && key <= 57) insertion = Character.toString((char)key);
            else return;
            Bundle b = new Bundle(); b.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text.substring(0, start) + insertion + text.substring(end));
            if (active && focus.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, b)) {
                int cursor = start + insertion.length(); Bundle selection = new Bundle(); selection.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, cursor); selection.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, cursor);
                focus.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, selection);
            }
        } finally { if (focus != null) focus.recycle(); root.recycle(); }
    }
}
