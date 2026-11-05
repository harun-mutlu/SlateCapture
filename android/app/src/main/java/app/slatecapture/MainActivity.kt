package app.slatecapture

import android.Manifest
import android.app.Activity
import android.app.Application
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.drawable.GradientDrawable
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.View
import android.widget.Button
import android.widget.ProgressBar
import android.widget.TextView

class App : Application() {
    override fun onCreate() {
        super.onCreate()
        Link.hello = { Device.hello(this) }
    }
}

class MainActivity : Activity(), Link.Listener {

    companion object {
        var visible = false
            private set
        private const val REQUEST_CAPTURE = 1
        private const val REQUEST_PERMISSIONS = 2
    }

    private lateinit var status: TextView
    private lateinit var dot: View
    private lateinit var spinner: ProgressBar
    private lateinit var hint: TextView
    private lateinit var start: Button
    private lateinit var stop: Button
    private lateinit var home: Button
    private lateinit var info: TextView

    private val handler = Handler(Looper.getMainLooper())
    private val ticker = object : Runnable {
        override fun run() {
            render()
            if (CaptureService.running) handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        status = findViewById(R.id.status)
        dot = findViewById(R.id.dot)
        spinner = findViewById(R.id.spinner)
        hint = findViewById(R.id.hint)
        start = findViewById(R.id.start)
        stop = findViewById(R.id.stop)
        home = findViewById(R.id.home)
        info = findViewById(R.id.info)

        start.setOnClickListener { begin() }
        stop.setOnClickListener { CaptureService.stop(this) }
        home.setOnClickListener { moveTaskToBack(true) }
    }

    override fun onStart() {
        super.onStart()
        visible = true
        Link.start(this)
        Link.addListener(this)
        CaptureService.onChange = { handler.post { tick() } }
        tick()
    }

    override fun onStop() {
        visible = false
        Link.removeListener(this)
        CaptureService.onChange = null
        handler.removeCallbacks(ticker)
        if (!CaptureService.running) Link.stop()
        super.onStop()
    }

    override fun onLinkState(state: Link.State) = render()

    private fun tick() {
        handler.removeCallbacks(ticker)
        ticker.run()
    }

    private fun begin() {
        if (Build.VERSION.SDK_INT < 23) {
            askCapture()
            return
        }
        val missing = ArrayList<String>()
        if (Device.audioCapture && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.RECORD_AUDIO)
        }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.POST_NOTIFICATIONS)
        }
        val prefs = getPreferences(MODE_PRIVATE)
        if (missing.isNotEmpty() && !prefs.getBoolean("asked", false)) {
            prefs.edit().putBoolean("asked", true).apply()
            requestPermissions(missing.toTypedArray(), REQUEST_PERMISSIONS)
            return
        }
        askCapture()
    }

    private fun askCapture() {
        val manager = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        @Suppress("DEPRECATION")
        startActivityForResult(manager.createScreenCaptureIntent(), REQUEST_CAPTURE)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        if (requestCode == REQUEST_PERMISSIONS) askCapture()
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (requestCode != REQUEST_CAPTURE) return
        if (resultCode == RESULT_OK && data != null) CaptureService.start(this, resultCode, data)
    }

    private fun render() {
        val link = Link.state
        val streaming = CaptureService.running
        val connected = link == Link.State.CONNECTED

        spinner.visibility = if (connected) View.GONE else View.VISIBLE
        dot.visibility = if (connected) View.VISIBLE else View.GONE
        start.visibility = if (connected && !streaming) View.VISIBLE else View.GONE
        stop.visibility = if (streaming) View.VISIBLE else View.GONE
        home.visibility = if (streaming && connected) View.VISIBLE else View.GONE
        hint.visibility = if (link == Link.State.SEARCHING) View.VISIBLE else View.GONE

        status.text = when {
            link == Link.State.LOST -> getString(R.string.status_lost)
            !connected -> getString(R.string.status_searching)
            streaming -> getString(R.string.status_streaming, elapsed())
            else -> getString(R.string.status_connected, Link.host ?: "")
        }
        status.setTextColor(color(if (streaming || link == Link.State.LOST) R.color.red_text else if (connected) R.color.green_text else R.color.text))
        (dot.background as? GradientDrawable)?.setColor(color(if (streaming) R.color.red else R.color.green))

        info.text = when {
            streaming -> CaptureService.info
            connected -> levelInfo()
            else -> ""
        }
        info.visibility = if (info.text.isEmpty()) View.GONE else View.VISIBLE
    }

    @Suppress("DEPRECATION")
    private fun color(id: Int) = resources.getColor(id)

    private fun levelInfo(): String {
        val level = Device.levels(this).first()
        val audio = if (Device.audioCapture) "" else getString(R.string.no_audio)
        return getString(R.string.stream_info, level.width, level.height, level.maxFps) + audio
    }

    private fun elapsed(): String {
        val s = (SystemClock.elapsedRealtime() - CaptureService.startedAt) / 1000
        return "%02d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60)
    }
}
