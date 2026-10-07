using System.Collections.ObjectModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace WolfSpeak;

sealed record DeviceItem(string? Id, string Name)
{
    public override string ToString() => Name;
}

sealed record KeyItem(int Vk, string Name)
{
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    static readonly KeyItem[] PttKeys =
    [
        new(0x05, "Mouse 4"), new(0x06, "Mouse 5"), new(0x04, "Middle mouse"),
        new(0x14, "Caps Lock"), new(0xA4, "Left Alt"), new(0xA2, "Left Ctrl"), new(0xA0, "Left Shift"),
        new(0xC0, "` (tilde)"), new(0x56, "V"), new(0x42, "B"), new(0x54, "T"), new(0x58, "X"),
    ];

    static readonly Brush ControlBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x33)));
    static readonly Brush DangerFill = Frozen(new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48)));

    readonly VoiceEngine engine;
    readonly Settings settings = Settings.Load();
    readonly ObservableCollection<PeerVm> peers = [];
    readonly ObservableCollection<string> savedIps = [];
    readonly DispatcherTimer frameTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly TrayIcon tray = new();
    readonly DeviceWatcher deviceWatcher = new();
    readonly DispatcherTimer deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly Updater updater = new();
    bool quitting;

    int shownPeerVersion = -1;
    bool loading = true;
    bool settingsOpen;
    CallState shownState = (CallState)(-1);
    long nextRingSoundMs;
    IPAddress? pendingCallIp;
    long pendingCallUntilMs;
    string shownName = "";
    double micLevel, meRing, partnerRing;
    long nextDelayInfoMs;
    long nextLoadingDotsMs;
    int loadingDotCount = 3;
    bool searchAnimating;

    public MainWindow(VoiceEngine engine)
    {
        this.engine = engine;
        InitializeComponent();

        PeerList.ItemsSource = peers;
        IpList.ItemsSource = savedIps;
        PttKeyBox.ItemsSource = PttKeys;

        LoadDevices();
        ApplySettings();
        WireEvents();
        loading = false;
        RestartAudio();

        var ips = VoiceEngine.LocalAddresses();
        HomeMeSub.Text = ips.Count > 0 ? $"Online · {ips[0]}" : "No network connection";
        AboutText.Text = $"WolfSpeak {VoiceEngine.AppVersion}\nYour address: {string.Join(", ", ips)}\nUDP port {VoiceEngine.Port} · direct, no servers";

        engine.Error += msg => Dispatcher.BeginInvoke(() => ShowNotice(msg));
        engine.Notice += msg => Dispatcher.BeginInvoke(() => ShowNotice(msg));
        toastTimer.Tick += (_, _) => HideToast();
        frameTimer.Tick += (_, _) => OnFrame();
        frameTimer.Start();
        updateTimer.Tick += (_, _) => OnUpdateTimer();
        updateTimer.Start();

        tray.OpenRequested += ShowFromTray;
        tray.ToggleMuteRequested += ToggleMute;
        tray.HangUpRequested += engine.HangUp;
        tray.QuitRequested += Quit;

        // Headset unplugged / plugged in / Windows default changed → recover automatically.
        deviceDebounce.Tick += (_, _) => { deviceDebounce.Stop(); OnDevicesChanged(); };
        deviceWatcher.Changed += () => Dispatcher.BeginInvoke(() => { deviceDebounce.Stop(); deviceDebounce.Start(); });

        RestoreWindowPosition();
        IsVisibleChanged += (_, _) =>
            frameTimer.Interval = TimeSpan.FromMilliseconds(IsVisible ? 30 : 200); // idle cheaply in the tray

        SourceInitialized += (_, _) => { StyleWindowFrame(); RegisterHotkeys(); };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) { HideToTray(); return; }
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
        Closing += (_, e) =>
        {
            if (!quitting && settings.CloseToTray)
            {
                e.Cancel = true; // Alt+F4 / taskbar close: keep running in the tray
                HideToTray();
                return;
            }
            frameTimer.Stop();
            SaveWindowPosition();
            settings.Save();
            deviceWatcher.Dispose();
            tray.Dispose();
        };
    }

    // ------------------------------------------------------------------ mute / deafen / hotkeys

    void ToggleMute()
    {
        engine.Muted = !engine.Muted;
        engine.PlaySound(engine.Muted ? SoundKind.MuteOn : SoundKind.MuteOff);
        UpdateCallButtons();
    }

    void ToggleDeafen()
    {
        engine.Deafened = !engine.Deafened;
        engine.PlaySound(engine.Deafened ? SoundKind.MuteOn : SoundKind.MuteOff);
        UpdateCallButtons();
    }

    const int HotkeyMute = 1, HotkeyDeafen = 2, WM_HOTKEY = 0x0312;
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    void RegisterHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Global: work while a game has focus. Silently skipped if another app already owns the combo.
        RegisterHotKey(hwnd, HotkeyMute, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'M');
        RegisterHotKey(hwnd, HotkeyDeafen, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'D');
        HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY)
            {
                if (wParam == HotkeyMute) ToggleMute();
                else if (wParam == HotkeyDeafen) ToggleDeafen();
                handled = true;
            }
            return IntPtr.Zero;
        });
        Closed += (_, _) => { UnregisterHotKey(hwnd, HotkeyMute); UnregisterHotKey(hwnd, HotkeyDeafen); };
    }

    // ------------------------------------------------------------------ devices

    void OnDevicesChanged()
    {
        // Sonar / Windows can fire several changes while re-routing; whatever happens here must never take the app down.
        try { LoadDevices(); }
        catch (Exception ex) { Log.Write("Refreshing device list failed", ex); }
        RestartAudio();
    }

    // ------------------------------------------------------------------ window position

    void RestoreWindowPosition()
    {
        if (settings.WindowLeft is not double left || settings.WindowTop is not double top) return;
        // Only if it's still on a connected screen.
        if (left < SystemParameters.VirtualScreenLeft || top < SystemParameters.VirtualScreenTop ||
            left + 100 > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth ||
            top + 100 > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    void SaveWindowPosition()
    {
        if (WindowState != WindowState.Normal || !IsLoaded) return;
        settings.WindowLeft = Left;
        settings.WindowTop = Top;
    }

    // ------------------------------------------------------------------ tray

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliably jump in front of other windows
        Topmost = false;
        Focus();
    }

    void HideToTray()
    {
        SaveWindowPosition();
        Hide();
        WindowState = WindowState.Normal;
        settings.Save();
        if (!settings.TrayHintShown)
        {
            settings.TrayHintShown = true;
            tray.Notify("WolfSpeak is still running", "Your pack can still call you. Right-click the wolf here to quit.");
        }
    }

    void Quit()
    {
        quitting = true;
        engine.HangUp();
        Close();
    }

    /// <summary>
    /// Keeps the whole pack on the same version: downloads new releases in the background and installs
    /// them (quick restart) as soon as we're not in a call. Checks sooner if a friend is already ahead of us.
    /// </summary>
    async void OnUpdateTimer()
    {
        bool friendAhead = engine.Peers.Any(p => Updater.IsNewerThanUs(p.Version));
        if (!await updater.CheckAsync(urgent: friendAhead)) return;
        if (engine.State != CallState.Idle || quitting) return; // never interrupt a call; retry next tick

        updateTimer.Stop();
        Log.Write($"Installing update {updater.Ready!.Version}");
        try
        {
            updater.ApplyAfterExit(IsVisible ? [] : [Autostart.TrayArgument]);
            Quit();
        }
        catch (Exception ex)
        {
            Log.Write("Could not apply update", ex);
        }
    }

    void UpdateTray(CallState state)
    {
        string name = engine.Partner?.Name ?? "friend";
        bool muted = engine.Muted;
        var (status, tip) = state switch
        {
            CallState.Connected => (muted ? TrayStatus.Muted : TrayStatus.InCall, $"WolfSpeak · in call with {name}{(muted ? " (muted)" : "")}"),
            CallState.Ringing => (TrayStatus.Ringing, $"WolfSpeak · {name} is calling"),
            CallState.Calling => (TrayStatus.Ringing, $"WolfSpeak · calling {name}…"),
            _ => (muted ? TrayStatus.Muted : TrayStatus.Idle, muted ? "WolfSpeak · mic muted" : "WolfSpeak · ready"),
        };
        tray.Update(status, tip, muted);
    }

    /// <summary>Toast in the window, or a tray notification while hidden.</summary>
    public void ShowError(string message) => ShowNotice(message);

    void OnDismissVirtualHint(object sender, RoutedEventArgs e)
    {
        settings.HideVirtualHint = true;
        settings.Save();
        VirtualHint.Visibility = Visibility.Collapsed;
    }

    void ShowNotice(string message)
    {
        if (IsVisible) ShowToast(message);
        else tray.Notify("WolfSpeak", message);
    }

    // ------------------------------------------------------------------ setup

    bool refreshingDevices;

    void LoadDevices()
    {
        refreshingDevices = true;
        try
        {
            using var en = new MMDeviceEnumerator();
            Fill(MicBox, en, DataFlow.Capture, settings.MicId);
            Fill(OutBox, en, DataFlow.Render, settings.OutputId);
        }
        finally { refreshingDevices = false; }

        static void Fill(ComboBox box, MMDeviceEnumerator en, DataFlow flow, string? selectedId)
        {
            string defaultName;
            try { defaultName = ShortDeviceName(en.GetDefaultAudioEndpoint(flow, VoiceEngine.DefaultRole).FriendlyName); }
            catch { defaultName = "none"; }
            var items = new List<DeviceItem> { new(null, $"Default · {defaultName}") };
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                // A device that is being torn down (e.g. Sonar re-routing) can fail to report its name — skip it.
                try { items.Add(new DeviceItem(d.ID, d.FriendlyName)); } catch { }
            }
            box.ItemsSource = items;
            // Keep the user's pick even if it's momentarily gone; it will be retried when it comes back.
            int index = items.FindIndex(d => d.Id == selectedId);
            if (index < 0 && selectedId is not null)
            {
                items.Add(new DeviceItem(selectedId, "Disconnected device"));
                index = items.Count - 1;
            }
            box.SelectedIndex = Math.Max(0, index);
        }
    }

    void ApplySettings()
    {
        NameBox.Text = settings.Name;
        TraySwitch.IsChecked = settings.CloseToTray;
        UltraSwitch.IsChecked = settings.UltraLowLatency;
        engine.UltraLowLatency = settings.UltraLowLatency;
        StartupSwitch.IsChecked = Autostart.IsEnabled;
        engine.Name = settings.Name;

        PttMode.IsChecked = settings.PushToTalk;
        VoiceMode.IsChecked = !settings.PushToTalk;
        PttKeyBox.SelectedItem = PttKeys.FirstOrDefault(k => k.Vk == settings.PttKey) ?? PttKeys[0];
        engine.PushToTalk = settings.PushToTalk;
        engine.PttKey = ((KeyItem)PttKeyBox.SelectedItem!).Vk;
        UpdateModeUi();

        GateSlider.Value = Math.Clamp(settings.GateDb, -70, 0);
        GainSlider.Value = Math.Clamp(settings.MicGainPct, 25, 400);
        VolSlider.Value = Math.Clamp(settings.VolumePct, 0, 200);
        BufSlider.Value = Math.Clamp(settings.BufferMs, 5, 100);
        BufAutoSwitch.IsChecked = settings.BufferAuto;
        engine.BufferAuto = settings.BufferAuto;
        BufSlider.IsEnabled = !settings.BufferAuto;
        OnSliders();

        foreach (var s in settings.ManualIps)
            if (IPAddress.TryParse(s, out var ip))
            {
                engine.AddManualTarget(ip);
                savedIps.Add(s);
            }
        SavedIpsSection.Visibility = savedIps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void WireEvents()
    {
        NameBox.TextChanged += (_, _) =>
        {
            settings.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? Environment.UserName : NameBox.Text.Trim();
            engine.Name = settings.Name;
        };
        // Ignore the transient (empty) selections a list refresh produces; only user picks count.
        MicBox.SelectionChanged += (_, _) =>
        {
            if (refreshingDevices || MicBox.SelectedItem is not DeviceItem d) return;
            settings.MicId = d.Id;
            RestartAudio();
        };
        OutBox.SelectionChanged += (_, _) =>
        {
            if (refreshingDevices || OutBox.SelectedItem is not DeviceItem d) return;
            settings.OutputId = d.Id;
            RestartAudio();
        };
        PttMode.Checked += (_, _) => { settings.PushToTalk = engine.PushToTalk = true; UpdateModeUi(); };
        VoiceMode.Checked += (_, _) => { settings.PushToTalk = engine.PushToTalk = false; UpdateModeUi(); };
        PttKeyBox.SelectionChanged += (_, _) => { settings.PttKey = engine.PttKey = ((KeyItem)PttKeyBox.SelectedItem).Vk; UpdateModeUi(); };
        LoopSwitch.Checked += (_, _) => engine.Loopback = true;
        UltraSwitch.Checked += (_, _) => SetUltraLowLatency(true);
        UltraSwitch.Unchecked += (_, _) => SetUltraLowLatency(false);
        LoopSwitch.Unchecked += (_, _) => engine.Loopback = false;
        TraySwitch.Checked += (_, _) => settings.CloseToTray = true;
        TraySwitch.Unchecked += (_, _) => settings.CloseToTray = false;
        StartupSwitch.Checked += (_, _) => Autostart.Set(true);
        StartupSwitch.Unchecked += (_, _) => Autostart.Set(false);
        GateSlider.ValueChanged += (_, _) => OnSliders();
        GainSlider.ValueChanged += (_, _) => OnSliders();
        VolSlider.ValueChanged += (_, _) => OnSliders();
        BufSlider.ValueChanged += (_, _) => OnSliders();
        BufAutoSwitch.Checked += (_, _) => SetBufferAuto(true);
        BufAutoSwitch.Unchecked += (_, _) => SetBufferAuto(false);
        MeterHost.SizeChanged += (_, _) => OnSliders();
    }

    string PttKeyName => (PttKeyBox.SelectedItem as KeyItem)?.Name ?? "your key";

    void UpdateModeUi()
    {
        bool ptt = PttMode.IsChecked == true;
        PttKeyBox.Visibility = ptt ? Visibility.Visible : Visibility.Collapsed;
        GatePanel.Visibility = ptt ? Visibility.Collapsed : Visibility.Visible;
        ModeHint.Text = ptt ? $"Push-to-talk · hold {PttKeyName} to talk" : "Voice activated · just talk";
    }

    void OnSliders()
    {
        if (GateLabel is null) return;
        settings.GateDb = (int)Math.Round(GateSlider.Value);
        settings.MicGainPct = (int)Math.Round(GainSlider.Value);
        settings.VolumePct = (int)Math.Round(VolSlider.Value);
        settings.BufferMs = (int)Math.Round(BufSlider.Value / 5) * 5;

        engine.GateDb = settings.GateDb;
        engine.MicGain = settings.MicGainPct / 100f;
        engine.OutputVolume = settings.VolumePct / 100f;
        engine.BufferMs = settings.BufferMs;

        GateLabel.Text = $"{settings.GateDb} dB".Replace('-', '−');
        GainValue.Text = $"{settings.MicGainPct}%";
        VolValue.Text = $"{settings.VolumePct}%";
        UpdateDelayInfo();
        GateShade.Width = Math.Max(0, (settings.GateDb + 70) / 70.0 * (MeterHost.ActualWidth - 2));
    }

    void SetBufferAuto(bool on)
    {
        settings.BufferAuto = on;
        engine.BufferAuto = on;
        if (!on) engine.BufferMs = settings.BufferMs;
        BufSlider.IsEnabled = !on;
        UpdateDelayInfo();
    }

    void SetUltraLowLatency(bool on)
    {
        settings.UltraLowLatency = on;
        engine.UltraLowLatency = on;
        settings.Save();
        RestartAudio();
    }

    void UpdateDelayInfo()
    {
        if (DelayTotal is null) return;
        int glitches = engine.Glitches;
        GlitchText.Text = glitches == 0 ? "no glitches" : glitches == 1 ? "1 glitch" : $"{glitches} glitches";
        GlitchText.Foreground = (Brush)FindResource(glitches == 0 ? "FaintTextBrush" : "AmberBrush");
        int buf = engine.CurrentBufferMs;
        BufValue.Text = settings.BufferAuto ? $"Auto · {buf} ms" : $"{buf} ms";
        DelayTotal.Text = $"≈ {engine.EstimatedDelayMs:0} ms delay";
        DelayBreakdown.Text =
            $"WolfSpeak delay ≈ {engine.EstimatedDelayMs:0} ms\n" +
            $"Mic {engine.CaptureMs:0.#} ms · packet {VoiceEngine.FrameSamples * 1000 / VoiceEngine.SampleRate} ms · " +
            $"buffer {buf} ms · headphones {engine.RenderMs:0.#} ms" +
            (engine.IsLowLatency ? " · low-latency mode ✓"
             : !engine.UltraLowLatency ? " · standard mode (ultra-low latency is off)"
             : VoiceEngine.IsVirtualDevice(engine.MicName) || VoiceEngine.IsVirtualDevice(engine.OutputName)
                ? " · standard mode (virtual devices distort in low-latency mode)"
             : " · standard mode (driver has no low-latency support)") +
            "\n\nWireless headsets and virtual devices add their own delay on top.\n\n" + engine.AudioInfo;
    }


    void UpdateVirtualHint()
    {
        var names = new[] { engine.MicName, engine.OutputName }
            .Where(VoiceEngine.IsVirtualDevice)
            .Select(ShortDeviceName)
            .ToList();
        VirtualHint.Visibility = names.Count > 0 && !settings.HideVirtualHint ? Visibility.Visible : Visibility.Collapsed;
        VirtualHint.ToolTip = $"{engine.MicName}\n{engine.OutputName}";
        if (names.Count > 0)
            VirtualHintText.Text = $"{string.Join(" + ", names)} {(names.Count > 1 ? "are virtual devices and add" : "is a virtual device and adds")} " +
                                   "delay. Pick your headset directly for the lowest delay.";
    }

    /// <summary>"SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)" → "SteelSeries Sonar - Microphone".</summary>
    static string ShortDeviceName(string name)
    {
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    void RestartAudio()
    {
        if (loading) return;
        try
        {
            if (engine.StartAudio(settings.MicId, settings.OutputId) is { } note)
                ShowNotice(note);
            UpdateVirtualHint();
            UpdateDelayInfo();
        }
        catch (Exception ex)
        {
            ShowNotice("Could not open audio device: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ per frame

    void OnFrame()
    {
        long now = Environment.TickCount64;
        var state = engine.State;
        if (state != shownState) OnStateChanged(shownState, state);
        UpdateTray(state);

        // mic level (fast attack, slow release) for all meters
        double target = Math.Clamp((engine.MicLevelDb + 70) / 70.0, 0, 1);
        micLevel = target > micLevel ? micLevel + (target - micLevel) * 0.6 : micLevel * 0.9 + target * 0.1;
        if (SettingsView.Visibility == Visibility.Visible)
            MeterFill.Width = Math.Max(0, micLevel * (MeterHost.ActualWidth - 6));
        if (IsVisible && now >= nextDelayInfoMs)
        {
            nextDelayInfoMs = now + 500;
            UpdateDelayInfo();
        }

        if (settings.Name != shownName)
        {
            shownName = settings.Name;
            HomeMeName.Text = shownName;
            HomeMeInitials.Text = CallMeInitials.Text = PeerVm.MakeInitials(shownName);
            HomeMeAvatar.Fill = CallMeAvatar.Fill = PeerVm.MakeAvatar(shownName);
        }

        switch (state)
        {
            case CallState.Idle:
                HomeMeterFill.Width = micLevel * HomeMeterHost.ActualWidth;
                SyncPeers();
                TryPendingIpCall(now);
                break;

            case CallState.Calling:
            case CallState.Ringing:
                UpdatePartnerHeader(RingAvatar, RingInitials, RingName);
                if (now >= nextRingSoundMs)
                {
                    engine.PlaySound(state == CallState.Ringing ? SoundKind.Ring : SoundKind.Ringback);
                    nextRingSoundMs = now + (state == CallState.Ringing ? 2200 : 2600);
                }
                break;

            case CallState.Connected:
                UpdateCallView(now);
                break;
        }

        if (EmptyState.IsVisible && now >= nextLoadingDotsMs)
        {
            loadingDotCount = (loadingDotCount + 1) % 4;
            LoadingDots.Text = new string('.', loadingDotCount);
            nextLoadingDotsMs = now + 420;
        }
        UpdateSearchAnimation();
    }

    void OnStateChanged(CallState from, CallState to)
    {
        shownState = to;
        FrameworkElement view = to switch
        {
            CallState.Idle => HomeView,
            CallState.Connected => CallView,
            _ => RingView,
        };
        foreach (var v in new FrameworkElement[] { HomeView, RingView, CallView })
            if (v != view) v.Visibility = Visibility.Collapsed;
        ShowWithFade(view);

        SetPulses(to is CallState.Calling or CallState.Ringing, RingPulse1, RingPulse2, RingPulse3);
        SetLinkAnimation(to == CallState.Connected);

        switch (to)
        {
            case CallState.Ringing:
                RingStatus.Text = "is howling at you — wants to talk";
                DeclineLabel.Text = "Decline";
                AcceptPanel.Visibility = Visibility.Visible;
                nextRingSoundMs = 0;
                BringToFront();
                break;
            case CallState.Calling:
                RingStatus.Text = "Calling…";
                DeclineLabel.Text = "Cancel";
                AcceptPanel.Visibility = Visibility.Collapsed;
                nextRingSoundMs = 0;
                break;
            case CallState.Connected:
                CheckPartnerKey();
                engine.PlaySound(SoundKind.Connected);
                engine.Deafened = false;
                UpdateCallButtons();
                break;
            case CallState.Idle:
                if (from == CallState.Connected) engine.PlaySound(SoundKind.Ended);
                engine.Deafened = false;
                shownPeerVersion = -1; // refresh list
                break;
        }
        if (settingsOpen && to != CallState.Idle && from == CallState.Idle) ToggleSettings(false);
    }

    /// <summary>
    /// Remembers each friend's key (trust on first use). If someone with the same name shows up with a
    /// different key, that's either a reinstall or an impostor — say so, and point at the safety code.
    /// </summary>
    void CheckPartnerKey()
    {
        if (engine.Partner is not { } p || engine.PartnerFingerprint is not { } fp) return;
        if (settings.KnownKeys.TryGetValue(p.Name, out var known) && known != fp)
            ShowNotice($"⚠ {p.Name}'s security key changed since your last call. If they didn't reinstall WolfSpeak, " +
                       "compare the safety code (top of the call screen) out loud before talking.");
        if (known != fp)
        {
            settings.KnownKeys[p.Name] = fp;
            settings.Save();
        }
    }

    void UpdatePartnerHeader(Ellipse avatar, TextBlock initials, TextBlock name)
    {
        var p = engine.Partner;
        if (p is null || name.Text == p.Name) return;
        name.Text = p.Name;
        initials.Text = PeerVm.MakeInitials(p.Name);
        avatar.Fill = PeerVm.MakeAvatar(p.Name);
    }

    void UpdateCallView(long now)
    {
        UpdatePartnerHeader(CallPartnerAvatar, CallPartnerInitials, CallPartnerName);

        var elapsed = TimeSpan.FromMilliseconds(now - engine.ConnectedAtMs);
        CallTimer.Text = $"Connected · {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        SafetyCodeText.Text = engine.SafetyCode ?? "";

        double rtt = engine.RttMs;
        double loss = engine.LossPercent;
        string ping = rtt < 0 ? "Ping –" : rtt < 1 ? "Ping <1 ms" : $"Ping {rtt:0} ms";
        PingText.Text = loss >= 0.5 ? $"{ping} · {loss:0}% loss" : ping;
        string quality = loss >= 5 || rtt > 80 ? "DangerBrush" : loss >= 1 || rtt > 30 ? "AmberBrush" : "TalkBrush";
        QualityDot.Fill = (Brush)FindResource(quality);
        PingPill.ToolTip = $"Voice delay ≈ {engine.EstimatedDelayMs + Math.Max(0, rtt) / 2:0} ms in WolfSpeak (headset hardware adds more)\n" +
                           $"Packet loss {loss:0.0}% · buffer {engine.Partner?.Buffer.BufferedMs ?? 0} ms";

        // me
        bool muted = engine.Muted, tx = engine.Transmitting;
        double meTarget = tx && !muted ? 0.35 + Math.Clamp((micLevel - 0.3) / 0.5, 0, 1) * 0.65 : 0;
        meRing += (meTarget - meRing) * 0.3;
        MeRing.Opacity = meRing;
        MeRingScale.ScaleX = MeRingScale.ScaleY = 0.86 + meRing * 0.14;
        MeMutedBadge.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        SetState(MeState, muted ? ("Muted", "DangerBrush")
            : tx ? ("Speaking", "TalkBrush")
            : engine.PushToTalk ? ($"Hold {PttKeyName}", "SubTextBrush")
            : ("Mic on", "SubTextBrush"));

        // partner
        var p = engine.Partner;
        bool talking = p is not null && now - p.LastAudio < 250;
        double pTarget = talking ? 0.35 + p!.Level * 0.65 : 0;
        partnerRing += (pTarget - partnerRing) * 0.3;
        PartnerRing.Opacity = partnerRing;
        PartnerRingScale.ScaleX = PartnerRingScale.ScaleY = 0.86 + partnerRing * 0.14;
        SetState(PartnerState, engine.Deafened ? ("You deafened them", "DangerBrush")
            : talking ? ("Speaking", "TalkBrush")
            : ("Connected", "SubTextBrush"));
    }

    void SetState(TextBlock block, (string Text, string Brush) s)
    {
        if (block.Text == s.Text) return;
        block.Text = s.Text;
        block.Foreground = (Brush)FindResource(s.Brush);
    }

    void UpdateCallButtons()
    {
        bool muted = engine.Muted, deaf = engine.Deafened;
        MuteButton.Background = muted ? DangerFill : ControlBrush;
        MuteButton.Content = muted ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute microphone" : "Mute microphone";
        DeafenButton.Background = deaf ? DangerFill : ControlBrush;
        DeafenButton.Content = deaf ? "" : "";
        DeafenButton.ToolTip = deaf ? "Undeafen" : "Deafen (stop hearing your friend)";
    }

    void SyncPeers()
    {
        int version = engine.PeerVersion;
        if (version != shownPeerVersion)
        {
            shownPeerVersion = version;
            var live = engine.Peers.ToDictionary(p => p.Id);
            for (int i = peers.Count - 1; i >= 0; i--)
                if (!live.TryGetValue(peers[i].Id, out var p) || !ReferenceEquals(p, peers[i].Peer))
                    peers.RemoveAt(i);
            foreach (var p in live.Values)
                if (peers.All(vm => vm.Id != p.Id))
                    peers.Add(new PeerVm(p));

            CountText.Text = peers.Count.ToString();
            bool empty = peers.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var vm in peers) vm.Update();
    }

    void TryPendingIpCall(long now)
    {
        if (pendingCallIp is null) return;
        var peer = engine.Peers.FirstOrDefault(p => p.EndPoint.Address.Equals(pendingCallIp));
        if (peer is not null)
        {
            pendingCallIp = null;
            HideToast();
            engine.Call(peer.Id);
        }
        else if (now > pendingCallUntilMs)
        {
            ShowToast($"No WolfSpeak found at {pendingCallIp}. Is it open there, and allowed through the firewall?");
            pendingCallIp = null;
        }
    }

    // ------------------------------------------------------------------ animation helpers

    void UpdateSearchAnimation()
    {
        bool shouldAnimate = IsVisible && HomeView.IsVisible && EmptyState.IsVisible;
        if (shouldAnimate == searchAnimating) return;
        searchAnimating = shouldAnimate;
        SetPulses(shouldAnimate, SearchPulse1, SearchPulse2);
    }

    void ShowWithFade(FrameworkElement view)
    {
        view.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        if (view.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    static void SetPulses(bool on, params Ellipse[] rings)
    {
        for (int i = 0; i < rings.Length; i++)
        {
            var ring = rings[i];
            var scale = (ScaleTransform)ring.RenderTransform;
            if (!on)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ring.BeginAnimation(OpacityProperty, null);
                ring.Opacity = 0;
                continue;
            }
            var period = TimeSpan.FromSeconds(2.4);
            var delay = TimeSpan.FromSeconds(2.4 * i / rings.Length);
            DoubleAnimation Anim(double from, double to) => new(from, to, period)
            {
                BeginTime = delay,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.45, 1));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.45, 1));
            ring.BeginAnimation(OpacityProperty, Anim(0.9, 0));
        }
    }

    void SetLinkAnimation(bool on)
    {
        var dur = TimeSpan.FromSeconds(1.4);
        LinkDot1.BeginAnimation(Canvas.LeftProperty, on ? new DoubleAnimation(0, 56, dur) { RepeatBehavior = RepeatBehavior.Forever } : null);
        LinkDot2.BeginAnimation(Canvas.LeftProperty, on ? new DoubleAnimation(56, 0, dur) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }

    // ------------------------------------------------------------------ handlers

    void OnCallPeerClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is PeerVm vm) engine.Call(vm.Id);
    }

    void OnAcceptClick(object sender, RoutedEventArgs e) => engine.Accept();
    void OnHangUpClick(object sender, RoutedEventArgs e) => engine.HangUp();

    void OnMicClick(object sender, RoutedEventArgs e) => ToggleMute();
    void OnDeafenClick(object sender, RoutedEventArgs e) => ToggleDeafen();

    void OnSettingsClick(object sender, RoutedEventArgs e) => ToggleSettings(!settingsOpen);

    void ToggleSettings(bool open)
    {
        settingsOpen = open;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (open)
        {
            SettingsView.Visibility = Visibility.Visible;
            SettingsView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            SettingsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
        else
        {
            settings.Save();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => { if (!settingsOpen) SettingsView.Visibility = Visibility.Collapsed; };
            SettingsView.BeginAnimation(OpacityProperty, fade);
            SettingsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 40, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        }
    }

    void OnMinimizeClick(object sender, RoutedEventArgs e) => HideToTray();
    void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (settings.CloseToTray) HideToTray(); else Quit();
    }

    void OnIpKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CallIp(); e.Handled = true; }
    }

    void OnCallIpClick(object sender, RoutedEventArgs e) => CallIp();

    void CallIp()
    {
        var text = IpBox.Text.Trim();
        if (text.Length == 0) { IpBox.Focus(); return; }
        IPAddress? ip = null;
        if (!IPAddress.TryParse(text, out ip))
        {
            try { ip = Dns.GetHostAddresses(text).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); }
            catch { }
        }
        if (ip is null)
        {
            ShowToast($"“{text}” is not a valid IP address.");
            return;
        }
        engine.AddManualTarget(ip);
        var s = ip.ToString();
        if (!settings.ManualIps.Contains(s)) settings.ManualIps.Add(s);
        if (!savedIps.Contains(s)) savedIps.Add(s);
        SavedIpsSection.Visibility = Visibility.Visible;
        settings.Save();
        IpBox.Clear();

        pendingCallIp = ip;
        pendingCallUntilMs = Environment.TickCount64 + 6000;
        ShowToast($"Reaching {s}…", autoHide: false);
    }

    void OnRemoveIpClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string s) return;
        if (IPAddress.TryParse(s, out var ip)) engine.RemoveManualTarget(ip);
        settings.ManualIps.Remove(s);
        savedIps.Remove(s);
        SavedIpsSection.Visibility = savedIps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        settings.Save();
    }

    void ShowToast(string message, bool autoHide = true)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        toastTimer.Stop();
        if (autoHide) toastTimer.Start();
    }

    void HideToast()
    {
        toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ window frame

    void BringToFront()
    {
        if (!IsVisible)
            tray.Notify($"{engine.Partner?.Name ?? "Someone"} is calling", "Click to answer");
        ShowFromTray();
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void StyleWindowFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x003B2A22; // COLORREF 0x00BBGGRR -> #222A3B
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE = round
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
