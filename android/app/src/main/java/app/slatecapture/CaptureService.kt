package app.slatecapture

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.hardware.display.DisplayManager
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.view.Display
import org.json.JSONObject

class CaptureService : Service(), Link.Listener, DisplayManager.DisplayListener {

    companion object {
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        const val ACTION_STOP = "app.slatecapture.STOP"
        private const val CHANNEL = "capture"
        private const val NOTIFICATION_ID = 1

        @Volatile var running = false
            private set
        var startedAt = 0L
            private set
        var info = ""
            private set
        var onChange: (() -> Unit)? = null

        fun start(context: Context, code: Int, data: Intent) {
            val intent = Intent(context, CaptureService::class.java)
                .putExtra(EXTRA_CODE, code)
                .putExtra(EXTRA_DATA, data)
            if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent) else context.startService(intent)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, CaptureService::class.java))
        }
    }

    private val handler = Handler(Looper.getMainLooper())
    private var projection: MediaProjection? = null
    private var video: ScreenEncoder? = null
    private var audio: AudioEncoder? = null
    private var applied: StreamConfig? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent == null || intent.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (projection != null) return START_NOT_STICKY

        goForeground()
        val code = intent.getIntExtra(EXTRA_CODE, 0)
        @Suppress("DEPRECATION")
        val data = intent.getParcelableExtra<Intent>(EXTRA_DATA)
        val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val p = data?.let { manager.getMediaProjection(code, it) }
        if (p == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                handler.post { stopSelf() }
            }
        }, handler)
        projection = p
        video = ScreenEncoder(p, Device.screen(this).second)

        running = true
        startedAt = SystemClock.elapsedRealtime()
        Link.onKeyframeRequest = { video?.requestKeyframe() }
        Link.onConnected = { handler.post { announce() } }
        Link.addListener(this)
        (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).registerDisplayListener(this, handler)

        apply(Link.config)
        onChange?.invoke()
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        running = false
        Link.send(Proto.STATE, JSONObject().put("streaming", false).toString().toByteArray())
        Link.removeListener(this)
        Link.onKeyframeRequest = null
        Link.onConnected = null
        (getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).unregisterDisplayListener(this)
        audio?.release()
        audio = null
        video?.release()
        video = null
        projection?.stop()
        projection = null
        if (!MainActivity.visible) Link.stop()
        onChange?.invoke()
        super.onDestroy()
    }

    override fun onConfig(config: StreamConfig) {
        if (config != applied) apply(config)
    }

    override fun onDisplayAdded(displayId: Int) {}
    override fun onDisplayRemoved(displayId: Int) {}

    override fun onDisplayChanged(displayId: Int) {
        if (displayId != Display.DEFAULT_DISPLAY) return
        val v = video ?: return
        val size = Device.screen(this).first
        if ((size.x > size.y) != (v.width > v.height)) applied?.let { apply(it, force = true) }
    }

    private fun apply(config: StreamConfig, force: Boolean = false) {
        val v = video ?: return
        val old = applied
        val levels = Device.levels(this)
        val videoChanged = force || old == null || old.level != config.level || old.fps != config.fps || old.bitrate != config.bitrate
        applied = config

        if (videoChanged) {
            val size = Device.screen(this).first
            val portrait = size.y > size.x
            var index = config.level.coerceIn(0, levels.size - 1)
            while (true) {
                val level = levels[index]
                val fps = minOf(config.fps, level.maxFps)
                val w = if (portrait) level.height else level.width
                val h = if (portrait) level.width else level.height
                try {
                    v.start(w, h, fps, config.bitrate)
                    break
                } catch (e: Exception) {
                    if (index == levels.size - 1) {
                        stopSelf()
                        return
                    }
                    index++
                }
            }
        }

        val wantAudio = config.audio && Device.audioCapture
        if (wantAudio && audio == null) {
            projection?.let { p ->
                val a = AudioEncoder(p)
                if (a.start()) audio = a
            }
        } else if (!wantAudio && audio != null) {
            audio?.release()
            audio = null
        }

        info = getString(
            R.string.stream_info, v.width, v.height, v.fps,
        ) + if (audio != null) getString(R.string.stream_info_audio) else ""
        sendState()
        onChange?.invoke()
    }

    private fun announce() {
        video?.resendFormat()
        audio?.resendFormat()
        sendState()
    }

    private fun sendState() {
        val v = video ?: return
        val state = JSONObject()
            .put("streaming", true)
            .put("width", v.width)
            .put("height", v.height)
            .put("fps", v.fps)
            .put("audio", audio != null)
            .put("audioFailed", applied?.audio == true && Device.audioCapture && audio == null)
        Link.send(Proto.STATE, state.toString().toByteArray())
    }

    private fun goForeground() {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW),
            )
        }
        val immutable = if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), immutable or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, CaptureService::class.java).setAction(ACTION_STOP),
            immutable or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val builder = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CHANNEL) else Notification.Builder(this)
        @Suppress("DEPRECATION")
        val notification = builder
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(getString(R.string.notification_text))
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(0, getString(R.string.stop), stop).build())
            .build()

        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }
}
