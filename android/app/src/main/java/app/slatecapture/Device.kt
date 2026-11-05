package app.slatecapture

import android.content.Context
import android.graphics.Point
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.util.DisplayMetrics
import android.view.WindowManager
import org.json.JSONArray
import org.json.JSONObject

class Level(val width: Int, val height: Int, val maxFps: Int)

object Device {
    const val VIDEO_MIME = MediaFormat.MIMETYPE_VIDEO_AVC

    val audioCapture: Boolean get() = Build.VERSION.SDK_INT >= 29

    private var cachedLevels: List<Level>? = null
    private var cachedEncoder: MediaCodecInfo? = null

    val name: String
        get() {
            val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
            val model = Build.MODEL
            return if (model.startsWith(maker, ignoreCase = true)) model else "$maker $model"
        }

    fun encoder(): MediaCodecInfo? {
        cachedEncoder?.let { return it }
        val all = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            info.isEncoder && info.supportedTypes.any { it.equals(VIDEO_MIME, ignoreCase = true) }
        }
        val pick = all.firstOrNull { isHardware(it) } ?: all.firstOrNull()
        cachedEncoder = pick
        return pick
    }

    private fun isHardware(info: MediaCodecInfo): Boolean {
        if (Build.VERSION.SDK_INT >= 29) return info.isHardwareAccelerated
        val n = info.name.lowercase()
        return !n.startsWith("omx.google.") && !n.startsWith("c2.android.")
    }

    fun screen(context: Context): Pair<Point, Int> {
        val wm = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        val metrics = DisplayMetrics()
        @Suppress("DEPRECATION")
        wm.defaultDisplay.getRealMetrics(metrics)
        return Point(metrics.widthPixels, metrics.heightPixels) to metrics.densityDpi
    }

    fun levels(context: Context): List<Level> {
        cachedLevels?.let { return it }
        val size = screen(context).first
        val long = maxOf(size.x, size.y)
        val short = minOf(size.x, size.y)
        val caps = encoder()?.getCapabilitiesForType(VIDEO_MIME)?.videoCapabilities
        val wa = caps?.widthAlignment ?: 2
        val ha = caps?.heightAlignment ?: 2

        val list = ArrayList<Level>()
        for (scale in floatArrayOf(1f, 0.75f, 0.5f)) {
            val w = (long * scale).toInt() / wa * wa
            val h = (short * scale).toInt() / ha * ha
            if (list.any { it.width == w }) continue
            val fps = when {
                caps == null -> 30
                caps.areSizeAndRateSupported(w, h, 60.0) -> 60
                caps.areSizeAndRateSupported(w, h, 30.0) -> 30
                else -> 0
            }
            if (fps > 0) list.add(Level(w, h, fps))
        }
        if (list.isEmpty()) list.add(Level(long / 2 / wa * wa, short / 2 / ha * ha, 30))

        val best = list.indexOfFirst { it.maxFps >= 60 }
        if (best > 0) list.add(0, list.removeAt(best))
        cachedLevels = list
        return list
    }

    fun hello(context: Context): ByteArray {
        val levels = JSONArray()
        for (l in levels(context)) {
            levels.put(JSONObject().put("w", l.width).put("h", l.height).put("fps", l.maxFps))
        }
        return JSONObject()
            .put("name", name)
            .put("os", "Android " + Build.VERSION.RELEASE)
            .put("sdk", Build.VERSION.SDK_INT)
            .put("audio", audioCapture)
            .put("encoder", encoder()?.name ?: "")
            .put("version", BuildConfig.VERSION_NAME)
            .put("levels", levels)
            .toString()
            .toByteArray(Charsets.UTF_8)
    }
}
