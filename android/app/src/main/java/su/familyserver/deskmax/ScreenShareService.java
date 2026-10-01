package su.familyserver.deskmax;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.*;
import android.util.DisplayMetrics;
import android.view.WindowManager;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;

/** Capture is owned by a visible foreground service and a fresh OS consent token. */
public final class ScreenShareService extends Service {
    private static final String STOP = "su.familyserver.deskmax.STOP_SHARE";
    private static volatile ScreenShareService live;
    private volatile boolean ready, ending;
    private HandlerThread worker;
    private Handler capture;
    private MediaProjection projection;
    private VirtualDisplay display;
    private ImageReader reader;
    private SessionSocket socket;
    private int width, height, density;
    private long lastFrame;
    private DisplayManager displayManager;
    private final DisplayManager.DisplayListener rotationListener = new DisplayManager.DisplayListener() {
        @Override public void onDisplayAdded(int id) { }
        @Override public void onDisplayRemoved(int id) { }
        @Override public void onDisplayChanged(int id) {
            if (Build.VERSION.SDK_INT >= 34 || ending || id != android.view.Display.DEFAULT_DISPLAY) return;
            DisplayMetrics metrics = new DisplayMetrics();
            getSystemService(WindowManager.class).getDefaultDisplay().getRealMetrics(metrics);
            if (metrics.widthPixels != width || metrics.heightPixels != height) resize(metrics.widthPixels, metrics.heightPixels);
        }
    };
    public static boolean isRunning() { return live != null && !live.ending; }
    public static void begin(Context context, String baseUrl, DeskApi.Identity identity, String sessionId, int resultCode, Intent resultData) {
        Intent intent = new Intent(context, ScreenShareService.class).putExtra("base", baseUrl).putExtra("id", identity.id).putExtra("secret", identity.secret)
                .putExtra("session", sessionId).putExtra("result", resultCode).putExtra("data", resultData);
        context.startForegroundService(intent);
    }
    public static void requestStop(Context context) {
        RemoteAccessibilityService.setSessionActive(false);
        ScreenShareService service = live;
        if (service != null) service.finishSession();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null || STOP.equals(intent.getAction())) { finishSession(); return START_NOT_STICKY; }
        if (live != null) { return START_NOT_STICKY; }
        live = this;
        try {
            NotificationManager manager = getSystemService(NotificationManager.class);
            manager.createNotificationChannel(new NotificationChannel("screen_share", "Передача экрана", NotificationManager.IMPORTANCE_LOW));
            PendingIntent stop = PendingIntent.getService(this, 1, new Intent(this, ScreenShareService.class).setAction(STOP), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            PendingIntent open = PendingIntent.getActivity(this, 2, new Intent(this, MainActivity.class), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            Notification notification = new Notification.Builder(this, "screen_share").setSmallIcon(android.R.drawable.ic_menu_view)
                    .setContentTitle("DeskMax · экран передаётся").setContentText("Удалённый сеанс активен. Нажмите «Остановить» для отключения.")
                    .setContentIntent(open).setOngoing(true).addAction(new Notification.Action.Builder(null, "Остановить", stop).build()).build();
            if (Build.VERSION.SDK_INT >= 29) startForeground(23, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION); else startForeground(23, notification);
            worker = new HandlerThread("DeskMaxCapture"); worker.start(); capture = new Handler(worker.getLooper());
            Intent data = Build.VERSION.SDK_INT >= 33 ? intent.getParcelableExtra("data", Intent.class) : intent.getParcelableExtra("data");
            if (data == null || intent.getIntExtra("result", 0) != Activity.RESULT_OK) throw new IllegalArgumentException("Screen sharing requires consent");
            projection = getSystemService(MediaProjectionManager.class).getMediaProjection(Activity.RESULT_OK, data);
            projection.registerCallback(new MediaProjection.Callback() {
                @Override public void onStop() { finishSession(); }
                @Override public void onCapturedContentResize(int w, int h) {
                    if (!ending && w > 0 && h > 0 && (w != width || h != height)) resize(w, h);
                }
            }, capture);
            DisplayMetrics metrics = new DisplayMetrics(); getSystemService(WindowManager.class).getDefaultDisplay().getRealMetrics(metrics);
            density = metrics.densityDpi; width = metrics.widthPixels; height = metrics.heightPixels;
            reader = makeReader(width, height);
            // A MediaProjection token creates exactly one virtual display. Resize never reuses the token.
            display = projection.createVirtualDisplay("DeskMax", width, height, density, DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, reader.getSurface(), null, capture);
            displayManager = getSystemService(DisplayManager.class);
            displayManager.registerDisplayListener(rotationListener, capture);
            socket = new SessionSocket(intent.getStringExtra("base"), new DeskApi.Identity(intent.getStringExtra("id"), intent.getStringExtra("secret")), intent.getStringExtra("session"), true, new SessionSocket.Listener() {
                @Override public void onReady() { if (!ending) { ready = true; RemoteAccessibilityService.setSessionActive(true); } }
                @Override public void onFrame(byte[] ignored) { }
                @Override public void onInput(JSONObject input) { if (ready && !ending) RemoteAccessibilityService.apply(input); }
                @Override public void onClosed(String reason) { finishSession(); }
            });
            socket.start();
        } catch (Exception failure) { finishSession(); }
        return START_NOT_STICKY;
    }
    private ImageReader makeReader(int w, int h) {
        ImageReader result = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2);
        try { result.setOnImageAvailableListener(this::frame, capture); return result; }
        catch (RuntimeException failure) { result.close(); throw failure; }
    }
    private void resize(int w, int h) {
        if (display == null || ending) return;
        ImageReader replacement = null;
        try {
            RemoteAccessibilityService.apply(DeskApi.json("kind", "releaseAll"));
            replacement = makeReader(w, h);
            display.setSurface(null);
            display.resize(w, h, density);
            display.setSurface(replacement.getSurface());
            ImageReader previous = reader;
            reader = replacement;
            replacement = null;
            width = w; height = h;
            if (previous != null) previous.close();
        } catch (RuntimeException failure) { finishSession(); }
        finally { if (replacement != null) replacement.close(); }
    }
    private void frame(ImageReader source) {
        Image image = null; Bitmap padded = null, cropped = null, scaled = null;
        try {
            image = source.acquireLatestImage(); if (image == null) return;
            long now = SystemClock.elapsedRealtime(); if (ending || !ready || now - lastFrame < 100) return;
            lastFrame = now;
            Image.Plane plane = image.getPlanes()[0]; ByteBuffer bytes = plane.getBuffer();
            int stride = plane.getPixelStride(), row = plane.getRowStride(), w = image.getWidth(), h = image.getHeight();
            if (stride != 4) return;
            padded = Bitmap.createBitmap(row / stride, h, Bitmap.Config.ARGB_8888); padded.copyPixelsFromBuffer(bytes);
            cropped = Bitmap.createBitmap(padded, 0, 0, w, h);
            double scale = Math.min(1d, 1280d / Math.max(w, h));
            scaled = scale < 1 ? Bitmap.createScaledBitmap(cropped, Math.max(1, (int)(w * scale)), Math.max(1, (int)(h * scale)), true) : cropped;
            ByteArrayOutputStream encoded = new ByteArrayOutputStream(); scaled.compress(Bitmap.CompressFormat.JPEG, 55, encoded);
            SessionSocket current = socket; if (current != null && ready && !ending) current.sendFrame(encoded.toByteArray());
        } catch (RuntimeException failure) { finishSession(); }
        finally {
            if (image != null) image.close();
            if (scaled != null && scaled != cropped) scaled.recycle();
            if (cropped != null && cropped != padded) cropped.recycle();
            if (padded != null) padded.recycle();
        }
    }
    private synchronized void finishSession() {
        if (ending) return;
        ending = true; ready = false;
        RemoteAccessibilityService.setSessionActive(false);
        if (socket != null) { socket.stop(); socket = null; }
        Handler handler = capture;
        if (handler != null) handler.post(this::releaseCapture); else releaseCapture();
    }
    private void releaseCapture() {
        if (displayManager != null) { displayManager.unregisterDisplayListener(rotationListener); displayManager = null; }
        if (display != null) { display.release(); display = null; }
        if (reader != null) { reader.close(); reader = null; }
        if (projection != null) { MediaProjection previous = projection; projection = null; previous.stop(); }
        if (worker != null) { worker.quitSafely(); worker = null; }
        if (live == this) live = null;
        sendBroadcast(new Intent("su.familyserver.deskmax.SHARE_STOPPED").setPackage(getPackageName()));
        stopForeground(STOP_FOREGROUND_REMOVE); stopSelf();
    }
    @Override public void onDestroy() { finishSession(); super.onDestroy(); }
    @Override public void onTaskRemoved(Intent rootIntent) { finishSession(); super.onTaskRemoved(rootIntent); }
}
