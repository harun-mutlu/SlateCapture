package app.slatecapture

import org.json.JSONObject

object Proto {
    const val DISCOVERY_PORT = 47900
    const val ASK = "SLATECAP?"
    const val ANSWER = "SLATECAP!"

    const val HELLO = 1
    const val CONFIG = 2
    const val STATE = 3
    const val KEYFRAME = 4
    const val VIDEO_FORMAT = 16
    const val VIDEO = 17
    const val AUDIO_FORMAT = 32
    const val AUDIO = 33
    const val HEARTBEAT = 48
}

class Packet(val type: Int, val data: ByteArray, val media: Boolean)

data class StreamConfig(
    val level: Int = 0,
    val fps: Int = 60,
    val bitrate: Int = 8_000_000,
    val audio: Boolean = false,
) {
    companion object {
        fun parse(json: String): StreamConfig {
            val o = JSONObject(json)
            return StreamConfig(
                level = o.optInt("level", 0),
                fps = o.optInt("fps", 60),
                bitrate = o.optInt("bitrate", 8_000_000),
                audio = o.optBoolean("audio", false),
            )
        }
    }
}
