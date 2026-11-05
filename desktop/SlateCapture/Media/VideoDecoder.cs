using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SlateCapture.Media;

public interface IFrameTarget
{
    bool Begin(int width, int height, out IntPtr pixels, out int stride);
    void End();
}

public sealed unsafe class VideoDecoder : IDisposable
{
    readonly AVCodecContext* _ctx;
    readonly AVFrame* _frame;
    readonly AVPacket* _packet;
    SwsContext* _sws;
    int _swsWidth, _swsHeight;
    AVPixelFormat _swsFormat;

    public VideoDecoder(byte[] csd)
    {
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) throw new InvalidOperationException("H.264 decoder not found");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
        _ctx->thread_count = 0;
        _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(csd.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        Marshal.Copy(csd, 0, (IntPtr)_ctx->extradata, csd.Length);
        _ctx->extradata_size = csd.Length;
        ffmpeg.avcodec_open2(_ctx, codec, null).Check("avcodec_open2");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    public bool Decode(byte[] data, int offset, int length, IFrameTarget target)
    {
        if (ffmpeg.av_new_packet(_packet, length) < 0) return false;
        Marshal.Copy(data, offset, (IntPtr)_packet->data, length);
        int sent = ffmpeg.avcodec_send_packet(_ctx, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (sent < 0) return false;

        var shown = false;
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
        {
            shown |= Convert(target);
            ffmpeg.av_frame_unref(_frame);
        }
        return shown;
    }

    bool Convert(IFrameTarget target)
    {
        int w = _frame->width, h = _frame->height;
        var format = (AVPixelFormat)_frame->format;
        if (_sws == null || w != _swsWidth || h != _swsHeight || format != _swsFormat)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = ffmpeg.sws_getContext(w, h, format, w, h, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_POINT, null, null, null);
            _swsWidth = w;
            _swsHeight = h;
            _swsFormat = format;
        }
        if (_sws == null) return false;
        if (!target.Begin(w, h, out var pixels, out var stride)) return false;
        try
        {
            var src = new byte*[] { _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3] };
            var srcStride = new[] { _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], _frame->linesize[3] };
            var dst = new byte*[] { (byte*)pixels };
            var dstStride = new[] { stride };
            ffmpeg.sws_scale(_sws, src, srcStride, 0, h, dst, dstStride);
        }
        finally
        {
            target.End();
        }
        return true;
    }

    public void Dispose()
    {
        var ctx = _ctx;
        ffmpeg.avcodec_free_context(&ctx);
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        ffmpeg.sws_freeContext(_sws);
        _sws = null;
    }
}
