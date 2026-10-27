using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TorsherPlayer.Infrastructure;
using TorsherPlayer.Input;
using TorsherPlayer.Integration;
using TorsherPlayer.Models;
using TorsherPlayer.Player;
using TorsherPlayer.Torrent;

namespace TorsherPlayer;

public partial class MainWindow : Window
{
    private enum MenuMode { None, Audio, Subtitles, Settings, Episodes }

    private static readonly TimeSpan OsdIdleTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxStartupRetries = 2;
    private const double PointerActivityThreshold = 4.0;
    private const double SeekTapStepSeconds = 10.0;
    private const double SeekHoldDelaySeconds = 0.32;
    private const double SeekBaseSpeedSecondsPerSecond = 18.0;
    private const double SeekAccelerationSecondsPerSecondSquared = 36.0;
    private const double SeekMaxSpeedSecondsPerSecond = 180.0;

    private readonly LaunchOptions _launch;
    private readonly FileLogger _log;
    private readonly PlayerSettings _settings;
    private ProgressReporter _reporter;
    private readonly IGamepadService _gamepad;
    private readonly PowerKeepAlive _powerKeepAlive;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _volumePanelTimer;
    private readonly DispatcherTimer _seekPreviewTimer;
    private readonly Avalonia.Input.Cursor _hiddenCursor = new(StandardCursorType.None);
    private readonly string _baseMediaKey;
    private readonly string _baseTitle;

    private MpvEngine? _engine;
    private TorrentSession? _torrent;
    private int? _torrentMediaIndex;
    private PlayerSnapshot _state = new(AudioTracks: [], SubtitleTracks: []);
    private MenuMode _menu;
    private long _lastActivity = Stopwatch.GetTimestamp();
    private Point? _lastPointerActivityPoint;
    private DateTime _toastUntil;
    private DateTime _exitArmedUntil;
    private bool _osdVisible = true;
    private bool _dragging;
    private bool _loaded;
    private bool _mediaStarted;
    private bool _reporterStarted;
    private bool _explicitResumeConsumed;
    private bool _closing;
    private bool _cleanupRunning;
    private bool _allowClose;
    private bool _renderFaulted;
    private bool _startupRetryScheduled;
    private bool _syncingVolumeSlider;
    private bool _volumeAdjusting;
    private bool _seekPreviewActive;
    private int _seekPreviewDirection;
    private double _seekPreviewTarget;
    private double? _pendingSeekTarget;
    private long _seekHoldStartedAt;
    private long _seekPreviewLastTick;
    private long _pendingSeekStartedAt;
    private int _startupRetryCount;
    private long _loadStartedAt;
    private double _currentLoadResumeSeconds;
    private CancellationTokenSource? _startupRetryCts;

    public MainWindow(LaunchOptions launch, FileLogger log)
    {
        _launch = launch;
        _log = log;
        _baseMediaKey = launch.MediaKey;
        _baseTitle = launch.Title;
        _settings = SettingsStore.Load(log);
        if (launch.UiScale is not null) _settings.UiScale = launch.UiScale.Value;
        if (launch.CompletedAt is not null) _settings.CompletedAt = launch.CompletedAt.Value;
        if (launch.ProgressEverySeconds is not null) _settings.ProgressEverySeconds = launch.ProgressEverySeconds.Value;

        _reporter = new ProgressReporter(launch, log, _settings.CompletedAt);
        InitializeComponent();
        ThemeCatalog.Apply(Application.Current, _settings.Theme);
        ApplyUiScale();
        MediaTitle.Text = launch.Title;
        ConfigureWindow();

        _powerKeepAlive = new PowerKeepAlive(log);
        _gamepad = SdlGamepadService.CreateOrNull(log);
        _gamepad.Action += action => Dispatcher.Post(() => HandleGamepad(action));
        _gamepad.ActionReleased += action => Dispatcher.Post(() => HandleGamepadReleased(action));

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UiTick();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.ProgressEverySeconds, 2, 30)) };
        _progressTimer.Tick += (_, _) =>
        {
            if (_state.FileLoaded && !_state.Ended) _reporter.Emit("progress", _state);
        };
        _volumePanelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _volumePanelTimer.Tick += (_, _) => HideVolumePanel(restoreFocus: true);
        _seekPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _seekPreviewTimer.Tick += (_, _) => UpdateSeekPreview();
        VolumeSlider.PropertyChanged += VolumeSlider_PropertyChanged;
        VolumeSlider.AddHandler(InputElement.PointerPressedEvent, VolumeSlider_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolumeSlider.AddHandler(InputElement.PointerReleasedEvent, VolumeSlider_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolumeSlider.PointerCaptureLost += (_, _) =>
        {
            _volumeAdjusting = false;
            if (VolumePanel.IsVisible) ArmVolumePanelAutoHide();
        };

        VideoGl.RendererReady += () => Dispatcher.Post(StartMediaIfReady);
        VideoGl.RenderError += message => Dispatcher.Post(() => HandleRenderError(message));

        AddHandler(InputElement.KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyUpEvent, Window_KeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, Window_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerPressedEvent, Window_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerWheelChangedEvent, Window_PointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        DoubleTapped += Window_DoubleTapped;
        Deactivated += (_, _) => CommitSeekPreview();
        DragDrop.AddDragOverHandler(this, Window_DragOver);
        DragDrop.AddDropHandler(this, Window_Drop);

        Progress.AddHandler(InputElement.PointerPressedEvent, Progress_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Progress.AddHandler(InputElement.PointerReleasedEvent, Progress_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Progress.PointerCaptureLost += (_, _) => _dragging = false;

        Opened += async (_, _) =>
        {
            _uiTimer.Start();
            _progressTimer.Start();
            RegisterActivity();
            await InitializeSourceAsync();
        };
    }

    private void ConfigureWindow()
    {
        if (_launch.Windowed || !_settings.StartFullscreen)
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
            CanResize = true;
            Width = 1280;
            Height = 720;
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            CanResize = false;
            WindowState = WindowState.FullScreen;
        }
    }

    private async Task InitializeSourceAsync()
    {
        if (!string.IsNullOrWhiteSpace(_launch.InitialError))
        {
            ShowError("Не удалось запустить плеер", _launch.InitialError!);
            return;
        }

        if (!_launch.IsTorrent)
        {
            StartEngine();
            return;
        }

        try
        {
            StatusPanel.IsVisible = true;
            ErrorButtons.IsVisible = false;
            StatusTitle.Text = "Открываем torrent";
            StatusDetail.Text = "Читаем метаданные и список видеофайлов…";
            _torrent = await TorrentSession.OpenAsync(_launch.TorrentPath!, AppPaths.Torrents, _log);
            EpisodesButton.IsVisible = _torrent.MediaFiles.Count > 1;

            if (_torrent.MediaFiles.Count == 0)
            {
                ShowError("В torrent нет файлов", "Не удалось найти файл для воспроизведения.");
                return;
            }

            if (_launch.TorrentFileIndex is { } requested)
            {
                if (requested >= _torrent.MediaFiles.Count)
                {
                    ShowError("Неверный индекс файла", $"Доступно видеофайлов: {_torrent.MediaFiles.Count}; передан индекс {requested}.");
                    return;
                }
                await SelectTorrentMediaAsync(requested);
                return;
            }

            if (_torrent.MediaFiles.Count == 1)
            {
                await SelectTorrentMediaAsync(0);
                return;
            }

            ShowInitialTorrentPicker();
        }
        catch (Exception ex)
        {
            _log.Exception(ex, "Torrent initialization");
            ShowError("Не удалось открыть torrent", ex.InnerException?.Message ?? ex.Message);
        }
    }

    private void ShowInitialTorrentPicker()
    {
        if (_torrent is null) return;
        StatusPanel.IsVisible = false;
        SidePanel.IsVisible = false;
        SourcePickerPanel.IsVisible = true;
        Osd.IsEnabled = false;
        SourcePickerSubtitle.Text = $"{Path.GetFileName(_torrent.TorrentPath)} · видеофайлов: {_torrent.MediaFiles.Count}";
        BuildTorrentButtons(SourceFilesHost, initialPicker: true);
        FocusFirstDeferred(SourceFilesHost);
    }

    private void BuildTorrentButtons(StackPanel host, bool initialPicker)
    {
        host.Children.Clear();
        if (_torrent is null) return;
        foreach (var item in _torrent.MediaFiles)
        {
            var selected = _torrentMediaIndex == item.Index;
            var button = CreateMenuButton(item.Name, $"{item.SizeText} · {item.Path}", selected);
            button.Tag = item;
            button.Click += async (_, _) =>
            {
                if (!initialPicker && _torrentMediaIndex == item.Index)
                {
                    CloseMenu();
                    return;
                }
                await SelectTorrentMediaAsync(item.Index);
            };
            host.Children.Add(button);
        }
    }

    private async Task SelectTorrentMediaAsync(int index)
    {
        if (_torrent is null) return;
        CancelStartupRetry();
        _startupRetryScheduled = false;
        _startupRetryCount = 0;
        var item = _torrent.MediaFiles[index];
        SourcePickerPanel.IsVisible = false;
        SidePanel.IsVisible = false;
        _menu = MenuMode.None;
        ShowLoadingStatus(item.Name, "Подготавливаем BitTorrent-поток…");
        RegisterActivity();

        try
        {
            // Prepare the next stream before retiring the current reporting session.
            // If torrent startup fails, the existing playback/session remains recoverable.
            var uri = await _torrent.OpenMediaAsync(index);

            if (_reporterStarted)
            {
                _reporter.Emit("stopped", _state);
                await _reporter.DisposeAsync();
                _reporterStarted = false;
            }
            else
            {
                await _reporter.DisposeAsync();
            }

            _torrentMediaIndex = index;
            var keyBase = string.IsNullOrWhiteSpace(_baseMediaKey)
                ? $"torrent:{Path.GetFileName(_torrent.TorrentPath)}"
                : _baseMediaKey;
            var mediaKey = $"{keyBase}#file:{index}:{item.Path}";
            var title = string.IsNullOrWhiteSpace(_baseTitle) ? item.Name : $"{_baseTitle} — {item.Name}";
            _launch.ResolveMedia(uri, mediaKey, title);
            _reporter = new ProgressReporter(_launch, _log, _settings.CompletedAt);
            MediaTitle.Text = title;
            EpisodesButton.IsVisible = _torrent.MediaFiles.Count > 1;

            if (_engine is null)
            {
                StartEngine();
            }
            else
            {
                BeginMediaLoad(ResolveResumeSeconds());
            }
        }
        catch (Exception ex)
        {
            _log.Exception(ex, "Torrent media selection");
            ShowError("Не удалось запустить файл из torrent", ex.InnerException?.Message ?? ex.Message);
        }
    }

    private void StartEngine()
    {
        if (_engine is not null) return;
        if (string.IsNullOrWhiteSpace(_launch.Url))
        {
            ShowError("Источник не готов", "Не удалось получить URL для воспроизведения.");
            return;
        }

        _mediaStarted = false;
        _renderFaulted = false;
        try
        {
            ShowLoadingStatus("Подготовка воспроизведения", "Инициализация libmpv и видеовывода…");

            _engine = new MpvEngine(_launch, _log, _settings.HardwareDecoding);
            _engine.PlayerEvent += OnPlayerEvent;
            _engine.Error += message => Dispatcher.Post(() => _ = HandlePlaybackErrorAsync(message));
            VideoGl.Attach(_engine, _log);
        }
        catch (Exception ex)
        {
            _log.Exception(ex, "StartEngine");
            ShowError("Не удалось запустить медиадвижок", ex.Message);
        }
    }

    private void StartMediaIfReady()
    {
        if (_mediaStarted || _engine is null || !_engine.RendererReady || string.IsNullOrWhiteSpace(_launch.Url)) return;
        BeginMediaLoad(ResolveResumeSeconds());
    }

    private void BeginMediaLoad(double resumeSeconds, bool resetRetryState = true)
    {
        if (_engine is null || string.IsNullOrWhiteSpace(_launch.Url)) return;
        if (resetRetryState)
        {
            CancelStartupRetry();
            _startupRetryCount = 0;
        }

        _loaded = false;
        _mediaStarted = true;
        _currentLoadResumeSeconds = Math.Max(0, resumeSeconds);
        _loadStartedAt = Stopwatch.GetTimestamp();
        ShowLoadingStatus(string.IsNullOrWhiteSpace(_launch.Title) ? "Подготовка воспроизведения" : _launch.Title,
            "Подключение к источнику…");

        if (!_reporterStarted)
        {
            _reporter.Emit("started", _state);
            _reporterStarted = true;
        }
        _engine.Load(_currentLoadResumeSeconds);
    }

    private void ShowLoadingStatus(string title, string detail)
    {
        StatusPanel.IsVisible = true;
        StatusProgress.IsVisible = true;
        StatusProgress.IsIndeterminate = true;
        ErrorButtons.IsVisible = false;
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        Osd.IsEnabled = false;
        Cursor = Avalonia.Input.Cursor.Default;
    }

    private void UpdateLoadingStatus()
    {
        if (!_mediaStarted || _loaded || !StatusPanel.IsVisible || ErrorButtons.IsVisible || _loadStartedAt == 0) return;
        var elapsed = Stopwatch.GetElapsedTime(_loadStartedAt);
        if (_state.CacheSeconds > .2)
        {
            StatusDetail.Text = $"Получаем поток · буфер {_state.CacheSeconds:0.0} с · ожидание {elapsed.TotalSeconds:0} с";
        }
        else if (elapsed < TimeSpan.FromSeconds(3))
        {
            StatusDetail.Text = "Подключение к источнику…";
        }
        else if (elapsed < TimeSpan.FromSeconds(10))
        {
            StatusDetail.Text = "Получаем данные и анализируем медиапоток…";
        }
        else
        {
            StatusDetail.Text = $"Источник отвечает медленно — продолжаем ждать · {elapsed.TotalSeconds:0} с";
        }
    }

    private async Task HandlePlaybackErrorAsync(string message)
    {
        if (_closing) return;
        _loaded = false;
        _state = _engine?.Snapshot ?? _state;

        if (_startupRetryScheduled) return;

        if (CanRetryStartup())
        {
            _startupRetryScheduled = true;
            _startupRetryCount++;
            var attempt = _startupRetryCount;
            ShowLoadingStatus(string.IsNullOrWhiteSpace(_launch.Title) ? "Подготовка воспроизведения" : _launch.Title,
                $"Источник пока не готов. Повторяем подключение… ({attempt}/{MaxStartupRetries})");
            _log.Warn($"startup playback failed; retry {attempt}/{MaxStartupRetries}: {message}");

            CancelStartupRetry();
            _startupRetryCts = new CancellationTokenSource();
            var token = _startupRetryCts.Token;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(StartupRetryDelay.TotalMilliseconds * attempt), token);
                if (token.IsCancellationRequested || _closing || _engine is null || _loaded) return;
                _startupRetryScheduled = false;
                BeginMediaLoad(_currentLoadResumeSeconds, resetRetryState: false);
                return;
            }
            catch (OperationCanceledException) { return; }
            finally
            {
                if (token.IsCancellationRequested) _startupRetryScheduled = false;
            }
        }

        _startupRetryScheduled = false;
        _reporter.Emit("error", _state, message);
        ShowError("Ошибка воспроизведения", message);
    }

    private bool CanRetryStartup()
    {
        if (_loaded || !_mediaStarted || _startupRetryCount >= MaxStartupRetries) return false;
        if (_torrent is not null) return true;
        if (!Uri.TryCreate(_launch.Url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https";
    }

    private void CancelStartupRetry()
    {
        try { _startupRetryCts?.Cancel(); } catch { }
        _startupRetryCts?.Dispose();
        _startupRetryCts = null;
    }

    private double ResolveResumeSeconds()
    {
        if (!_explicitResumeConsumed && _launch.ResumeSeconds is { } explicitResume)
        {
            _explicitResumeConsumed = true;
            return explicitResume;
        }
        return _reporter.LoadLocalResume() ?? 0;
    }

    private void HandleRenderError(string message)
    {
        if (_closing) return;
        _renderFaulted = true;
        _loaded = false;
        ShowError("Ошибка видеовывода", message);
    }

    private void OnPlayerEvent(string name)
    {
        Dispatcher.Post(() =>
        {
            if (_engine is null) return;
            _state = _engine.Snapshot;
            switch (name)
            {
                case "file_loaded":
                    _loaded = true;
                    _startupRetryScheduled = false;
                    CancelStartupRetry();
                    StatusPanel.IsVisible = false;
                    StatusProgress.IsVisible = false;
                    SourcePickerPanel.IsVisible = false;
                    Osd.IsEnabled = true;
                    RegisterActivity();
                    EnsureDefaultFocus();
                    _reporter.Emit("file_loaded", _state);
                    break;
                case "paused":
                    _reporter.Emit("paused", _state);
                    ShowToast("Пауза");
                    break;
                case "playing":
                    _reporter.Emit("playing", _state);
                    ShowToast("Воспроизведение");
                    break;
                case "seeked":
                    _reporter.Emit("seeked", _state);
                    break;
                case "completed":
                    _reporter.Emit("completed", _state);
                    Close();
                    break;
            }
        });
    }

    private void UiTick()
    {
        if (_engine is not null) _state = _engine.Snapshot;
        if (_loaded)
        {
            // Keep the preview/pending target stable until the one real seek made
            // on release has reached mpv. Without this grace period the 100 ms
            // snapshot tick can briefly snap the thumb back to the old position.
            if (_pendingSeekTarget is not null)
            {
                var reached = Math.Abs(_state.Position - _pendingSeekTarget.Value) <= 1.5;
                var timedOut = Stopwatch.GetElapsedTime(_pendingSeekStartedAt) >= TimeSpan.FromSeconds(2);
                if (reached || timedOut) _pendingSeekTarget = null;
            }

            if (!_dragging && !_seekPreviewActive && _pendingSeekTarget is null)
            {
                Progress.Maximum = Math.Max(1, _state.Duration);
                Progress.Value = Math.Clamp(_state.Position, 0, Progress.Maximum);
            }

            PositionText.Text = Time((_dragging || _seekPreviewActive || _pendingSeekTarget is not null) ? Progress.Value : _state.Position);
            DurationText.Text = Time(_state.Duration);
            PlayIcon.Kind = _state.Paused ? MaterialIconKind.Play : MaterialIconKind.Pause;
            MediaTitle.Text = string.IsNullOrWhiteSpace(_launch.Title) ? _state.MediaTitle : _launch.Title;
            VolumeButtonIcon.Kind = _state.Muted ? MaterialIconKind.VolumeOff : MaterialIconKind.VolumeHigh;
            VolumeText.Text = $"{Math.Round(_state.Volume):0}%";
            VolumePanelValue.Text = $"{Math.Round(VolumePanel.IsVisible ? VolumeSlider.Value : _state.Volume):0}%";
            BufferText.Text = _state.CacheSeconds > .2 ? $"буфер {_state.CacheSeconds:0.0}с" : "";
            BufferingBadge.IsVisible = _state.Buffering;
            BufferingPercent.Text = _state.Buffering ? $"{Math.Round(_state.CachePercent):0}%" : "";
            FullscreenIcon.Kind = WindowState == WindowState.FullScreen ? MaterialIconKind.FullscreenExit : MaterialIconKind.Fullscreen;
            ProgressFocusBorder.BorderThickness = Progress.IsFocused ? new Thickness(3) : new Thickness(0);
            UpdateSettingValues();
        }
        else
        {
            BufferingBadge.IsVisible = false;
        }

        UpdateLoadingStatus();
        if (_toastUntil != default && DateTime.UtcNow > _toastUntil) Toast.IsVisible = false;
        if (_menu == MenuMode.None && !SourcePickerPanel.IsVisible && !_dragging && !_seekPreviewActive && _osdVisible && Stopwatch.GetElapsedTime(_lastActivity) >= OsdIdleTimeout)
            HideOsd();
    }

    private void RegisterActivity()
    {
        _lastActivity = Stopwatch.GetTimestamp();
        SetOsdVisible(true);
        Cursor = Avalonia.Input.Cursor.Default;
    }

    private void RegisterPointerActivity(Point point)
    {
        if (_lastPointerActivityPoint is { } previous)
        {
            var dx = point.X - previous.X;
            var dy = point.Y - previous.Y;
            if (dx * dx + dy * dy < PointerActivityThreshold * PointerActivityThreshold) return;
        }
        _lastPointerActivityPoint = point;
        RegisterActivity();
    }

    private void SetOsdVisible(bool visible)
    {
        _osdVisible = visible;
        Osd.IsVisible = visible;
    }

    private void HideOsd()
    {
        if (_menu != MenuMode.None || SourcePickerPanel.IsVisible || _dragging) return;
        HideVolumePanel(restoreFocus: false);
        SetOsdVisible(false);
        Cursor = _hiddenCursor;
    }

    private void ShowToast(string text, int ms = 900)
    {
        ToastText.Text = text;
        Toast.IsVisible = true;
        _toastUntil = DateTime.UtcNow.AddMilliseconds(ms);
    }

    private void Play_Click(object? sender, RoutedEventArgs e) => DoPause();
    private void SeekBack_Click(object? sender, RoutedEventArgs e) { _engine?.SeekRelative(-10); ShowToast("−10 сек"); RegisterActivity(); }
    private void SeekForward_Click(object? sender, RoutedEventArgs e) { _engine?.SeekRelative(10); ShowToast("+10 сек"); RegisterActivity(); }

    private void DoPause()
    {
        RegisterActivity();
        if (_engine is null) return;
        var paused = !_state.Paused;
        _state = _state with { Paused = paused };
        PlayIcon.Kind = paused ? MaterialIconKind.Play : MaterialIconKind.Pause;
        ShowToast(paused ? "Пауза" : "Воспроизведение");
        _engine.SetPause(paused);
    }

    private void Audio_Click(object? sender, RoutedEventArgs e) => OpenMenu(MenuMode.Audio);
    private void Subs_Click(object? sender, RoutedEventArgs e) => OpenMenu(MenuMode.Subtitles);
    private void Volume_Click(object? sender, RoutedEventArgs e)
    {
        RegisterActivity();
        if (VolumePanel.IsVisible) HideVolumePanel(restoreFocus: true);
        else ShowVolumePanel();
    }
    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenMenu(MenuMode.Settings);
    private void Episodes_Click(object? sender, RoutedEventArgs e) => OpenMenu(MenuMode.Episodes);
    private void Fullscreen_Click(object? sender, RoutedEventArgs e) => ToggleFullscreen();
    private void CloseMenu_Click(object? sender, RoutedEventArgs e) => CloseMenu();
    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Retry_Click(object? sender, RoutedEventArgs e)
    {
        RegisterActivity();
        CancelStartupRetry();
        _startupRetryCount = 0;
        _startupRetryScheduled = false;
        ShowLoadingStatus("Повторное подключение", "Подготавливаем источник…");
        _loaded = false;

        try
        {
            if (_torrent is not null && _torrentMediaIndex is { } torrentIndex)
            {
                var uri = await _torrent.OpenMediaAsync(torrentIndex);
                _launch.ResolveMedia(uri);
            }

            if (_engine is null)
            {
                _renderFaulted = false;
                _mediaStarted = false;
                StartEngine();
                return;
            }

            if (_renderFaulted || !_engine.RendererReady)
            {
                await RestartEngineAsync();
                return;
            }

            BeginMediaLoad(Math.Max(0, _state.Position));
        }
        catch (Exception ex)
        {
            _log.Exception(ex, "Retry");
            ShowError("Повторное подключение не удалось", ex.InnerException?.Message ?? ex.Message);
        }
    }

    private async Task RestartEngineAsync()
    {
        var old = _engine;
        _engine = null;
        _mediaStarted = false;
        _renderFaulted = false;
        if (old is not null)
        {
            try { await VideoGl.ReleaseRendererAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { _log.Exception(ex, "Renderer release during retry"); }
            VideoGl.Detach();
            try { await Task.Run(old.Dispose); }
            catch (Exception ex) { _log.Exception(ex, "Retry engine dispose"); }
        }
        if (!_closing) StartEngine();
    }

    private void Screenshot_Click(object? sender, RoutedEventArgs e)
    {
        RegisterActivity();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TorsherPlayer");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"TorsherPlayer-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        _engine?.Screenshot(path);
        ShowToast("Снимок сохранён", 1400);
    }

    private void OpenMenu(MenuMode mode)
    {
        RegisterActivity();
        HideVolumePanel(restoreFocus: false);
        _menu = mode;
        SidePanel.IsVisible = true;
        SourcePickerPanel.IsVisible = false;
        Osd.IsEnabled = false;
        Cursor = Avalonia.Input.Cursor.Default;
        MenuScroll.IsVisible = mode != MenuMode.Settings;
        SettingsScroll.IsVisible = mode == MenuMode.Settings;
        MenuItemsHost.Children.Clear();

        switch (mode)
        {
            case MenuMode.Audio:
                SideTitle.Text = "Аудиодорожка";
                foreach (var track in _state.AudioTracks ?? [])
                {
                    var button = CreateMenuButton(track.Label, DescribeTrack(track), track.Selected);
                    button.Click += (_, _) =>
                    {
                        _engine?.SetAudioTrack(track.Id);
                        ShowToast(track.Label, 1300);
                        CloseMenu();
                    };
                    MenuItemsHost.Children.Add(button);
                }
                FocusFirstDeferred(MenuItemsHost);
                break;

            case MenuMode.Subtitles:
                SideTitle.Text = "Субтитры";
                var off = CreateMenuButton("Без субтитров", "Отключить отображение субтитров", _state.SelectedSubtitle is null);
                off.Click += (_, _) => { _engine?.SetSubtitleTrack(null); CloseMenu(); };
                MenuItemsHost.Children.Add(off);
                foreach (var track in _state.SubtitleTracks ?? [])
                {
                    var button = CreateMenuButton(track.Label, DescribeTrack(track), track.Selected);
                    button.Click += (_, _) =>
                    {
                        _engine?.SetSubtitleTrack(track.Id);
                        ShowToast(track.Label, 1300);
                        CloseMenu();
                    };
                    MenuItemsHost.Children.Add(button);
                }
                FocusFirstDeferred(MenuItemsHost);
                break;

            case MenuMode.Episodes:
                SideTitle.Text = "Серии / файлы";
                BuildTorrentButtons(MenuItemsHost, initialPicker: false);
                FocusFirstDeferred(MenuItemsHost);
                break;

            case MenuMode.Settings:
                SideTitle.Text = "Настройки";
                UpdateSettingValues();
                FocusDeferred(ThemePrevButton);
                break;
        }
    }

    private Button CreateMenuButton(string primary, string secondary, bool selected)
    {
        var button = new Button { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        button.Classes.Add("menuItem");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = primary, FontSize = CurrentFont("UiFont"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = secondary, FontSize = CurrentFont("UiFontSmall"), Opacity = .68, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        if (selected)
        {
            button.Classes.Add("selected");
            var check = new Material.Icons.Avalonia.MaterialIcon
            {
                Kind = MaterialIconKind.Check,
                Width = CurrentFont("UiIconSmall"),
                Height = CurrentFont("UiIconSmall"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }
        button.Content = grid;
        return button;
    }

    private static string DescribeTrack(MediaTrack track)
    {
        var parts = new[] { track.Lang, track.Codec, track.External ? "внешняя" : null }
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join(" · ", parts);
    }

    private void CloseMenu()
    {
        _menu = MenuMode.None;
        SidePanel.IsVisible = false;
        Osd.IsEnabled = true;
        RegisterActivity();
        FocusDeferred(PlayButton);
    }

    private void HandleGamepad(GamepadAction action)
    {
        RegisterActivity();

        if (action == GamepadAction.B)
        {
            if (VolumePanel.IsVisible) HideVolumePanel(restoreFocus: true);
            else if (_menu != MenuMode.None) CloseMenu();
            else ArmOrExit();
            return;
        }

        if (action is GamepadAction.Left or GamepadAction.Right or GamepadAction.Up or GamepadAction.Down)
        {
            HandleDirectionalInput(action switch
            {
                GamepadAction.Left => NavigationDirection.Left,
                GamepadAction.Right => NavigationDirection.Right,
                GamepadAction.Up => NavigationDirection.Up,
                _ => NavigationDirection.Down
            });
            return;
        }

        if (action == GamepadAction.A)
        {
            ActivateFocused();
            return;
        }

        // Optional convenience buttons stay available, but the entire player is
        // operable without memorising them.
        if (action == GamepadAction.Menu) OpenMenu(MenuMode.Settings);
        else if (action == GamepadAction.View) ToggleFullscreen();
        else if (action == GamepadAction.LeftBumper) { _engine?.SeekRelative(-60); ShowToast("−60 сек"); }
        else if (action == GamepadAction.RightBumper) { _engine?.SeekRelative(60); ShowToast("+60 сек"); }
    }

    private void HandleGamepadReleased(GamepadAction action)
    {
        if (action == GamepadAction.Left) CommitSeekPreview(-1);
        else if (action == GamepadAction.Right) CommitSeekPreview(1);
    }

    private void HandleDirectionalInput(NavigationDirection direction)
    {
        EnsureDefaultFocus();
        var focused = FocusManager?.GetFocusedElement();

        if (ReferenceEquals(focused, VolumeSlider))
        {
            if (direction is NavigationDirection.Left or NavigationDirection.Right)
            {
                VolumeSlider.Value = Math.Clamp(
                    VolumeSlider.Value + (direction == NavigationDirection.Left ? -5d : 5d),
                    VolumeSlider.Minimum,
                    VolumeSlider.Maximum);
                return;
            }

            if (direction == NavigationDirection.Down)
            {
                HideVolumePanel(restoreFocus: true);
                return;
            }

            if (direction == NavigationDirection.Up)
            {
                HideVolumePanel(restoreFocus: false);
                Progress.Focus(NavigationMethod.Directional, KeyModifiers.None);
                return;
            }
        }

        if (ReferenceEquals(focused, Progress))
        {
            if (direction is NavigationDirection.Left or NavigationDirection.Right)
            {
                BeginSeekPreview(direction == NavigationDirection.Left ? -1 : 1);
                return;
            }
            if (direction == NavigationDirection.Down)
            {
                CommitSeekPreview();
                PlayButton.Focus(NavigationMethod.Directional, KeyModifiers.None);
                return;
            }
        }

        // Settings use a strict two-column button matrix. Spatial focus works well
        // for free-form menus, but here a slightly different label/value width can
        // otherwise make Left/Right jump into the row above or below.
        if (_menu == MenuMode.Settings && HandleSettingsDirectionalInput(focused, direction))
            return;

        // The seek bar is a first-class navigation row. Up from any bottom OSD
        // action always lands on it, instead of relying on spatial heuristics.
        if (direction == NavigationDirection.Up && focused is Button &&
            _menu == MenuMode.None && !SourcePickerPanel.IsVisible && !StatusPanel.IsVisible)
        {
            Progress.Focus(NavigationMethod.Directional, KeyModifiers.None);
            return;
        }

        MoveFocus(direction);
    }

    private void BeginSeekPreview(int direction)
    {
        if (!_loaded || _engine is null || _state.Duration <= 0 || direction == 0) return;
        direction = Math.Sign(direction);

        if (_seekPreviewActive)
        {
            // Key repeat and SDL repeat events are deliberately ignored here. The
            // 30 fps timer below owns motion, which makes a held direction smooth.
            if (_seekPreviewDirection == direction) return;
            CommitSeekPreview();
        }

        var maximum = Math.Max(1, _state.Duration);
        Progress.Maximum = maximum;
        var origin = _pendingSeekTarget ?? Math.Clamp(_state.Position, 0, maximum);
        _pendingSeekTarget = null;

        _seekPreviewDirection = direction;
        _seekPreviewTarget = Math.Clamp(origin + direction * SeekTapStepSeconds, 0, maximum);
        _seekPreviewActive = true;
        _seekHoldStartedAt = Stopwatch.GetTimestamp();
        _seekPreviewLastTick = _seekHoldStartedAt;
        Progress.Value = _seekPreviewTarget;
        PositionText.Text = Time(_seekPreviewTarget);
        ShowToast(direction < 0 ? "−10 сек" : "+10 сек", 700);
        _seekPreviewTimer.Start();
    }

    private void UpdateSeekPreview()
    {
        if (!_seekPreviewActive)
        {
            _seekPreviewTimer.Stop();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var heldSeconds = Stopwatch.GetElapsedTime(_seekHoldStartedAt, now).TotalSeconds;
        var deltaSeconds = Stopwatch.GetElapsedTime(_seekPreviewLastTick, now).TotalSeconds;
        _seekPreviewLastTick = now;
        if (heldSeconds < SeekHoldDelaySeconds) return;

        // Start gently and continuously accelerate the longer the direction is
        // held. Only the UI target moves here; mpv is untouched until release.
        var acceleratingFor = heldSeconds - SeekHoldDelaySeconds;
        var speed = Math.Min(
            SeekMaxSpeedSecondsPerSecond,
            SeekBaseSpeedSecondsPerSecond + SeekAccelerationSecondsPerSecondSquared * acceleratingFor);

        var maximum = Math.Max(1, _state.Duration);
        _seekPreviewTarget = Math.Clamp(
            _seekPreviewTarget + _seekPreviewDirection * speed * deltaSeconds,
            0,
            maximum);
        Progress.Value = _seekPreviewTarget;
        PositionText.Text = Time(_seekPreviewTarget);
        RegisterActivity();
    }

    private void CommitSeekPreview(int? expectedDirection = null)
    {
        if (!_seekPreviewActive) return;
        if (expectedDirection is not null && _seekPreviewDirection != Math.Sign(expectedDirection.Value)) return;

        var target = _seekPreviewTarget;
        StopSeekPreview();
        _pendingSeekTarget = target;
        _pendingSeekStartedAt = Stopwatch.GetTimestamp();
        Progress.Value = target;
        PositionText.Text = Time(target);

        // Exactly one decoder seek per gesture. Holding a direction only scrubs
        // the target above, so network/4K playback is not hammered by seek spam.
        _engine?.SeekAbsolute(target);
        RegisterActivity();
    }

    private void CancelSeekPreview()
    {
        if (!_seekPreviewActive) return;
        StopSeekPreview();
    }

    private void StopSeekPreview()
    {
        _seekPreviewActive = false;
        _seekPreviewDirection = 0;
        _seekPreviewTimer.Stop();
    }

    private bool HandleSettingsDirectionalInput(object? focused, NavigationDirection direction)
    {
        if (ReferenceEquals(focused, SideBackButton))
        {
            if (direction == NavigationDirection.Down)
                ThemePrevButton.Focus(NavigationMethod.Directional, KeyModifiers.None);
            return true;
        }

        var rows = new (Button Left, Button Right)[]
        {
            (ThemePrevButton, ThemeNextButton),
            (UiScaleMinusButton, UiScalePlusButton),
            (SubScaleMinusButton, SubScalePlusButton),
            (SubDelayMinusButton, SubDelayPlusButton),
            (AudioDelayMinusButton, AudioDelayPlusButton),
            (SpeedMinusButton, SpeedPlusButton)
        };

        for (var row = 0; row < rows.Length; row++)
        {
            var column = ReferenceEquals(focused, rows[row].Left) ? 0
                : ReferenceEquals(focused, rows[row].Right) ? 1
                : -1;
            if (column < 0) continue;

            if (direction == NavigationDirection.Up && row == 0)
            {
                SideBackButton.Focus(NavigationMethod.Directional, KeyModifiers.None);
                return true;
            }

            var targetRow = direction switch
            {
                NavigationDirection.Up => Math.Max(0, row - 1),
                NavigationDirection.Down => Math.Min(rows.Length - 1, row + 1),
                _ => row
            };
            var targetColumn = direction switch
            {
                NavigationDirection.Left => 0,
                NavigationDirection.Right => 1,
                _ => column
            };

            var target = targetColumn == 0 ? rows[targetRow].Left : rows[targetRow].Right;
            if (!ReferenceEquals(target, focused))
                target.Focus(NavigationMethod.Directional, KeyModifiers.None);
            return true;
        }

        return false;
    }

    private void MoveFocus(NavigationDirection direction)
    {
        EnsureDefaultFocus();
        var root = SourcePickerPanel.IsVisible ? (InputElement)SourcePickerPanel
            : SidePanel.IsVisible ? SidePanel
            : StatusPanel.IsVisible && ErrorButtons.IsVisible ? StatusPanel
            : VolumePanel.IsVisible ? VolumePanel
            : Osd;
        FocusManager?.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = root });
    }

    private void ActivateFocused()
    {
        EnsureDefaultFocus();
        var focused = FocusManager?.GetFocusedElement();
        if (ReferenceEquals(focused, VolumeSlider))
        {
            HideVolumePanel(restoreFocus: true);
            return;
        }
        if (focused is Button button && button.IsVisible && button.IsEnabled)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            return;
        }
        EnsureDefaultFocus();
    }

    private void EnsureDefaultFocus()
    {
        // A focused child can keep its own IsVisible/IsEnabled=true even when an
        // ancestor surface has just been hidden or disabled. Validate focus against
        // the currently active surface instead of trusting the focused control alone.
        if (SourcePickerPanel.IsVisible)
        {
            if (!SourcePickerPanel.IsKeyboardFocusWithin) FocusFirst(SourceFilesHost);
            return;
        }

        if (SidePanel.IsVisible)
        {
            if (SidePanel.IsKeyboardFocusWithin) return;
            if (_menu == MenuMode.Settings) ThemePrevButton.Focus(NavigationMethod.Directional, KeyModifiers.None);
            else FocusFirst(MenuItemsHost);
            return;
        }

        if (StatusPanel.IsVisible && ErrorButtons.IsVisible)
        {
            if (!StatusPanel.IsKeyboardFocusWithin) RetryButton.Focus(NavigationMethod.Directional, KeyModifiers.None);
            return;
        }

        if (VolumePanel.IsVisible)
        {
            if (!VolumePanel.IsKeyboardFocusWithin)
                VolumeSlider.Focus(NavigationMethod.Directional, KeyModifiers.None);
            return;
        }

        if (!Osd.IsKeyboardFocusWithin)
            PlayButton.Focus(NavigationMethod.Directional, KeyModifiers.None);
    }

    private static void FocusFirst(StackPanel host)
    {
        if (host.Children.OfType<Button>().FirstOrDefault() is { } button)
            button.Focus(NavigationMethod.Directional, KeyModifiers.None);
    }

    private void FocusFirstDeferred(StackPanel host)
    {
        if (host.Children.OfType<Button>().FirstOrDefault() is { } button) FocusDeferred(button);
    }

    private void FocusDeferred(Control control)
    {
        Dispatcher.Post(() =>
        {
            if (control.IsEffectivelyVisible && control.IsEffectivelyEnabled)
                control.Focus(NavigationMethod.Directional, KeyModifiers.None);
        });
    }

    private void ArmOrExit()
    {
        var now = DateTime.UtcNow;
        if (now <= _exitArmedUntil)
        {
            _log.Info("close requested: confirmed back action");
            Close();
            return;
        }
        _exitArmedUntil = now.AddSeconds(1.6);
        ShowToast("Назад ещё раз — выйти", 1500);
        RegisterActivity();
    }

    private void SettingAdjust_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string tag }) return;
        var parts = tag.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var dir)) return;
        dir = Math.Sign(dir);
        switch (parts[0])
        {
            case "theme":
                _settings.Theme = ThemeCatalog.Next(_settings.Theme, dir);
                ThemeCatalog.Apply(Application.Current, _settings.Theme);
                SettingsStore.Save(_settings, _log);
                break;
            case "ui":
                _settings.UiScale = Math.Clamp(Math.Round((_settings.UiScale + dir * .1) * 10) / 10, .9, 2.5);
                ApplyUiScale();
                SettingsStore.Save(_settings, _log);
                break;
            case "subscale": _engine?.SetSubtitleScale(_state.SubtitleScale + dir * .1); break;
            case "subdelay": _engine?.SetSubtitleDelay(_state.SubtitleDelay + dir * .1); break;
            case "audiodelay": _engine?.SetAudioDelay(_state.AudioDelay + dir * .1); break;
            case "speed": _engine?.SetSpeed(_state.Speed + dir * .05); break;
        }
        UpdateSettingValues();
        RegisterActivity();
    }

    private void ShowVolumePanel()
    {
        _volumePanelTimer.Stop();
        _syncingVolumeSlider = true;
        VolumeSlider.Value = Math.Clamp(_state.Volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
        _syncingVolumeSlider = false;
        VolumePanelValue.Text = $"{Math.Round(VolumeSlider.Value):0}%";
        VolumePanel.IsVisible = true;
        FocusDeferred(VolumeSlider);
    }

    private void HideVolumePanel(bool restoreFocus)
    {
        _volumePanelTimer.Stop();
        if (!VolumePanel.IsVisible) return;
        VolumePanel.IsVisible = false;
        if (restoreFocus && VolumeButton.IsEffectivelyVisible && VolumeButton.IsEffectivelyEnabled)
            FocusDeferred(VolumeButton);
    }

    private void ArmVolumePanelAutoHide()
    {
        _volumePanelTimer.Stop();
        _volumePanelTimer.Start();
    }

    private void VolumeSlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncingVolumeSlider || e.Property != Slider.ValueProperty || !VolumePanel.IsVisible) return;

        var value = Math.Clamp(VolumeSlider.Value, VolumeSlider.Minimum, VolumeSlider.Maximum);
        if (_state.Muted)
        {
            _engine?.SetMute(false);
            _state = _state with { Muted = false };
        }

        _engine?.SetVolume(value);
        _state = _state with { Volume = value };
        VolumePanelValue.Text = $"{Math.Round(value):0}%";
        VolumeText.Text = $"{Math.Round(value):0}%";
        VolumeButtonIcon.Kind = MaterialIconKind.VolumeHigh;
        RegisterActivity();
        if (!_volumeAdjusting) ArmVolumePanelAutoHide();
    }

    private void VolumeSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _volumeAdjusting = true;
        _volumePanelTimer.Stop();
        RegisterActivity();
    }

    private void VolumeSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _volumeAdjusting = false;
        RegisterActivity();
        ArmVolumePanelAutoHide();
    }

    private void UpdateSettingValues()
    {
        ThemeValue.Text = ThemeCatalog.Normalize(_settings.Theme);
        UiScaleValue.Text = $"{_settings.UiScale:0.0}×";
        SubScaleValue.Text = $"{_state.SubtitleScale:0.0}×";
        SubDelayValue.Text = $"{_state.SubtitleDelay:+0.0;-0.0;0.0} с";
        AudioDelayValue.Text = $"{_state.AudioDelay:+0.0;-0.0;0.0} с";
        SpeedValue.Text = $"{_state.Speed:0.00}×";
    }

    private void ApplyUiScale()
    {
        var s = _settings.UiScale;
        Resources["UiFontSmall"] = 14d * s;
        Resources["UiFont"] = 17d * s;
        Resources["UiFontLarge"] = 22d * s;
        Resources["UiFontXLarge"] = 28d * s;
        Resources["UiFontHuge"] = 38d * s;
        Resources["UiIcon"] = 22d * s;
        Resources["UiIconSmall"] = 18d * s;
        Resources["UiIconButtonSize"] = 44d * s;
        Resources["UiIconButtonPadding"] = new Thickness(10d * s);
        Resources["UiOsdMargin"] = new Thickness(32 * s, 70 * s, 32 * s, 24 * s);
        Resources["UiMenuPadding"] = new Thickness(24 * s);
        Resources["UiMenuWidth"] = Math.Min(1100d, 540d * s);
        Resources["UiSettingValueWidth"] = 110d * s;
        Resources["UiSliderHeight"] = 32d * s;

        // FluentTheme keeps the slider's internal rows/thumb at fixed pixel sizes.
        // Scale the template resources too: 7 + 18 + 7 == the 32px slider height.
        Resources["SliderPreContentMargin"] = new GridLength(7d * s);
        Resources["SliderPostContentMargin"] = new GridLength(7d * s);
        Resources["SliderHorizontalHeight"] = 32d * s;
        Resources["SliderHorizontalThumbWidth"] = 18d * s;
        Resources["SliderHorizontalThumbHeight"] = 18d * s;
        Resources["SliderTrackThemeHeight"] = 4d * s;
        Resources["SliderThumbCornerRadius"] = new CornerRadius(9d * s);

        Resources["UiTimeWidth"] = 75d * s;
        Resources["UiButtonPadding"] = new Thickness(16 * s, 10 * s, 16 * s, 10 * s);
        Resources["UiButtonMargin"] = new Thickness(4 * s);
        Resources["UiListPadding"] = new Thickness(16 * s, 12 * s, 16 * s, 12 * s);
        Resources["UiSettingPadding"] = new Thickness(14 * s);
        Resources["UiSettingMargin"] = new Thickness(0, 3 * s, 0, 3 * s);
        Resources["UiToastPadding"] = new Thickness(28 * s, 20 * s, 28 * s, 20 * s);
        Resources["UiBufferPadding"] = new Thickness(22 * s, 14 * s, 22 * s, 14 * s);
        Resources["UiInfoPillPadding"] = new Thickness(12 * s, 7 * s);
        Resources["UiInfoPillMargin"] = new Thickness(16 * s, 0);
        Resources["UiInfoPillSpacing"] = 12d * s;
        Resources["UiVolumePanelWidth"] = Math.Min(650d, 240d * s);
        Resources["UiVolumePanelMargin"] = new Thickness(0, 0, 16d * s, 84d * s);
        Resources["UiVolumePanelPadding"] = new Thickness(16d * s, 12d * s);
        Resources["UiStatusPadding"] = new Thickness(36 * s);
        Resources["UiStatusWidth"] = Math.Min(1200d, 780d * s);
    }

    private double CurrentFont(string key) => Resources[key] is double d ? d : 24d;

    private void Progress_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Progress).Properties.IsLeftButtonPressed) return;
        CancelSeekPreview();
        _pendingSeekTarget = null;
        _dragging = true;
        RegisterActivity();
        SetSliderFromPointer(e.GetPosition(Progress).X);
        e.Pointer.Capture(Progress);
        e.Handled = true;
    }

    private void Progress_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        SetSliderFromPointer(e.GetPosition(Progress).X);
        _dragging = false;
        e.Pointer.Capture(null);
        _engine?.SeekAbsolute(Progress.Value);
        RegisterActivity();
        e.Handled = true;
    }

    private void SetSliderFromPointer(double x)
    {
        if (Progress.Bounds.Width <= 1) return;
        Progress.Value = Progress.Minimum + (Progress.Maximum - Progress.Minimum) * Math.Clamp(x / Progress.Bounds.Width, 0, 1);
        PositionText.Text = Time(Progress.Value);
    }

    private void Window_PointerMoved(object? sender, PointerEventArgs e) => RegisterPointerActivity(e.GetPosition(Root));
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e) => RegisterActivity();

    private void Window_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_menu != MenuMode.None || SourcePickerPanel.IsVisible) return;
        if (VolumePanel.IsVisible)
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta.Y > 0 ? 5d : -5d), VolumeSlider.Minimum, VolumeSlider.Maximum);
            e.Handled = true;
            return;
        }
        _engine?.AddVolume(e.Delta.Y > 0 ? 5 : -5);
        RegisterActivity();
    }

    private void Window_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_menu == MenuMode.None && !SourcePickerPanel.IsVisible) ToggleFullscreen();
    }

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        var added = 0;
        foreach (var item in files)
        {
            var path = item.Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path)) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".torrent")
            {
                ShowToast("Torrent нужно передать при запуске через --torrent", 1800);
                continue;
            }
            if (_engine is null) continue;
            if (ext is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub") { _engine.AddSubtitle(path); added++; }
            else if (ext is ".aac" or ".ac3" or ".eac3" or ".dts" or ".flac" or ".mka" or ".mp3" or ".wav") { _engine.AddAudio(path); added++; }
        }
        if (added > 0) ShowToast($"Добавлено файлов: {added}", 1400);
        RegisterActivity();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        RegisterActivity();

        if (e.Key == Key.Escape)
        {
            if (VolumePanel.IsVisible) HideVolumePanel(restoreFocus: true);
            else if (_menu != MenuMode.None) CloseMenu();
            else ArmOrExit();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            HandleDirectionalInput(e.Key switch
            {
                Key.Left => NavigationDirection.Left,
                Key.Right => NavigationDirection.Right,
                Key.Up => NavigationDirection.Up,
                _ => NavigationDirection.Down
            });
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            if (FocusManager?.GetFocusedElement() is null) EnsureDefaultFocus();
            return;
        }

        if (e.Key == Key.Enter)
        {
            ActivateFocused();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && FocusManager?.GetFocusedElement() is not Button)
        {
            DoPause();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.P: DoPause(); e.Handled = true; break;
            case Key.M: _engine?.ToggleMute(); e.Handled = true; break;
            case Key.F:
            case Key.F11: ToggleFullscreen(); e.Handled = true; break;
            case Key.Q: Close(); e.Handled = true; break;
            case Key.OemOpenBrackets: _engine?.SetSubtitleDelay(_state.SubtitleDelay - .1); e.Handled = true; break;
            case Key.OemCloseBrackets: _engine?.SetSubtitleDelay(_state.SubtitleDelay + .1); e.Handled = true; break;
        }
    }

    private void Window_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            var handled = _seekPreviewActive && _seekPreviewDirection == -1;
            CommitSeekPreview(-1);
            e.Handled = handled;
        }
        else if (e.Key == Key.Right)
        {
            var handled = _seekPreviewActive && _seekPreviewDirection == 1;
            CommitSeekPreview(1);
            e.Handled = handled;
        }
    }

    private void ToggleFullscreen()
    {
        if (WindowState != WindowState.FullScreen)
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            CanResize = false;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
            CanResize = true;
            Width = 1280;
            Height = 720;
        }
        RegisterActivity();
    }

    private void ShowError(string title, string detail)
    {
        SourcePickerPanel.IsVisible = false;
        SidePanel.IsVisible = false;
        _menu = MenuMode.None;
        StatusPanel.IsVisible = true;
        StatusProgress.IsVisible = false;
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        ErrorButtons.IsVisible = true;
        Osd.IsEnabled = false;
        Cursor = Avalonia.Input.Cursor.Default;
        RegisterActivity();
        FocusDeferred(RetryButton);
    }

    public void ShowFatalError(string title, string detail) => ShowError(title, detail);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_cleanupRunning) return;
        _cleanupRunning = true;
        _closing = true;
        _ = CleanupAndCloseAsync();
    }

    private async Task CleanupAndCloseAsync()
    {
        try
        {
            if (_reporterStarted) _reporter.Emit("stopped", _state);
            _uiTimer.Stop();
            _progressTimer.Stop();
            _seekPreviewTimer.Stop();
            _gamepad.Dispose();
            _powerKeepAlive.Dispose();

            try { await VideoGl.ReleaseRendererAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { _log.Exception(ex, "OpenGL renderer shutdown"); }

            try { await _reporter.FlushAsync(TimeSpan.FromSeconds(1)); } catch { }

            var engine = _engine;
            _engine = null;
            VideoGl.Detach();
            if (engine is not null)
            {
                try { await Task.Run(engine.Dispose); }
                catch (Exception ex) { _log.Exception(ex, "Engine shutdown"); }
            }

            if (_torrent is not null)
            {
                try { await _torrent.DisposeAsync(); }
                catch (Exception ex) { _log.Exception(ex, "Torrent shutdown"); }
                _torrent = null;
            }

            await _reporter.DisposeAsync();
            _log.Info("window closed");
        }
        finally
        {
            _allowClose = true;
            Dispatcher.Post(Close);
        }
    }

    private static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
