using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SlateCapture.Net;

namespace SlateCapture.Media;

public sealed unsafe class Mp4Recorder : IDisposable
{
    static readonly AVRational Micro = new() { num = 1, den = 1_000_000 };

    readonly AVFormatContext* _ctx;
    readonly AVStream* _video;
    readonly AVStream* _audio;
    readonly AVPacket* _packet;
    long _base = long.MinValue;
    long _lastVideo = long.MinValue;
    long _lastAudio = long.MinValue;
    bool _waitKey = true;
    bool _closed;

    public string Path { get; }
    public VideoFormat Format { get; }
    public long Bytes { get; private set; }
    public bool HasAudio => _audio != null;

    public Mp4Recorder(string path, VideoFormat video, AudioFormat? audio)
    {
        Path = path;
        Format = video;

        AVFormatContext* ctx = null;
        ffmpeg.avformat_alloc_output_context2(&ctx, null, "mp4", path).Check("avformat_alloc_output_context2");
        _ctx = ctx;

        _video = ffmpeg.avformat_new_stream(_ctx, null);
        var vp = _video->codecpar;
        vp->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        vp->codec_id = AVCodecID.AV_CODEC_ID_H264;
        vp->width = video.Width;
        vp->height = video.Height;
        Codec.SetExtradata(vp, video.Csd);
        _video->time_base = new AVRational { num = 1, den = 90_000 };
        _video->avg_frame_rate = new AVRational { num = video.Fps, den = 1 };

        if (audio != null)
        {
            _audio = ffmpeg.avformat_new_stream(_ctx, null);
            var ap = _audio->codecpar;
            ap->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
            ap->codec_id = AVCodecID.AV_CODEC_ID_AAC;
            ap->sample_rate = audio.SampleRate;
            ap->frame_size = 1024;
            ffmpeg.av_channel_layout_default(&ap->ch_layout, audio.Channels);
            Codec.SetExtradata(ap, audio.Csd);
            _audio->time_base = new AVRational { num = 1, den = audio.SampleRate };
        }

        ffmpeg.avio_open(&_ctx->pb, path, ffmpeg.AVIO_FLAG_WRITE).Check("avio_open");
        ffmpeg.avformat_write_header(_ctx, null).Check("avformat_write_header");
        _packet = ffmpeg.av_packet_alloc();
    }

    public void Resync() => _waitKey = true;

    public void WriteVideo(byte[] data, int offset, int length, long ptsUs, bool key)
    {
        if (_closed) return;
        if (_waitKey)
        {
            if (!key) return;
            _waitKey = false;
            if (_base == long.MinValue) _base = ptsUs;
        }
        Write(_video, data, offset, length, ptsUs - _base, key, ref _lastVideo);
    }

    public void WriteAudio(byte[] data, int offset, int length, long ptsUs)
    {
        if (_closed || _audio == null || _base == long.MinValue || _waitKey) return;
        long t = ptsUs - _base;
        if (t < 0) return;
        Write(_audio, data, offset, length, t, true, ref _lastAudio);
    }

    void Write(AVStream* stream, byte[] data, int offset, int length, long us, bool key, ref long last)
    {
        long ts = ffmpeg.av_rescale_q(us, Micro, stream->time_base);
        if (ts <= last) ts = last + 1;
        last = ts;

        if (ffmpeg.av_new_packet(_packet, length) < 0) return;
        Marshal.Copy(data, offset, (IntPtr)_packet->data, length);
        _packet->stream_index = stream->index;
        _packet->pts = ts;
        _packet->dts = ts;
        if (key) _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
        if (ffmpeg.av_interleaved_write_frame(_ctx, _packet) >= 0) Bytes += length;
        ffmpeg.av_packet_unref(_packet);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        ffmpeg.av_write_trailer(_ctx);
        ffmpeg.avio_closep(&_ctx->pb);
        ffmpeg.avformat_free_context(_ctx);
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
    }
}
