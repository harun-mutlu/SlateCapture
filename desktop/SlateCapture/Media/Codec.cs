using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SlateCapture.Media;

static class Codec
{
    public static string? Error { get; private set; }

    public static bool Init()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(dir)) ffmpeg.RootPath = dir;
        try
        {
            _ = ffmpeg.av_version_info();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            return true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return false;
        }
    }

    public static void Check(this int result, string what)
    {
        if (result >= 0) return;
        throw new InvalidOperationException($"{what} failed ({ErrorText(result)})");
    }

    static unsafe string ErrorText(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString();
    }

    public static unsafe void SetExtradata(AVCodecParameters* par, byte[] data)
    {
        par->extradata = (byte*)ffmpeg.av_mallocz((ulong)(data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        Marshal.Copy(data, 0, (IntPtr)par->extradata, data.Length);
        par->extradata_size = data.Length;
    }
}
