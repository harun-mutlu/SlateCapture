using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlateCapture.Media;
using SlateCapture.Net;
using static SlateCapture.Strings;

namespace SlateCapture;

public sealed class Recording
{
    public required string Path { get; init; }
    public required string Meta { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string RevealText => T("reveal");
}

public partial class MainWindow : Window, IFrameTarget
{
    static readonly IBrush Green = Brush.Parse("#34D3A6");
    static readonly IBrush Amber = Brush.Parse("#E2B341");
    static readonly IBrush Red = Brush.Parse("#F2555A");
    static readonly IBrush Grey = Brush.Parse("#3A414D");
    static readonly IBrush Dim = Brush.Parse("#B3000000");

    readonly Settings _settings = Settings.Load();
    readonly Server _server = new();
    readonly ObservableCollection<Recording> _recent = [];
    readonly DispatcherTimer _timer;
    readonly Stopwatch _statsClock = Stopwatch.StartNew();
    readonly bool _codecReady;
    readonly bool _ready;

    Hello? _hello;
    StreamState? _state;
    bool _connected, _lost, _streaming, _recording;
    int? _busyPort;
    DateTime _recStarted;
    double _fps, _mbps;

    readonly object _recLock = new();
    Mp4Recorder? _recorder;
    VideoFormat? _videoFormat;
    AudioFormat? _audioFormat;
    VideoDecoder? _decoder;
    string _recBase = "";
    int _part;
    int _frames;
    long _bytes;

    readonly object _bitmapLock = new();
    WriteableBitmap? _bitmap;
    ILockedFramebuffer? _locked;
    int _invalidate;
    volatile bool _hasFrame;

    public MainWindow()
    {
        InitializeComponent();
        Use(_settings.Language);
        _codecReady = Codec.Init();

        RecentList.ItemsSource = _recent;
        LanguageBox.ItemsSource = Languages.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == _settings.Language));
        QualitySlider.Value = Math.Clamp(_settings.BitrateMbps, 2, 20);
        QualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) OnQualityChanged();
        };
        AudioCheck.IsChecked = _settings.Audio;
        FolderText.Text = _settings.Folder;

        _server.Connected += h => Dispatcher.UIThread.Post(() => OnConnected(h));
        _server.Disconnected += OnDisconnected;
        _server.StateChanged += s => Dispatcher.UIThread.Post(() => OnState(s));
        _server.VideoFormatReceived += OnVideoFormat;
        _server.VideoReceived += OnVideo;
        _server.AudioFormatReceived += f => { lock (_recLock) _audioFormat = f; };
        _server.AudioReceived += OnAudio;
        _server.PortBusy += port => Dispatcher.UIThread.Post(() =>
        {
            _busyPort = port;
            Render();
        });

        _server.Connected += h => Feedback.Note($"connected: {h.Name}, {h.Os}, encoder {h.Encoder}");
        _server.Disconnected += () => Feedback.Note("disconnected");
        _server.StateChanged += s => Feedback.Note(s.Streaming
            ? $"streaming {s.Width}x{s.Height} {s.Fps} fps, audio {(s.AudioFailed ? "failed" : s.Audio ? "on" : "off")}"
            : "stream stopped");
        _server.PortBusy += port => Feedback.Note($"port {port} busy");

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();

        LoadRecent();
        ApplyTexts();
        _ready = true;
        _ = Feedback.Flush();
        Render();
        _server.Start();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        StopRecording();
        _timer.Stop();
        _server.Dispose();
        base.OnClosing(e);
    }

    void OnConnected(Hello hello)
    {
        _hello = hello;
        _connected = true;
        _lost = false;
        BuildLevels();
        SendConfig();
        Render();
    }

    void OnDisconnected()
    {
        lock (_recLock) _recorder?.Resync();
        Dispatcher.UIThread.Post(() =>
        {
            _lost = _streaming || _recording;
            _connected = false;
            _streaming = false;
            Render();
        });
    }

    void OnState(StreamState state)
    {
        _state = state;
        _streaming = state.Streaming;
        if (!state.Streaming)
        {
            StopRecording();
            _hasFrame = false;
        }
        Render();
    }

    void OnVideoFormat(VideoFormat format)
    {
        if (_codecReady && (_decoder == null || _videoFormat == null || !_videoFormat.Csd.AsSpan().SequenceEqual(format.Csd)))
        {
            _decoder?.Dispose();
            try
            {
                _decoder = new VideoDecoder(format.Csd);
            }
            catch (InvalidOperationException)
            {
                _decoder = null;
            }
        }

        lock (_recLock)
        {
            _videoFormat = format;
            if (_recorder != null)
            {
                if (_recorder.Format.Width != format.Width || _recorder.Format.Height != format.Height)
                    NextPart(format);
                else
                    _recorder.Resync();
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            _streaming = true;
            Render();
        });
    }

    void OnVideo(byte[] data, int offset, int length, long pts, bool key)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, length);
        _decoder?.Decode(data, offset, length, this);
        lock (_recLock) _recorder?.WriteVideo(data, offset, length, pts, key);
    }

    void OnAudio(byte[] data, int offset, int length, long pts)
    {
        lock (_recLock) _recorder?.WriteAudio(data, offset, length, pts);
    }

    bool IFrameTarget.Begin(int width, int height, out IntPtr pixels, out int stride)
    {
        Monitor.Enter(_bitmapLock);
        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _bitmap = bitmap;
            Dispatcher.UIThread.Post(() =>
            {
                var old = Screen.Source as IDisposable;
                Screen.Source = bitmap;
                old?.Dispose();
            });
        }
        _locked = _bitmap.Lock();
        pixels = _locked.Address;
        stride = _locked.RowBytes;
        return true;
    }

    void IFrameTarget.End()
    {
        _locked?.Dispose();
        _locked = null;
        Monitor.Exit(_bitmapLock);

        if (!_hasFrame)
        {
            _hasFrame = true;
            Dispatcher.UIThread.Post(Render);
        }
        if (Interlocked.Exchange(ref _invalidate, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _invalidate, 0);
                Screen.InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    void Tick()
    {
        var seconds = _statsClock.Elapsed.TotalSeconds;
        if (seconds >= 1)
        {
            _statsClock.Restart();
            _fps = Interlocked.Exchange(ref _frames, 0) / seconds;
            _mbps = Interlocked.Exchange(ref _bytes, 0) * 8 / seconds / 1_000_000;
        }

        if (_recording)
        {
            var elapsed = DateTime.Now - _recStarted;
            RecTime.Text = elapsed.ToString(@"hh\:mm\:ss");
            long bytes;
            lock (_recLock) bytes = _recorder?.Bytes ?? 0;
            RecSize.Text = FormatSize(bytes);
        }

        var v = _videoFormat;
        if (v != null)
        {
            var audio = _state?.Audio == true ? " · AAC" : "";
            StatsText.Text = $"{v.Width}×{v.Height} · {_fps:0} FPS · {_mbps:0.0} Mbps{audio}";
        }
    }

    void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    void StartRecording()
    {
        if (!_connected || !_streaming) return;
        try
        {
            Directory.CreateDirectory(_settings.Folder);
            _recBase = Path.Combine(_settings.Folder, $"SlateCapture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
            _part = 1;
            lock (_recLock)
            {
                if (_videoFormat == null) return;
                var audio = _settings.Audio && _state?.Audio == true ? _audioFormat : null;
                _recorder = new Mp4Recorder(_recBase + ".mp4", _videoFormat, audio);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RecHint.Text = ex.Message;
            return;
        }

        _recording = true;
        _recStarted = DateTime.Now;
        RecTime.Text = "00:00:00";
        RecSize.Text = FormatSize(0);
        _server.RequestKeyframe();
        Render();
    }

    void StopRecording()
    {
        Mp4Recorder? recorder;
        lock (_recLock)
        {
            recorder = _recorder;
            _recorder = null;
        }
        if (recorder == null) return;

        var duration = DateTime.Now - _recStarted;
        recorder.Dispose();
        AddRecent(recorder, duration);
        _recording = false;
        Render();
    }

    void NextPart(VideoFormat format)
    {
        var old = _recorder!;
        old.Dispose();
        var duration = DateTime.Now - _recStarted;
        Dispatcher.UIThread.Post(() => AddRecent(old, duration));

        _part++;
        try
        {
            _recorder = new Mp4Recorder($"{_recBase}_part{_part}.mp4", format, old.HasAudio ? _audioFormat : null);
        }
        catch (InvalidOperationException)
        {
            _recorder = null;
            Dispatcher.UIThread.Post(() =>
            {
                _recording = false;
                Render();
            });
        }
    }

    void AddRecent(Mp4Recorder recorder, TimeSpan duration)
    {
        if (!File.Exists(recorder.Path)) return;
        var f = recorder.Format;
        var meta = $"{duration:hh\\:mm\\:ss} · {FormatSize(new FileInfo(recorder.Path).Length)} · {f.Width}×{f.Height}";
        _recent.Insert(0, new Recording { Path = recorder.Path, Meta = meta });
        while (_recent.Count > 20) _recent.RemoveAt(_recent.Count - 1);
        Render();
    }

    void LoadRecent()
    {
        try
        {
            if (!Directory.Exists(_settings.Folder)) return;
            var files = new DirectoryInfo(_settings.Folder).GetFiles("*.mp4")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(10);
            foreach (var f in files)
                _recent.Add(new Recording { Path = f.FullName, Meta = $"{f.LastWriteTime:g} · {FormatSize(f.Length)}" });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    void OnSnapshotClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.Folder);
            var path = Path.Combine(_settings.Folder, $"SlateCapture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
            lock (_bitmapLock) _bitmap?.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string path }) return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", ["-R", path]);
            else
                Process.Start("xdg-open", [Path.GetDirectoryName(path) ?? path]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    async void OnFolderClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = T("folder"),
        });
        var path = result.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        _settings.Folder = path;
        _settings.Save();
        FolderText.Text = path;
        _recent.Clear();
        LoadRecent();
        Render();
    }

    void OnLevelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: int level }) return;
        _settings.Level = level;
        SettingsChanged();
    }

    void OnFpsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;
        _settings.Fps = int.Parse(tag);
        SettingsChanged();
    }

    void OnQualityChanged()
    {
        if (!_ready) return;
        _settings.BitrateMbps = (int)Math.Round(QualitySlider.Value);
        SettingsChanged();
    }

    void OnAudioClick(object? sender, RoutedEventArgs e)
    {
        _settings.Audio = AudioCheck.IsChecked == true;
        SettingsChanged();
    }

    void OnFeedbackClick(object? sender, RoutedEventArgs e) => _ = new FeedbackWindow(_hello, _state).ShowDialog(this);

    void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedIndex < 0) return;
        _settings.Language = Languages[LanguageBox.SelectedIndex].Code;
        _settings.Save();
        Use(_settings.Language);
        ApplyTexts();
        Render();
    }

    void SettingsChanged()
    {
        _settings.Save();
        SendConfig();
        Render();
    }

    int CurrentLevel => _hello is { Levels.Count: > 0 } h ? Math.Clamp(_settings.Level, 0, h.Levels.Count - 1) : 0;

    int CurrentFps => _hello is { Levels.Count: > 0 } h ? Math.Min(_settings.Fps, h.Levels[CurrentLevel].MaxFps) : _settings.Fps;

    void SendConfig()
    {
        if (!_connected || _hello == null) return;
        _server.SendConfig(new StreamConfig(CurrentLevel, CurrentFps, _settings.BitrateMbps * 1_000_000, _settings.Audio && _hello.Audio));
    }

    void BuildLevels()
    {
        LevelPanel.Children.Clear();
        if (_hello == null) return;
        for (int i = 0; i < _hello.Levels.Count; i++)
        {
            var level = _hello.Levels[i];
            var button = new ToggleButton
            {
                Content = i == 0 ? T("resolution.auto", level.Size) : level.Size,
                Tag = i,
            };
            button.Classes.Add("seg");
            button.Click += OnLevelClick;
            LevelPanel.Children.Add(button);
        }
    }

    void ApplyTexts()
    {
        LiveText.Text = T("badge.live");
        RecText.Text = T("badge.rec");
        SizeLabel.Text = T("rec.size");
        SnapshotButton.Content = T("snapshot");
        RecentTitle.Text = T("recent");
        RecentEmptyText.Text = T("recent.empty");
        ResolutionTitle.Text = T("resolution");
        FpsTitle.Text = T("fps");
        FpsNote.Text = T("fps.limited");
        QualityTitle.Text = T("quality");
        QualityHint.Text = T("quality.hint");
        AudioLabel.Text = T("audio");
        FolderTitle.Text = T("folder");
        FolderButton.Content = T("folder.change");
        FormatText.Text = T("folder.format");
        LanguageTitle.Text = T("language");
        LockedNote.Text = T("locked");
        FeedbackButton.Content = T("feedback.button");
        BuildLevels();

        var items = _recent.ToList();
        _recent.Clear();
        foreach (var r in items) _recent.Add(r);
    }

    void Render()
    {
        if (!_ready) return;
        var name = _hello?.Name ?? "";
        var live = _connected && _streaming;

        if (_lost)
        {
            StatusText.Text = T("status.lost");
            StatusDot.Fill = Red;
        }
        else if (!_connected)
        {
            StatusText.Text = T("status.searching");
            StatusDot.Fill = Amber;
        }
        else
        {
            StatusText.Text = T(live ? "status.live" : "status.connected", name);
            StatusDot.Fill = Green;
        }

        Overlay.IsVisible = !live || !_hasFrame;
        Overlay.Background = _lost && _hasFrame ? Dim : Brushes.Black;
        Spinner.IsVisible = !_connected && _busyPort == null;
        if (_busyPort is int port)
        {
            OverlayTitle.Text = T("preview.port", port);
            OverlayHint.Text = "";
        }
        else if (!_codecReady)
        {
            OverlayTitle.Text = T("preview.ffmpeg");
            OverlayHint.Text = Codec.Error ?? "";
        }
        else if (_lost)
        {
            OverlayTitle.Text = T("preview.lost");
            OverlayHint.Text = T(_recording ? "preview.lost.recording" : "preview.lost.hint");
        }
        else if (!_connected)
        {
            OverlayTitle.Text = T("preview.searching");
            OverlayHint.Text = T("preview.searching.hint");
        }
        else if (!_streaming)
        {
            OverlayTitle.Text = T("preview.waiting", name);
            OverlayHint.Text = T("preview.waiting.hint");
        }
        else
        {
            OverlayTitle.Text = "";
            OverlayHint.Text = "";
        }

        LiveBadge.IsVisible = live && _hasFrame;
        RecBadge.IsVisible = _recording;
        StatsBadge.IsVisible = live && _hasFrame;

        var canRecord = live && _hasFrame;
        RecordButton.IsEnabled = canRecord || _recording;
        ToolTip.SetTip(RecordButton, T(_recording ? "rec.stop" : "rec.start"));
        RecIcon.Width = RecIcon.Height = _recording ? 18 : 26;
        RecIcon.CornerRadius = new CornerRadius(_recording ? 3 : 13);
        RecIcon.Background = canRecord || _recording ? Red : Grey;
        RecTime.Foreground = _recording ? Brush.Parse("#FF8A8E") : Brushes.White;
        RecHint.Text = _recording
            ? T(_connected ? "rec.running" : "rec.waiting")
            : T(canRecord ? "rec.ready" : "rec.idle");
        if (!_recording && RecSize.Text == "") RecSize.Text = "—";
        SnapshotButton.IsEnabled = canRecord;

        RecentEmpty.IsVisible = _recent.Count == 0;

        var unlocked = !_recording;
        var levels = _hello?.Levels ?? [];
        foreach (var child in LevelPanel.Children.OfType<ToggleButton>())
        {
            child.IsChecked = child.Tag is int i && i == CurrentLevel;
            child.IsEnabled = unlocked;
        }
        ProbeText.Text = levels.Count > 0
            ? T("resolution.probe", levels[0].MaxFps, levels[0].Size)
            : T("resolution.none");

        var maxFps = levels.Count > 0 ? levels[CurrentLevel].MaxFps : 60;
        Fps60.IsEnabled = unlocked && maxFps >= 60;
        Fps30.IsEnabled = unlocked;
        Fps60.IsChecked = CurrentFps >= 60;
        Fps30.IsChecked = CurrentFps < 60;
        FpsNote.IsVisible = maxFps < 60;

        QualitySlider.IsEnabled = unlocked;
        QualityValue.Text = $"{_settings.BitrateMbps} Mbps";

        var audioSupported = _hello?.Audio == true;
        AudioCheck.IsEnabled = unlocked && audioSupported;
        AudioCheck.IsChecked = _settings.Audio && (audioSupported || _hello == null);
        AudioLabel.Foreground = audioSupported || _hello == null ? Brushes.White : Brush.Parse("#7D8592");
        AudioNote.Text = _hello == null ? T("audio.unknown")
            : !audioSupported ? T("audio.no", $"{_hello.Name}, {_hello.Os}")
            : _state?.AudioFailed == true ? T("audio.failed")
            : T("audio.ok");

        FolderButton.IsEnabled = unlocked;
        LanguageBox.IsEnabled = unlocked;
        LockedNote.IsVisible = _recording;
    }

    static string FormatSize(long bytes) => bytes < 1L << 30
        ? $"{bytes / 1048576.0:0.0} MB"
        : $"{bytes / 1073741824.0:0.00} GB";
}
