package app.slatecapture

import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.os.Build
import android.os.Bundle
import android.view.Surface
import java.nio.ByteBuffer

class ScreenEncoder(private val projection: MediaProjection, private val dpi: Int) {
    var width = 0
        private set
    var height = 0
        private set
    var fps = 0
        private set

    private var display: VirtualDisplay? = null
    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    private var thread: Thread? = null
    private var csd: ByteArray? = null

    @Volatile private var running = false

    fun start(w: Int, h: Int, fps: Int, bitrate: Int) {
        stopCodec()
        val format = MediaFormat.createVideoFormat(Device.VIDEO_MIME, w, h).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 1_000_000L / fps)
            if (Build.VERSION.SDK_INT >= 23) setInteger(MediaFormat.KEY_PRIORITY, 0)
            if (Build.VERSION.SDK_INT >= 29) setFloat(MediaFormat.KEY_MAX_FPS_TO_ENCODER, fps.toFloat())
        }

        val name = Device.encoder()?.name
        val c = if (name != null) MediaCodec.createByCodecName(name) else MediaCodec.createEncoderByType(Device.VIDEO_MIME)
        try {
            c.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        } catch (e: Exception) {
            c.release()
            throw e
        }
        val s = c.createInputSurface()
        c.start()

        val d = display
        if (d == null) {
            display = projection.createVirtualDisplay(
                "SlateCapture", w, h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, s, null, null,
            )
        } else {
            d.resize(w, h, dpi)
            d.surface = s
        }

        codec = c
        surface = s
        width = w
        height = h
        this.fps = fps
        csd = null
        running = true
        thread = Thread({ drain(c) }, "video").apply { start() }
    }

    fun requestKeyframe() {
        try {
            codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
        } catch (_: IllegalStateException) {
        }
    }

    fun resendFormat() {
        csd?.let { Link.send(Proto.VIDEO_FORMAT, formatPacket(it)) }
        requestKeyframe()
    }

    fun release() {
        stopCodec()
        display?.release()
        display = null
    }

    private fun stopCodec() {
        display?.surface = null
        running = false
        thread?.join(500)
        thread = null
        codec?.let {
            try {
                it.stop()
            } catch (_: IllegalStateException) {
            }
            it.release()
        }
        codec = null
        surface?.release()
        surface = null
    }

    private fun drain(c: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                val index = c.dequeueOutputBuffer(info, 50_000)
                if (index < 0) continue
                val buf = c.getOutputBuffer(index)
                if (buf != null && info.size > 0) {
                    buf.position(info.offset)
                    buf.limit(info.offset + info.size)
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                        val bytes = ByteArray(info.size)
                        buf.get(bytes)
                        csd = bytes
                        Link.send(Proto.VIDEO_FORMAT, formatPacket(bytes))
                    } else {
                        val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                        val out = ByteArray(9 + info.size)
                        ByteBuffer.wrap(out).putLong(info.presentationTimeUs).put(if (key) 1 else 0)
                        buf.get(out, 9, info.size)
                        Link.sendVideo(out, key)
                    }
                }
                c.releaseOutputBuffer(index, false)
            }
        } catch (_: IllegalStateException) {
        }
    }

    private fun formatPacket(csd: ByteArray): ByteArray {
        val out = ByteArray(12 + csd.size)
        ByteBuffer.wrap(out).putInt(width).putInt(height).putInt(fps).put(csd)
        return out
    }
}
