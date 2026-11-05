package app.slatecapture

import android.annotation.SuppressLint
import android.annotation.TargetApi
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import java.nio.ByteBuffer

@TargetApi(29)
class AudioEncoder(private val projection: MediaProjection) {
    private companion object {
        const val RATE = 48_000
        const val CHANNELS = 2
        const val BYTES_PER_FRAME = 4
    }

    private var record: AudioRecord? = null
    private var codec: MediaCodec? = null
    private var thread: Thread? = null
    private var csd: ByteArray? = null

    @Volatile private var running = false

    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        try {
            val capture = AudioPlaybackCaptureConfiguration.Builder(projection)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build()
            val pcm = AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                .build()
            val min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            val r = AudioRecord.Builder()
                .setAudioFormat(pcm)
                .setBufferSizeInBytes(maxOf(min * 2, 16384))
                .setAudioPlaybackCaptureConfig(capture)
                .build()
            record = r

            val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, RATE, CHANNELS).apply {
                setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
                setInteger(MediaFormat.KEY_BIT_RATE, 128_000)
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 16384)
            }
            val c = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
            codec = c
            c.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            c.start()
            r.startRecording()
        } catch (_: Exception) {
            release()
            return false
        }
        running = true
        thread = Thread(::run, "audio").apply { start() }
        return true
    }

    fun resendFormat() {
        csd?.let { Link.send(Proto.AUDIO_FORMAT, formatPacket(it)) }
    }

    fun release() {
        running = false
        thread?.join(500)
        thread = null
        record?.let {
            try {
                it.stop()
            } catch (_: IllegalStateException) {
            }
            it.release()
        }
        record = null
        codec?.let {
            try {
                it.stop()
            } catch (_: IllegalStateException) {
            }
            it.release()
        }
        codec = null
    }

    private fun run() {
        val c = codec ?: return
        val r = record ?: return
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                val inIndex = c.dequeueInputBuffer(10_000)
                if (inIndex >= 0) {
                    val buf = c.getInputBuffer(inIndex)
                    if (buf != null) {
                        buf.clear()
                        val n = maxOf(r.read(buf, buf.capacity()), 0)
                        val latencyUs = n.toLong() / BYTES_PER_FRAME * 1_000_000L / RATE
                        c.queueInputBuffer(inIndex, 0, n, System.nanoTime() / 1000 - latencyUs, 0)
                    }
                }
                drain(c, info)
            }
        } catch (_: IllegalStateException) {
        }
    }

    private fun drain(c: MediaCodec, info: MediaCodec.BufferInfo) {
        while (true) {
            val index = c.dequeueOutputBuffer(info, 0)
            if (index < 0) return
            val buf = c.getOutputBuffer(index)
            if (buf != null && info.size > 0) {
                buf.position(info.offset)
                buf.limit(info.offset + info.size)
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                    val bytes = ByteArray(info.size)
                    buf.get(bytes)
                    csd = bytes
                    Link.send(Proto.AUDIO_FORMAT, formatPacket(bytes))
                } else {
                    val out = ByteArray(8 + info.size)
                    ByteBuffer.wrap(out).putLong(info.presentationTimeUs)
                    buf.get(out, 8, info.size)
                    Link.send(Proto.AUDIO, out)
                }
            }
            c.releaseOutputBuffer(index, false)
        }
    }

    private fun formatPacket(csd: ByteArray): ByteArray {
        val out = ByteArray(8 + csd.size)
        ByteBuffer.wrap(out).putInt(RATE).putInt(CHANNELS).put(csd)
        return out
    }
}
