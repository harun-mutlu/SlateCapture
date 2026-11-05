package app.slatecapture

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.TimeUnit

object Link {
    enum class State { IDLE, SEARCHING, CONNECTED, LOST }

    interface Listener {
        fun onLinkState(state: State) {}
        fun onConfig(config: StreamConfig) {}
    }

    private const val MAX_QUEUE = 90
    private val EMPTY = ByteArray(0)

    @Volatile var state = State.IDLE
        private set
    @Volatile var host: String? = null
        private set
    @Volatile var config = StreamConfig()
        private set

    var hello: (() -> ByteArray)? = null
    var onConnected: (() -> Unit)? = null
    var onKeyframeRequest: (() -> Unit)? = null

    private val main = Handler(Looper.getMainLooper())
    private val listeners = ArrayList<Listener>()
    private val queue = LinkedBlockingDeque<Packet>()

    @Volatile private var running = false
    @Volatile private var skipping = false
    @Volatile private var socket: Socket? = null
    private var thread: Thread? = null
    private var wifi: WifiManager? = null
    private var lastHost: InetAddress? = null

    fun addListener(l: Listener) {
        listeners.add(l)
        l.onLinkState(state)
    }

    fun removeListener(l: Listener) {
        listeners.remove(l)
    }

    fun start(context: Context) {
        if (running) return
        wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        running = true
        thread = Thread(::loop, "link").apply {
            isDaemon = true
            start()
        }
    }

    fun stop() {
        running = false
        closeSocket()
        thread?.interrupt()
        thread = null
        setState(State.IDLE)
    }

    fun send(type: Int, data: ByteArray) {
        if (state != State.CONNECTED) return
        queue.offer(Packet(type, data, false))
    }

    fun sendVideo(data: ByteArray, key: Boolean) {
        if (state != State.CONNECTED) return
        if (key) {
            skipping = false
        } else if (skipping) {
            return
        }
        if (queue.size > MAX_QUEUE) {
            dropMedia()
            skipping = true
            onKeyframeRequest?.invoke()
            if (!key) return
        }
        queue.offer(Packet(Proto.VIDEO, data, true))
    }

    private fun dropMedia() {
        val it = queue.iterator()
        while (it.hasNext()) {
            if (it.next().media) it.remove()
        }
    }

    private fun loop() {
        var wasConnected = false
        while (running) {
            if (!wasConnected) setState(State.SEARCHING)
            val found = find() ?: continue
            try {
                session(found.first, found.second, found.third)
            } catch (_: IOException) {
            }
            closeSocket()
            if (state == State.CONNECTED) {
                wasConnected = true
                setState(State.LOST)
            }
        }
    }

    private fun find(): Triple<InetAddress, Int, String>? {
        val lock = wifi?.createMulticastLock("slatecapture")?.apply { setReferenceCounted(false) }
        try {
            lock?.acquire()
            DatagramSocket().use { sock ->
                sock.broadcast = true
                sock.soTimeout = 1000
                val ask = Proto.ASK.toByteArray()
                for (target in targets()) {
                    try {
                        sock.send(DatagramPacket(ask, ask.size, target, Proto.DISCOVERY_PORT))
                    } catch (_: IOException) {
                    }
                }
                val buf = ByteArray(256)
                val reply = DatagramPacket(buf, buf.size)
                val deadline = System.currentTimeMillis() + 1000
                while (System.currentTimeMillis() < deadline) {
                    try {
                        sock.receive(reply)
                    } catch (_: SocketTimeoutException) {
                        return null
                    }
                    val text = String(reply.data, 0, reply.length, Charsets.UTF_8).trim()
                    if (!text.startsWith(Proto.ANSWER)) continue
                    val parts = text.split(' ', limit = 3)
                    val port = parts.getOrNull(1)?.toIntOrNull() ?: continue
                    return Triple(reply.address, port, parts.getOrNull(2) ?: reply.address.hostAddress ?: "")
                }
            }
        } catch (_: IOException) {
            sleep(1000)
        } finally {
            if (lock?.isHeld == true) lock.release()
        }
        return null
    }

    private fun targets(): List<InetAddress> {
        val list = ArrayList<InetAddress>()
        lastHost?.let { list.add(it) }
        list.add(InetAddress.getByName("255.255.255.255"))
        val dhcp = wifi?.dhcpInfo
        if (dhcp != null && dhcp.ipAddress != 0 && dhcp.netmask != 0) {
            val b = (dhcp.ipAddress and dhcp.netmask) or dhcp.netmask.inv()
            val bytes = byteArrayOf(b.toByte(), (b shr 8).toByte(), (b shr 16).toByte(), (b shr 24).toByte())
            list.add(InetAddress.getByAddress(bytes))
        }
        return list
    }

    private fun session(address: InetAddress, port: Int, name: String) {
        val s = Socket()
        s.tcpNoDelay = true
        s.connect(InetSocketAddress(address, port), 2000)
        s.soTimeout = 6000
        socket = s
        lastHost = address

        val out = DataOutputStream(BufferedOutputStream(s.getOutputStream(), 64 * 1024))
        val input = DataInputStream(BufferedInputStream(s.getInputStream()))
        queue.clear()
        skipping = false
        write(out, Proto.HELLO, hello?.invoke() ?: EMPTY)
        out.flush()

        host = name
        setState(State.CONNECTED)
        val writer = Thread({ writeLoop(out) }, "link-out").apply {
            isDaemon = true
            start()
        }
        onConnected?.invoke()

        try {
            while (running) {
                val type = input.readUnsignedByte()
                val len = input.readInt()
                if (len < 0 || len > 1 shl 20) throw IOException("bad packet")
                val data = ByteArray(len)
                input.readFully(data)
                when (type) {
                    Proto.CONFIG -> {
                        val c = StreamConfig.parse(String(data, Charsets.UTF_8))
                        config = c
                        main.post { listeners.toList().forEach { it.onConfig(c) } }
                    }
                    Proto.KEYFRAME -> onKeyframeRequest?.invoke()
                }
            }
        } finally {
            writer.interrupt()
        }
    }

    private fun writeLoop(out: DataOutputStream) {
        try {
            while (true) {
                val p = queue.poll(2, TimeUnit.SECONDS)
                if (p == null) {
                    write(out, Proto.HEARTBEAT, EMPTY)
                    out.flush()
                    continue
                }
                write(out, p.type, p.data)
                if (queue.isEmpty()) out.flush()
            }
        } catch (_: InterruptedException) {
        } catch (_: IOException) {
            closeSocket()
        }
    }

    private fun write(out: DataOutputStream, type: Int, data: ByteArray) {
        out.writeByte(type)
        out.writeInt(data.size)
        out.write(data)
    }

    private fun closeSocket() {
        try {
            socket?.close()
        } catch (_: IOException) {
        }
        socket = null
    }

    private fun setState(s: State) {
        if (state == s) return
        state = s
        main.post { listeners.toList().forEach { it.onLinkState(s) } }
    }

    private fun sleep(ms: Long) {
        try {
            Thread.sleep(ms)
        } catch (_: InterruptedException) {
        }
    }
}
