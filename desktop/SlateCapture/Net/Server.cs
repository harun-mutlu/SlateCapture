using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SlateCapture.Net;

public sealed record Level(int Width, int Height, int MaxFps)
{
    public string Size => $"{Width}×{Height}";
}

public sealed record Hello(string Name, string Os, int Sdk, bool Audio, string Encoder, IReadOnlyList<Level> Levels);

public sealed record VideoFormat(int Width, int Height, int Fps, byte[] Csd);

public sealed record AudioFormat(int SampleRate, int Channels, byte[] Csd);

public sealed record StreamState(bool Streaming, int Width, int Height, int Fps, bool Audio, bool AudioFailed);

public sealed record StreamConfig(int Level, int Fps, int Bitrate, bool Audio);

public sealed class Server : IDisposable
{
    public const int DiscoveryPort = 47900;
    public const int StreamPort = 47901;

    const byte HelloType = 1, ConfigType = 2, StateType = 3, KeyframeType = 4;
    const byte VideoFormatType = 16, VideoType = 17, AudioFormatType = 32, AudioType = 33, HeartbeatType = 48;

    public event Action<Hello>? Connected;
    public event Action? Disconnected;
    public event Action<StreamState>? StateChanged;
    public event Action<VideoFormat>? VideoFormatReceived;
    public event Action<byte[], int, int, long, bool>? VideoReceived;
    public event Action<AudioFormat>? AudioFormatReceived;
    public event Action<byte[], int, int, long>? AudioReceived;
    public event Action<int>? PortBusy;

    readonly CancellationTokenSource _cts = new();
    readonly object _sendLock = new();
    UdpClient? _udp;
    TcpListener? _tcp;
    TcpClient? _client;
    NetworkStream? _stream;

    public bool IsConnected => _stream != null;

    public void Start()
    {
        new Thread(DiscoveryLoop) { IsBackground = true, Name = "discovery" }.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "accept" }.Start();
        new Thread(HeartbeatLoop) { IsBackground = true, Name = "heartbeat" }.Start();
    }

    public void SendConfig(StreamConfig c)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["level"] = c.Level,
            ["fps"] = c.Fps,
            ["bitrate"] = c.Bitrate,
            ["audio"] = c.Audio,
        });
        Send(ConfigType, json);
    }

    public void RequestKeyframe() => Send(KeyframeType, []);

    void Send(byte type, byte[] data)
    {
        var stream = _stream;
        if (stream == null) return;
        var header = new byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), data.Length);
        try
        {
            lock (_sendLock)
            {
                stream.Write(header);
                if (data.Length > 0) stream.Write(data);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    void DiscoveryLoop()
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true };
        }
        catch (SocketException)
        {
            PortBusy?.Invoke(DiscoveryPort);
            return;
        }

        var answer = Encoding.UTF8.GetBytes($"SLATECAP! {StreamPort} {Environment.MachineName}");
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                var data = _udp.Receive(ref remote);
                if (Encoding.UTF8.GetString(data).Trim() == "SLATECAP?")
                    _udp.Send(answer, answer.Length, remote);
            }
            catch (SocketException) when (!_cts.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    void AcceptLoop()
    {
        try
        {
            _tcp = new TcpListener(IPAddress.Any, StreamPort);
            _tcp.Start();
        }
        catch (SocketException)
        {
            PortBusy?.Invoke(StreamPort);
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _tcp.AcceptTcpClient();
            }
            catch (Exception)
            {
                return;
            }
            DropClient();
            client.NoDelay = true;
            client.ReceiveBufferSize = 1 << 20;
            client.ReceiveTimeout = 6000;
            _client = client;
            new Thread(() => ReadLoop(client)) { IsBackground = true, Name = "stream" }.Start();
        }
    }

    void HeartbeatLoop()
    {
        while (!_cts.Token.WaitHandle.WaitOne(2000))
            Send(HeartbeatType, []);
    }

    void ReadLoop(TcpClient client)
    {
        var stream = client.GetStream();
        var header = new byte[5];
        var buffer = new byte[1 << 20];
        var hello = false;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                stream.ReadExactly(header);
                int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (len < 0 || len > 64 << 20) break;
                if (len > buffer.Length) buffer = new byte[len];
                stream.ReadExactly(buffer, 0, len);

                switch (header[0])
                {
                    case HelloType:
                        _stream = stream;
                        hello = true;
                        Connected?.Invoke(ParseHello(buffer, len));
                        break;
                    case StateType:
                        StateChanged?.Invoke(ParseState(buffer, len));
                        break;
                    case VideoFormatType:
                        VideoFormatReceived?.Invoke(new VideoFormat(
                            BinaryPrimitives.ReadInt32BigEndian(buffer),
                            BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4)),
                            BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(8)),
                            buffer.AsSpan(12, len - 12).ToArray()));
                        break;
                    case VideoType:
                        VideoReceived?.Invoke(buffer, 9, len - 9, BinaryPrimitives.ReadInt64BigEndian(buffer), buffer[8] == 1);
                        break;
                    case AudioFormatType:
                        AudioFormatReceived?.Invoke(new AudioFormat(
                            BinaryPrimitives.ReadInt32BigEndian(buffer),
                            BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4)),
                            buffer.AsSpan(8, len - 8).ToArray()));
                        break;
                    case AudioType:
                        AudioReceived?.Invoke(buffer, 8, len - 8, BinaryPrimitives.ReadInt64BigEndian(buffer));
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException or InvalidOperationException)
        {
        }

        client.Dispose();
        if (ReferenceEquals(_client, client))
        {
            _client = null;
            _stream = null;
            if (hello) Disconnected?.Invoke();
        }
    }

    static Hello ParseHello(byte[] buffer, int len)
    {
        using var doc = JsonDocument.Parse(buffer.AsMemory(0, len));
        var root = doc.RootElement;
        var levels = new List<Level>();
        if (root.TryGetProperty("levels", out var arr))
        {
            foreach (var l in arr.EnumerateArray())
                levels.Add(new Level(l.GetProperty("w").GetInt32(), l.GetProperty("h").GetInt32(), l.GetProperty("fps").GetInt32()));
        }
        return new Hello(
            Str(root, "name"),
            Str(root, "os"),
            root.TryGetProperty("sdk", out var sdk) ? sdk.GetInt32() : 0,
            root.TryGetProperty("audio", out var audio) && audio.GetBoolean(),
            Str(root, "encoder"),
            levels);
    }

    static StreamState ParseState(byte[] buffer, int len)
    {
        using var doc = JsonDocument.Parse(buffer.AsMemory(0, len));
        var r = doc.RootElement;
        return new StreamState(
            Bool(r, "streaming"),
            r.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            r.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            r.TryGetProperty("fps", out var f) ? f.GetInt32() : 0,
            Bool(r, "audio"),
            Bool(r, "audioFailed"));
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    void DropClient()
    {
        var old = _client;
        _client = null;
        _stream = null;
        old?.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _tcp?.Stop();
        DropClient();
    }
}
