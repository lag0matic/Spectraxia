using System.Globalization;

namespace Spectraxia;

internal sealed class ReceiverWindow : Form
{
    private readonly Label stationName = new() { AutoSize = true, ForeColor = Color.Turquoise, Visible = false, UseMnemonic = false };
    private readonly Label radioText = new() { AutoSize = true, MaximumSize = new Size(480,0), Visible = false, UseMnemonic = false };
    private sealed record Broadcast(long Station, string Name, string Text);
    private volatile Broadcast? broadcast;
    private volatile HdReception? hdReception;
    private sealed record HdChoice(long Station, int Program);
    private volatile HdChoice? hdChoice;
    private sealed record ProgramItem(int Program)
    {
        public override string ToString() => Program < 0 ? "Analog FM" : $"HD{Program + 1}";
    }
    private readonly ComboBox hdPrograms = new() { AccessibleName = "FM audio service", Width = 218, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label hdStatus = new() { AutoSize = true, MaximumSize = new Size(212, 0), UseMnemonic = false };
    private int hdMenuMask = -1;
    private readonly Label status = new() { AutoSize = true };
    private readonly ToolTip diagnosticsTip = new();
    private readonly Button bias = new() { Text = "Bias-T unknown", AutoSize = true, Enabled = false, FlatStyle = FlatStyle.Flat };
    private BiasT biasSession = new();
    private readonly FlowLayoutPanel digits = new() { AutoSize = true, WrapContents = false };
    private readonly TextBox entry = new() { Visible = false, Width = 200 };
    private readonly List<Label> numbers = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 2000 };
    private readonly string settings;
    private readonly Spectrum plot = new();
    private readonly CaptureTape tape = new();
    private volatile bool reviewing;
    private readonly ComboBox outputs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private CancellationTokenSource? cancel;
    private Task? running;
    private AdsbTraffic? aircraftTraffic;
    private AdsbTuner? aircraftTuner;
    private BiasT? aircraftBias;
    private bool closing;
    private volatile string audioDiagnostics = "";
    private int volume;
    private volatile string message = "Starting receiver";
    private sealed record Frame(long Center, int SampleRate, float[] Bins, ScanTarget? Scan, long Time);
    private readonly AirScanPanel scanner;
    private volatile ScanTarget? scanTarget;
    private Frame? latest;
    private long retryAfter;
    private long frequency = 101_300_000;
    private long requestedCenter;
    private volatile ReceiverSettings receiver = new();
    private int[]? pendingSteps;
    private int[] hardwareSteps = [];
    private readonly TrackBar gainSteps = new() { Width = 212, AutoSize = false, Height = 30, TickStyle = TickStyle.None,
        SmallChange = 1, LargeChange = 1, AccessibleName = "Manual tuner gain" };
    private readonly Label gainValue = new() { AutoSize = true };
    private readonly CheckBox manualGain = new() { AutoSize = true, Enabled = false };
    private readonly Label signal = new() { AutoSize = true };
    private readonly Panel meter = new() { Width = 210, Height = 12, BackColor = Color.FromArgb(42, 52, 60) };
    private volatile int signalTenths = -950;
    private volatile bool squelchOpen;
    private bool fillingSteps;
    private event Action? dialChanged;
    private DialSetting CurrentDial => new(frequency, receiver.Mode, receiver.Width,
        hdChoice is { } choice && choice.Station == frequency ? choice.Program : null);
    private sealed class BandwidthField : NumericUpDown
    {
        private int wheel;
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            wheel += e.Delta;
            Value = Math.Clamp(Value - (wheel / 120) * Increment, Minimum, Maximum);
            wheel %= 120;
        }
    }

    public ReceiverWindow(string? settingsPath = null)
    {
        settings = settingsPath ?? AppPaths.Settings;
        Icon = Program.AppIcon;
        Text = "Spectraxia";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(520, 320);
        BackColor = Color.FromArgb(20, 26, 32);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 11);
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(5),
            BackColor = Color.FromArgb(29, 37, 45) };
        var body = new Panel { Dock = DockStyle.Fill };
        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 254, Padding = new Padding(9),
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var rail = new TableLayoutPanel { Dock = DockStyle.Right, Width = TextRenderer.MeasureText("Max dBFS", Font).Width + 36, ColumnCount = 1, RowCount = 3 };
        rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var display = new Panel { Dock=DockStyle.Fill };
        display.Controls.Add(plot);
        var review = new ReviewPane(() => ((Headphones.Endpoint)outputs.SelectedItem!).Id,
            () => Volatile.Read(ref volume), () => receiver, value => reviewing=value);
        display.Controls.Add(review);
        body.Controls.Add(display);
        body.Controls.Add(sidebar);
        body.Controls.Add(rail);
        Controls.Add(body);
        Controls.Add(header);
        var sidebarToggle = new CheckBox { Text = "Controls", AccessibleName = "Controls sidebar",
            Appearance = Appearance.Button, Checked = true, AutoSize = true };
        sidebarToggle.CheckedChanged += (_, _) => sidebar.Visible = sidebarToggle.Checked;
        bool compact = false;
        void FitSidebar()
        {
            bool narrow = ClientSize.Width < 850;
            if (narrow != compact) sidebarToggle.Checked = !narrow;
            compact = narrow;
        }
        ClientSizeChanged += (_, _) => FitSidebar();
        FitSidebar();
        header.Controls.Add(sidebarToggle);
        header.Controls.Add(digits);
        header.Controls.Add(entry);
        header.Controls.Add(stationName);
        foreach (var output in Headphones.Outputs()) outputs.Items.Add(output);
        outputs.SelectedIndex = 0;
        outputs.Width = 218;
        outputs.AccessibleName = "Audio output";
        sidebar.Controls.Add(new Label { Text = "Audio output", AutoSize = true });
        sidebar.Controls.Add(outputs);
        string receiverPath = Path.ChangeExtension(settings, ".receiver.json");
        receiver = ReceiverSettings.Load(receiverPath);
        void ChangeReceiver(ReceiverSettings value, bool restart = false)
        {
            receiver = value;
            if (restart) Restart();
            try { value.Save(receiverPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        manualGain.Checked = receiver.Manual;
        void ShowGainMode()
        {
            manualGain.Text = manualGain.Checked ? "Tuner gain: manual" : "Tuner gain: auto";
            manualGain.ForeColor = manualGain.Checked ? Color.Khaki : Color.LightGreen;
            gainSteps.Enabled = manualGain.Checked && hardwareSteps.Length > 0;
        }
        ShowGainMode();
        manualGain.CheckedChanged += (_, _) =>
        {
            ShowGainMode();
            ChangeReceiver(receiver with { Manual = manualGain.Checked });
        };
        gainSteps.ValueChanged += (_, _) =>
        {
            if (fillingSteps || hardwareSteps.Length == 0) return;
            int tenths = hardwareSteps[gainSteps.Value];
            gainValue.Text = $"Manual gain {tenths / 10m:0.0} dB";
            ChangeReceiver(receiver with { GainTenths = tenths });
        };
        var agc = new CheckBox { Checked = receiver.DigitalAgc, AutoSize = true };
        void ShowAgc()
        {
            agc.Text = agc.Checked ? "RTL digital AGC: on" : "RTL digital AGC: off";
            agc.ForeColor = agc.Checked ? Color.LightGreen : ForeColor;
        }
        ShowAgc();
        agc.CheckedChanged += (_, _) => { ShowAgc(); ChangeReceiver(receiver with { DigitalAgc = agc.Checked }); };
        sidebar.Controls.Add(manualGain);
        sidebar.Controls.Add(gainValue);
        sidebar.Controls.Add(gainSteps);
        sidebar.Controls.Add(agc);
        var modePicker = new TableLayoutPanel { AccessibleName = "Listening mode", AccessibleRole = AccessibleRole.Grouping,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = Padding.Empty };
        var modeButtons = new Dictionary<ListenMode, RadioButton>();
        foreach (var mode in Enum.GetValues<ListenMode>())
        {
            var button = new RadioButton { Text = Listening.Name(mode), AutoSize = true, Margin = new Padding(3, 1, 3, 1) };
            modeButtons.Add(mode, button);
            modePicker.Controls.Add(button);
        }
        sidebar.Controls.Add(new Label { Text = "Listening mode", AutoSize = true });
        sidebar.Controls.Add(modePicker);
        var stereoChoice = new ComboBox { AccessibleName = "WFM stereo mode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 218 };
        stereoChoice.Items.AddRange(new[] { "Auto Stereo", "Mono" });
        stereoChoice.SelectedIndex = receiver.ForceMono ? 1 : 0;
        stereoChoice.Visible = receiver.Mode == ListenMode.WFM;
        sidebar.Controls.Add(stereoChoice);
        sidebar.Controls.Add(hdPrograms);
        sidebar.Controls.Add(hdStatus);
        hdPrograms.SelectionChangeCommitted += (_, _) =>
        {
            if (hdPrograms.SelectedItem is not ProgramItem item) return;
            hdChoice = item.Program < 0 ? null : new(frequency, item.Program);
            RefreshHd();
        };
        var audioAgc = new ComboBox { AccessibleName = "Audio AGC", Width = 218, DropDownStyle = ComboBoxStyle.DropDownList };
        audioAgc.Items.AddRange(Enum.GetNames<AgcSpeed>());
        sidebar.Controls.Add(new Label { Text = "Audio AGC", AutoSize = true });
        sidebar.Controls.Add(audioAgc);
        var voice = new CheckBox { Text = "Voice filters", AutoSize = true, Checked = receiver.VoiceFilterEnabled };
        var voicePanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Visible = voice.Checked };
        sidebar.Controls.Add(voice);
        sidebar.Controls.Add(voicePanel);
        void AddCutoff(string name, int[] choices, int hz, Action<int> save)
        {
            var label = new Label { AutoSize = true };
            var slider = new TrackBar { Width = 210, Height = 24, AutoSize = false, TickStyle = TickStyle.None,
                Minimum = 0, Maximum = choices.Length-1, Value = Array.IndexOf(choices,hz), AccessibleName = name };
            void ShowValue() => label.Text = name + " · " + (choices[slider.Value] == 0 ? "off" : $"{choices[slider.Value]:N0} Hz");
            ShowValue();
            slider.ValueChanged += (_,_) => { ShowValue(); save(choices[slider.Value]); };
            voicePanel.Controls.Add(label);
            voicePanel.Controls.Add(slider);
        }
        AddCutoff("High-pass", VoiceFilters.HighPassOptions, receiver.HighPassHz,
            hz => ChangeReceiver(receiver with { HighPassHz = hz }));
        AddCutoff("Low-pass", VoiceFilters.LowPassOptions, receiver.LowPassHz,
            hz => ChangeReceiver(receiver with { LowPassHz = hz }));
        var noise = new CheckBox { Text = "Reduce steady noise", AutoSize = true, Checked = receiver.ReduceNoise };
        voicePanel.Controls.Add(noise);
        noise.CheckedChanged += (_,_) => ChangeReceiver(receiver with { ReduceNoise = noise.Checked });
        voice.CheckedChanged += (_,_) =>
        {
            voicePanel.Visible = voice.Checked;
            ChangeReceiver(receiver with { VoiceFilterEnabled = voice.Checked });
        };
        var autoNotch = new ComboBox { AccessibleName = "Auto notch for AM and sideband",
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 218 };
        int[] notchWidths = [0,40,100,220];
        autoNotch.Items.AddRange(["Off","Narrow · 40 Hz","Medium · 100 Hz","Wide · 220 Hz"]);
        autoNotch.SelectedIndex = Array.IndexOf(notchWidths,receiver.AutoNotchHz);
        autoNotch.SelectionChangeCommitted += (_,_) =>
            ChangeReceiver(receiver with { AutoNotchHz = notchWidths[autoNotch.SelectedIndex] });
        audioAgc.SelectionChangeCommitted += (_, _) =>
            ChangeReceiver(receiver.WithAudioSpeed((AgcSpeed)audioAgc.SelectedIndex));
        stereoChoice.SelectedIndexChanged += (_, _) => ChangeReceiver(receiver with { ForceMono = stereoChoice.SelectedIndex == 1 });
        sidebar.Controls.Add(new Label { Text = "Filter width (kHz)", AutoSize = true });
        var bandwidth = new BandwidthField { DecimalPlaces = 2, ReadOnly = true, AccessibleName = "Filter width in kHz" };
        var filterRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty };
        var resetWidth = new Button { Text = "Default", AccessibleName = "Reset filter width to mode default",
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        filterRow.Controls.Add(bandwidth);
        filterRow.Controls.Add(resetWidth);
        sidebar.Controls.Add(filterRow);
        resetWidth.Click += (_, _) => bandwidth.Value = Listening.Range(receiver.Mode).Default / 1000m;
        bool fitting = false;
        void SetModeControls(bool retune = true)
        {
            var shown = scanTarget?.Listen(receiver) ?? receiver;
            fitting = true;
            modeButtons[shown.Mode].Checked = true;
            audioAgc.Enabled = shown.Mode != ListenMode.IQ && scanTarget == null;
            voice.Enabled = voicePanel.Enabled = shown.Mode != ListenMode.IQ;
            autoNotch.Enabled = shown.Mode is ListenMode.AM or ListenMode.USB or ListenMode.LSB;
            audioAgc.SelectedIndex = (int)shown.AudioSpeed;
            stereoChoice.Visible = shown.Mode == ListenMode.WFM;
            var range = Listening.Range(shown.Mode);
            bandwidth.Minimum = range.Min / 1000m;
            bandwidth.Maximum = range.Max / 1000m;
            bandwidth.Increment = range.Increment / 1000m;
            bandwidth.Value = shown.Width / 1000m;
            plot.View.Mode = plot.Mode = shown.Mode;
            plot.View.FilterHz = plot.FilterHz = shown.Width;
            if (retune && numbers.Count != 0) SetFrequency(frequency, false);
            plot.Invalidate();
            fitting = false;
        }
        SetModeControls();
        foreach (var (mode, button) in modeButtons)
        {
            button.CheckedChanged += (_, _) =>
            {
                if (fitting || !button.Checked) return;
                ChangeReceiver(receiver with { Mode = mode, FilterHz = 0 });
                ClearBroadcast();
                SetModeControls();
            };
        }
        bandwidth.ValueChanged += (_, _) =>
        {
            if (fitting) return;
            int hz = (int)(bandwidth.Value * 1000);
            ChangeReceiver(receiver.Mode == ListenMode.WFM ? receiver with { WfmBandwidthKhz = hz / 1000 } : receiver with { FilterHz = hz });
            plot.View.FilterHz = plot.FilterHz = hz;
            if (numbers.Count != 0) SetFrequency(frequency, false);
            plot.Invalidate();
        };
        plot.ResizeBandwidth += hz => bandwidth.Value = hz / 1000m;
        sidebar.Controls.Add(signal);
        sidebar.Controls.Add(meter);
        var squelch = new CheckBox { Text = $"Squelch {receiver.SquelchDbfs} dBFS", Checked = receiver.SquelchEnabled, AutoSize = true };
        var threshold = new TrackBar { Minimum = -95, Maximum = 0, Value = receiver.SquelchDbfs,
            Width = 212, AutoSize = false, Height = 30, TickStyle = TickStyle.None, AccessibleName = "Squelch threshold" };
        sidebar.Controls.Add(squelch);
        sidebar.Controls.Add(threshold);
        squelch.CheckedChanged += (_, _) => ChangeReceiver(receiver with { SquelchEnabled = squelch.Checked });
        threshold.ValueChanged += (_, _) =>
        {
            squelch.Text = $"Squelch {threshold.Value} dBFS";
            ChangeReceiver(receiver with { SquelchDbfs = threshold.Value });
        };
        meter.Paint += (_, e) =>
        {
            int X(double level) => (int)Math.Round((level + 95) / 95 * (meter.ClientSize.Width - 1));
            if (message.StartsWith("Live ", StringComparison.Ordinal))
                e.Graphics.FillRectangle(Brushes.Turquoise, 0, 0, X(signalTenths / 10.0), meter.Height);
            if (scanTarget == null && receiver.SquelchEnabled)
                e.Graphics.DrawLine(Pens.Gold, X(receiver.SquelchDbfs), 0, X(receiver.SquelchDbfs), meter.Height);
        };
        string audioSettings = Path.ChangeExtension(settings, ".audio");
        int savedLevel = 0;
        bool savedMute = false;
        try
        {
            var values = File.ReadAllLines(audioSettings);
            if (values.Length == 2 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int percent)
                && percent is >= 0 and <= 100 && bool.TryParse(values[1], out bool muted))
                (savedLevel, savedMute) = (percent, muted);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var level = new TrackBar { Minimum = 0, Maximum = 100, Value = savedLevel, Width = 150,
            AutoSize = false, Height = 28, TickStyle = TickStyle.None, AccessibleName = "Volume" };
        var mute = new CheckBox { Text = "Mute", AutoSize = true, Checked = savedMute };
        volume = savedMute ? 0 : savedLevel;
        void SetLevel()
        {
            Volatile.Write(ref volume, mute.Checked ? 0 : level.Value);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(audioSettings)!);
                string temporary = audioSettings + "." + Environment.ProcessId + ".tmp";
                File.WriteAllLines(temporary, [level.Value.ToString(CultureInfo.InvariantCulture), mute.Checked.ToString()]);
                File.Move(temporary, audioSettings, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        level.ValueChanged += (_, _) => SetLevel();
        mute.CheckedChanged += (_, _) => SetLevel();
        var audio = new FlowLayoutPanel { AutoSize = true };
        audio.Controls.Add(new Label { Text = "Volume", AutoSize = true });
        audio.Controls.Add(level);
        audio.Controls.Add(mute);
        header.Controls.Add(audio);
        header.Controls.Add(bias);
        diagnosticsTip.SetToolTip(bias, "Switch DC power to the antenna port. State reflects the driver's response.");
        bias.Click += (_, _) =>
        {
            if (closing || cancel?.IsCancellationRequested != false) return;
            biasSession.Toggle();
            bias.Enabled = false;
        };
        header.Controls.Add(status);
        header.Controls.Add(radioText);
        var recorder = new CapturePanel(tape,review,receiver.RecordingFolder,
            folder => ChangeReceiver(receiver with { RecordingFolder = folder }));
        Controls.Add(recorder);
        Controls.SetChildIndex(recorder,1);
        var recordingControls=new CheckBox { Text="Record / rewind",Appearance=Appearance.Button,AutoSize=true };
        header.Controls.Add(recordingControls);
        var aircraft = new Button { Text = "ADS-B aircraft ▾", AutoSize = true, FlatStyle = FlatStyle.Flat };
        header.Controls.Add(aircraft);
        var scanToggle = new CheckBox { Text = "Airband scanner", Appearance = Appearance.Button, AutoSize = true };
        header.Controls.Add(scanToggle);
        scanner = new(Path.ChangeExtension(settings, ".scan-locks.json"), () => receiver.SampleRate);
        Controls.Add(scanner);
        Controls.SetChildIndex(scanner, 1);
        scanToggle.CheckedChanged += (_, _) =>
        {
            if (!scanToggle.Checked) scanner.Stop();
            scanner.Visible = scanToggle.Checked;
        };
        bool recording = false;
        var sources = new ContextMenuStrip();
        var dedicated = sources.Items.Add("HackRF · keep listening on RTL");
        var shared = sources.Items.Add("RTL-SDR · switch to aircraft");
        AdsbWindow? hackrfMap = null;
        Task hackrfTask = Task.CompletedTask;
        aircraft.Click += (_,_) =>
        {
            if (closing) return;
            if (hackrfMap != null)
            {
                if (hackrfMap.WindowState == FormWindowState.Minimized) hackrfMap.WindowState = FormWindowState.Normal;
                hackrfMap.Activate();
                return;
            }
            dedicated.Enabled = hackrfTask.IsCompleted;
            shared.Enabled = !recording && !reviewing && !review.Visible;
            sources.Show(aircraft, new Point(0, aircraft.Height));
        };
        Disposed += (_,_) => sources.Dispose();
        dedicated.Click += (_,_) =>
        {
            if (closing || hackrfMap != null || !hackrfTask.IsCompleted) return;
            var traffic = new AdsbTraffic { Status = "Opening HackRF" };
            var tuner = new HackrfTuner(Path.ChangeExtension(settings, ".hackrf-gain.json"));
            var window = new AdsbWindow(traffic, tuner) { StartPosition = FormStartPosition.CenterScreen };
            var stop = new CancellationTokenSource();
            hackrfMap = window;
            var session = hackrfTask = Task.Run(() => HackrfAdsb.Run(traffic, tuner, stop.Token));
            window.FormClosed += async (_,_) =>
            {
                hackrfMap = null;
                stop.Cancel();
                try { await session; }
                finally { stop.Dispose(); }
            };
            window.Show();
        };
        shared.Click += (_,_) =>
        {
            if (recording || reviewing || closing) return;
            scanner.Stop();
            var traffic = new AdsbTraffic();
            aircraftTuner ??= new AdsbTuner(Path.ChangeExtension(settings, ".adsb-gain.json"), receiver);
            using var window = new AdsbWindow(traffic, aircraftTuner, () => aircraftBias?.State ?? BiasState.Unknown,
                () => aircraftBias?.Toggle());
            aircraftTraffic = traffic;
            Restart();
            try { window.ShowDialog(this); }
            finally
            {
                aircraftTraffic = null;
                aircraftBias = null;
                Restart();
            }
        };
        recordingControls.CheckedChanged+=(_,_)=>recorder.Visible=recordingControls.Checked;
        recorder.RecordingChanged += active =>
        {
            recording = active;
            recordingControls.Text = active ? "● Recording" : "Record / rewind";
            recordingControls.ForeColor = active ? Color.Salmon : ForeColor;
        };
        var centering = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 156, AccessibleName = "Plot tuning" };
        centering.Items.AddRange(new[] { "Tune only", "Tune & center" });
        centering.SelectedIndex = 0;
        sidebar.Controls.Add(new Label { Text = "Plot tuning", AutoSize = true });
        sidebar.Controls.Add(centering);
        void RecallDial(DialSetting dial)
        {
            scanner.Stop();
            if (receiver.Mode != dial.Mode) ClearBroadcast();
            ChangeReceiver(dial.Apply(receiver));
            SetModeControls(false);
            SetFrequency(dial.Hz, centering.SelectedIndex == 1);
            hdChoice = dial.HdProgram is int program ? new(dial.Hz, program) : null;
            RefreshHd();
        }
        var libraryPanel = new StationPanel(Path.ChangeExtension(settings, ".library.json"),
            () => CurrentDial, () => broadcast is { } ps && ps.Station == frequency ? ps.Name : "",
            RecallDial, plot);
        sidebar.Controls.Add(libraryPanel);
        sidebar.Controls.SetChildIndex(libraryPanel, 0);
        var dc = new CheckBox { Text = "Remove center DC", AutoSize = true, Checked = receiver.RemoveDc };
        dc.CheckedChanged += (_, _) => ChangeReceiver(receiver with { RemoveDc = dc.Checked });
        sidebar.Controls.Add(dc);
        var balance = new CheckBox { Text = "I/Q balance", AutoSize = true, Checked = receiver.BalanceIq };
        sidebar.Controls.Add(balance);
        var trims = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, Visible = balance.Checked, Margin = Padding.Empty };
        NumericUpDown AddTrim(string label, decimal limit, decimal initial)
        {
            var field = new NumericUpDown { AccessibleName = label, DecimalPlaces = 1, Increment = .1m,
                Minimum = -limit, Maximum = limit, Width = 70, Value = initial };
            trims.Controls.Add(new Label { Text = label, AutoSize = true });
            trims.Controls.Add(field);
            return field;
        }
        var gainTrim = AddTrim("Q gain trim (dB)", 6, receiver.QGainDb);
        var phaseTrim = AddTrim("Q phase error (°)", 20, receiver.QPhaseDegrees);
        sidebar.Controls.Add(trims);
        balance.CheckedChanged += (_, _) =>
        {
            trims.Visible = balance.Checked;
            ChangeReceiver(receiver with { BalanceIq = balance.Checked });
        };
        gainTrim.ValueChanged += (_, _) => ChangeReceiver(receiver with { QGainDb = gainTrim.Value });
        phaseTrim.ValueChanged += (_, _) => ChangeReceiver(receiver with { QPhaseDegrees = phaseTrim.Value });
        sidebar.Controls.Add(new Label { Text="Auto notch (AM / USB / LSB)",AutoSize=true });
        sidebar.Controls.Add(autoNotch);
        sidebar.Controls.Add(new NotchPanel(receiver, plot,
            (notches, bypass) => ChangeReceiver(receiver with { Notches = notches, BypassNotches = bypass }),
            hz => SetFrequency(hz, centering.SelectedIndex == 1)));
        (TrackBar Slider, Action Refresh) AddSlider(string name, int min, int max, int value, Func<int, string> format)
        {
            var group = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var label = new Label { Text = name, AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter };
            var readout = new Label { Text = format(value), AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter };
            var slider = new TrackBar { Minimum = min, Maximum = max, Value = value, Orientation = Orientation.Vertical,
                TickStyle = TickStyle.None, AccessibleName = name, AutoSize = false };
            slider.ValueChanged += (_, _) => readout.Text = format(slider.Value);
            group.Controls.Add(slider);
            group.Controls.Add(readout);
            group.Controls.Add(label);
            void FitSlider()
            {
                int width = group.ClientSize.Width, height = group.ClientSize.Height;
                int line = Font.Height + 2;
                int track = Math.Min(width, 28);
                if (height < line * 2 + 60)
                {
                    label.SetBounds(0, Math.Max(0, height / 2 - line), width - track, line);
                    readout.SetBounds(0, height / 2, width - track, line);
                    slider.SetBounds(width - track, 0, track, height);
                }
                else
                {
                    label.SetBounds(0, 0, width, line);
                    readout.SetBounds(0, height - line, width, line);
                    slider.SetBounds((width - track) / 2, line, track, height - 2 * line);
                }
            }
            group.SizeChanged += (_, _) => FitSlider();
            FitSlider();
            rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
            rail.Controls.Add(group, 0, rail.Controls.Count);
            return (slider, () => readout.Text = format(slider.Value));
        }
        var (zoom, refreshSpan) = AddSlider("Zoom", 0, TuningView.ZoomMaximum, 0, n =>
        {
            int span = TuningView.ZoomSpan(plot.View.SampleRate, n);
            return span >= 1_000_000 ? $"{span / 1e6:0.###} MHz" : $"{span / 1000.0:0.#} kHz";
        });
        zoom.LargeChange = 50;
        var zoomFrames = new System.Windows.Forms.Timer { Interval = 15 };
        int targetSpan = plot.View.Span;
        long lastZoomFrame = 0;
        zoomFrames.Tick += (_, _) =>
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(lastZoomFrame, now).TotalMilliseconds;
            lastZoomFrame = now;
            int current = plot.View.Span;
            // Follow the latest thumb position at a frame-rate-independent speed on the log scale.
            double remaining = Math.Log(current / (double)targetSpan) * Math.Exp(-elapsed / 24);
            int next = Math.Abs(remaining) < .0005 ? targetSpan : (int)Math.Round(targetSpan * Math.Exp(remaining));
            plot.View.Zoom(next);
            plot.Invalidate();
            if (next == targetSpan) zoomFrames.Stop();
        };
        Disposed += (_, _) => zoomFrames.Dispose();
        zoom.ValueChanged += (_, _) =>
        {
            targetSpan = TuningView.ZoomSpan(plot.View.SampleRate, zoom.Value);
            if (plot.View.Span == targetSpan) { zoomFrames.Stop(); return; }
            if (zoomFrames.Enabled) return;
            lastZoomFrame = System.Diagnostics.Stopwatch.GetTimestamp();
            zoomFrames.Start();
        };
        var capture = new ComboBox { Width = 218, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "RF capture width" };
        foreach (var rate in Radio.SampleRates) capture.Items.Add($"{rate / 1e6:0.00} MHz");
        capture.SelectedIndex = Array.IndexOf(Radio.SampleRates, receiver.SampleRate);
        sidebar.Controls.Add(new Label { Text = "RF capture width", AutoSize = true });
        sidebar.Controls.Add(capture);
        capture.SelectionChangeCommitted += (_, _) =>
        {
            int rate = Radio.SampleRates[capture.SelectedIndex];
            zoomFrames.Stop();
            plot.ChangeCapture(rate);
            zoom.Value = 0;
            refreshSpan();
            ChangeReceiver(receiver with { SampleRate = rate }, true);
        };
        TuningView normalView = plot.View;
        var scanView = new TuningView(AirScanner.MinHz);
        bool resumeZoom = false;
        scanner.Changed += () =>
        {
            bool wasScanning = scanTarget != null;
            var next = scanner.Scan?.Target;
            scanTarget = next;
            bool scanning = next != null;
            if (scanning != wasScanning)
            {
                plot.View = scanning ? scanView : normalView;
                modePicker.Enabled = filterRow.Enabled = capture.Enabled = libraryPanel.Enabled =
                    plot.Enabled = zoom.Enabled = digits.Enabled = entry.Enabled = !scanning;
                squelch.Enabled = threshold.Enabled = !scanning;
                SetModeControls(false);
                if (scanning)
                {
                    resumeZoom = zoomFrames.Enabled;
                    zoomFrames.Stop();
                    entry.Visible = false;
                    digits.Visible = true;
                    broadcast = null;
                }
                else
                {
                    Interlocked.Exchange(ref requestedCenter, normalView.CaptureCenter);
                    if (resumeZoom)
                    {
                        lastZoomFrame = System.Diagnostics.Stopwatch.GetTimestamp();
                        zoomFrames.Start();
                    }
                }
            }
            if (next != null) scanView.ShowScan(next, receiver.SampleRate);
            ShowFrequency();
            RefreshHd();
            plot.Invalidate();
        };
        // A decoder pop-out or recorded review explicitly takes over listening.
        review.VisibleChanged += (_, _) =>
        {
            if (review.Visible) scanner.Stop();
            scanToggle.Enabled = !review.Visible;
            scanner.Enabled = !review.Visible;
        };
        var (ceiling, _) = AddSlider("Max dBFS", receiver.WaterfallMin + 1, 0, receiver.WaterfallMax, n => n.ToString());
        var (floor, _) = AddSlider("Min dBFS", -95, receiver.WaterfallMax - 1, receiver.WaterfallMin, n => n.ToString());
        plot.SetWaterfallLimits(floor.Value, ceiling.Value);
        void SetLimits()
        {
            floor.Maximum = ceiling.Value - 1;
            ceiling.Minimum = floor.Value + 1;
            plot.SetWaterfallLimits(floor.Value, ceiling.Value);
            ChangeReceiver(receiver with { WaterfallMin = floor.Value, WaterfallMax = ceiling.Value });
        }
        floor.ValueChanged += (_, _) => SetLimits();
        ceiling.ValueChanged += (_, _) => SetLimits();
        plot.Averaging = receiver.AverageTrace;
        plot.PeakHold = receiver.PeakHold;
        plot.HoldSeconds = receiver.HoldSeconds;
        var avg = new CheckBox { Text = "Average trace", Checked = plot.Averaging, AutoSize = true };
        var peak = new CheckBox { Text = "Peak hold", Checked = plot.PeakHold, AutoSize = true };
        sidebar.Controls.Add(avg);
        sidebar.Controls.Add(peak);
        sidebar.Controls.Add(new Label { Text = "Hold (seconds)", AutoSize = true });
        var hold = new NumericUpDown { Minimum = 0, Maximum = 30, Value = plot.HoldSeconds, AccessibleName = "Hold (seconds)" };
        sidebar.Controls.Add(hold);
        var clear = new Button { Text = "Clear peaks", AutoSize = true };
        sidebar.Controls.Add(clear);
        avg.CheckedChanged += (_, _) => { plot.Averaging = avg.Checked; ChangeReceiver(receiver with { AverageTrace = avg.Checked }); plot.Invalidate(); };
        peak.CheckedChanged += (_, _) => { plot.PeakHold = peak.Checked; ChangeReceiver(receiver with { PeakHold = peak.Checked }); plot.ClearPeaks(); };
        hold.ValueChanged += (_, _) => { plot.HoldSeconds = (int)hold.Value; ChangeReceiver(receiver with { HoldSeconds = plot.HoldSeconds }); };
        clear.Click += (_, _) => plot.ClearPeaks();
        plot.Tune += hz => SetFrequency(hz, centering.SelectedIndex == 1);
        plot.DragTune += (hz, offset) => SetFrequency(hz, false, offset);
        plot.WheelTune += steps => SetFrequency(BandPlan.Step(frequency, -steps, receiver.Mode), centering.SelectedIndex == 1);
        outputs.SelectedIndexChanged += (_, _) => Restart();
        for (int power = 9; power >= 0; power--)
        {
            long place = (long)Math.Pow(10, power);
            var label = new Label { AutoSize = true, Margin = Padding.Empty, Cursor = Cursors.Hand,
                Font = new Font("Consolas", 22), AccessibleName = $"Tune {place} Hz digit" };
            int remainder = 0;
            label.MouseWheel += (_, e) =>
            {
                remainder += e.Delta;
                long next = Tuning.Step(frequency, place, remainder);
                remainder %= 120;
                if (next != frequency) SetFrequency(next, true);
            };
            label.MouseEnter += (_, _) => label.ForeColor = Color.Turquoise;
            label.MouseLeave += (_, _) => label.ForeColor = ForeColor;
            diagnosticsTip.SetToolTip(label, "Scroll to tune · Middle-click to zero this digit · Left-click to enter MHz");
            label.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) EditFrequency();
                if (e.Button == MouseButtons.Middle)
                {
                    long digit = frequency / place % 10;
                    if (digit != 0) SetFrequency(frequency - digit * place, true);
                }
            };
            numbers.Add(label);
            digits.Controls.Add(label);
            if (power == 6)
                digits.Controls.Add(new Label { Text = ".", AutoSize = true, Font = label.Font, Margin = Padding.Empty });
        }
        digits.Controls.Add(new Label { Text = "MHz", AutoSize = true });
        entry.AccessibleName = "Frequency in MHz";
        entry.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Escape) return;
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Enter && !Tuning.Parse(entry.Text, out var parsed))
            {
                entry.SelectAll();
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            if (e.KeyCode == Keys.Enter && Tuning.Parse(entry.Text, out var value)) SetFrequency(value, true);
            entry.Visible = false;
            digits.Visible = true;
            ShowFrequency();
        };
        try
        {
            if (File.Exists(settings) && Tuning.Parse(File.ReadAllText(settings), out var saved)) frequency = saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        plot.View.Tune(frequency, true);
        plot.ChangeCapture(receiver.SampleRate);
        dialChanged += libraryPanel.TrackDial;
        libraryPanel.TrackDial();
        refreshSpan();
        ShowFrequency();
        try
        {
            if (File.Exists(Path.ChangeExtension(settings, ".centering")) &&
                File.ReadAllText(Path.ChangeExtension(settings, ".centering")) == "center") centering.SelectedIndex = 1;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            var name = File.ReadAllText(Path.ChangeExtension(settings, ".output"));
            int index = outputs.FindStringExact(name);
            if (index >= 0) outputs.SelectedIndex = index;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        bool drained=false;
        FormClosing += async (_, e) =>
        {
            if (closing) { e.Cancel = !drained; return; }
            closing = true;
            scanner.Stop();
            libraryPanel.Flush();
            poll.Stop();
            cancel?.Cancel();
            hackrfMap?.Close();
            var pending=Task.WhenAll(review.Shutdown(),recorder.Shutdown(),running??Task.CompletedTask,hackrfTask);
            if(pending.IsCompleted) {drained=true; return;}
            e.Cancel=true;
            await pending;
            drained=true; Close();
        };
        poll.Interval = 50;
        Shown += (_, _) => { Probe(); poll.Start(); };
        poll.Tick += (_, _) => Probe();
        FormClosed += (_, _) =>
        {
            poll.Dispose();
            diagnosticsTip.Dispose();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
                File.WriteAllText(Path.ChangeExtension(settings, ".centering"), centering.SelectedIndex == 1 ? "center" : "only");
                File.WriteAllText(Path.ChangeExtension(settings, ".output"), outputs.Text);
                File.WriteAllText(settings + ".tmp", MHz());
                File.Move(settings + ".tmp", settings, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    private void Probe()
    {
        string current = message;
        bool live = current.StartsWith("Live ", StringComparison.Ordinal);
        if (!live && running is { IsCompleted: true }) scanner.Stop();
        scanner.SetAvailable(live && aircraftTraffic == null && !closing);
        var steps = Interlocked.Exchange(ref pendingSteps, null);
        if (steps != null)
        {
            fillingSteps = true;
            hardwareSteps = steps;
            gainSteps.Maximum = Math.Max(0, steps.Length - 1);
            if (steps.Length > 0)
            {
                int tenths = receiver.NearestGain(steps);
                gainSteps.Value = Array.IndexOf(steps, tenths);
                gainValue.Text = $"Manual gain {tenths / 10m:0.0} dB";
            }
            else gainValue.Text = "Manual gain unavailable";
            fillingSteps = false;
        }
        manualGain.Enabled = live && hardwareSteps.Length > 0;
        gainSteps.Enabled = manualGain.Enabled && manualGain.Checked;
        signal.Text = live ? $"Signal {signalTenths / 10.0:F1} dBFS" +
            (scanTarget == null && receiver.SquelchEnabled ? (squelchOpen ? " · Open" : " · Closed") : "") : "Signal unavailable";
        meter.Invalidate();
        var received = broadcast;
        bool valid = live && scanTarget == null && receiver.Mode == ListenMode.WFM && received?.Station == frequency;
        stationName.Text = valid ? received!.Name : "";
        radioText.Text = valid ? received!.Text : "";
        stationName.Visible = stationName.Text.Length > 0;
        radioText.Visible = radioText.Text.Length > 0;
        status.Text = current;
        RefreshHd(live);
        string diagnostics = live ? audioDiagnostics : "";
        if (status.AccessibleDescription != diagnostics)
        {
            status.AccessibleDescription = diagnostics;
            diagnosticsTip.SetToolTip(status, diagnostics);
        }
        BiasButton.Update(bias, biasSession.State, live && !closing && cancel?.IsCancellationRequested == false);
        var frame = Interlocked.Exchange(ref latest, null);
        if (frame != null && frame.SampleRate == plot.View.SampleRate && ReferenceEquals(scanTarget, frame.Scan))
        {
            plot.Add(frame.Center, frame.Bins);
            if (frame.Scan != null) scanner.Observe(frame.Center, frame.Bins, frame.Time);
        }
        if (closing || running is { IsCompleted: false } || Environment.TickCount64 < Interlocked.Read(ref retryAfter)) return;
        cancel?.Dispose();
        cancel = new();
        var token = cancel.Token;
        long hz = plot.View.CaptureCenter;
        Interlocked.Exchange(ref requestedCenter, hz);
        uint output = ((Headphones.Endpoint)outputs.SelectedItem!).Id;
        var config = receiver;
        var deviceBias = biasSession = new BiasT();
        if (aircraftTraffic is {} traffic)
        {
            aircraftBias = deviceBias;
            var tuner = aircraftTuner!;
            running = Task.Run(() => ReceiveAircraft(traffic, tuner, token, deviceBias));
            return;
        }
        running = Task.Run(() => Receive(hz, output, config, token, deviceBias));
    }

    private void ReceiveAircraft(AdsbTraffic traffic, AdsbTuner tuner, CancellationToken token, BiasT deviceBias)
    {
        var decoder = new AdsbDecoder(frame => traffic.Update(frame, Environment.TickCount64));
        try
        {
            if (token.IsCancellationRequested) return;
            Radio.Receive(AdsbDecoder.Frequency, tuner.Capture, () => tuner.Capture, () => AdsbDecoder.Frequency,
                steps => traffic.GainSteps = steps, token, (iq, _, settled) =>
                {
                    if (!settled) return;
                    traffic.Status = "Listening · 1090 MHz";
                    decoder.Feed(iq);
                }, bias: deviceBias);
        }
        catch (Exception ex)
        {
            traffic.Status = ex is DllNotFoundException ? "RTL-SDR native library unavailable" : ex.Message;
            if (!token.IsCancellationRequested) Interlocked.Exchange(ref retryAfter, Environment.TickCount64 + 1800);
        }
        finally { traffic.GainSteps = null; }
    }

    private void Restart()
    {
        cancel?.Cancel();
        Interlocked.Exchange(ref retryAfter, 0);
    }

    private void Receive(long hz, uint output, ReceiverSettings config, CancellationToken token, BiasT deviceBias)
    {
        Headphones? audio = null;
        try
        {
            var decoder = new ReceiverAudio(config.SampleRate);
            var voiceFilters = new VoiceFilters();
            using var digital = new HdReceiver(config.SampleRate);
            int retunes = 0;
            var session=Guid.NewGuid();
            DateTimeOffset? origin=null;
            long pairs=0;
            bool stereo = false;
            long drawAfter = 0;
            Radio.Receive(hz, config, () => receiver, () => scanTarget?.Center ?? Interlocked.Read(ref requestedCenter),
                steps => Interlocked.Exchange(ref pendingSteps, steps), token, (iq, blockCenter, settled) =>
            {

                var target = scanTarget;
                long station = target?.Station ?? Interlocked.Read(ref frequency);
                var normal = receiver;
                var active = target?.Listen(normal) ?? normal;
                bool needsStereoOutput = active.Mode is ListenMode.IQ or ListenMode.WFM;
                if (stereo != needsStereoOutput) { audio?.Dispose(); audio = null; stereo = needsStereoOutput; }
                var pcm = decoder.Decode(iq, blockCenter, station, active, 100);
                target?.Gate(pcm, settled, blockCenter);
                byte[] captured = (byte[])iq.Clone();
                var choice = hdChoice;
                int program = choice?.Station == station && active.Mode == ListenMode.WFM ? choice.Program : -1;
                var hd = digital.Push(captured, station, blockCenter, settled && active.Mode == ListenMode.WFM, program);
                hdReception = hd;
                bool digitalAudio = false;
                if (program >= 0)
                {
                    if (hd == null) Array.Clear(pcm);
                    else digitalAudio = hd.Render(pcm, decoder.Gate, active.ForceMono);
                }
                bool audible = settled && decoder.Gate && (program < 0 || digitalAudio)
                    && (target == null || target.Audible && target.Center == blockCenter);
                voiceFilters.Apply(pcm, active, decoder.DecodedStation, program, audible);
                origin ??= DateTimeOffset.UtcNow.AddSeconds(-iq.Length/(2.0*config.SampleRate));
                var meta=new CaptureBand(config.SampleRate,blockCenter,decoder.DecodedStation,active.Mode,active.Width,settled,session);
                tape.Append(new TapeBlock(origin.Value.AddSeconds(pairs/(double)config.SampleRate),meta,captured,pcm,stereo?2:1));
                pairs+=iq.Length/2;
                signalTenths = (int)Math.Round(decoder.Level * 10);
                squelchOpen = decoder.Gate;
                if(reviewing) { audio?.Dispose(); audio=null; }
                else { audio ??= new Headphones(output,stereo); audio.Play(pcm,Volatile.Read(ref volume)); }
                message = active.Mode == ListenMode.WFM ? (decoder.StereoLocked && !active.ForceMono ? "Live WFM stereo" : "Live WFM mono") : $"Live {Listening.Name(active.Mode)}";
                if (program >= 0) message = digitalAudio ? $"Live HD{program + 1}" : $"Live · waiting for HD{program + 1}";
                if (settled && Environment.TickCount64 >= drawAfter && iq.Length >= Spectrum.Bins * 2)
                {
                    broadcast = active.Mode == ListenMode.WFM ? new Broadcast(decoder.DecodedStation, decoder.StationName, decoder.RadioText) : null;
                    long halfRate = config.SampleRate / 2;
                    audioDiagnostics = $"RF capture {(blockCenter-halfRate)/1e6:F6}–{(blockCenter+halfRate)/1e6:F6} MHz · hardware retunes {Volatile.Read(ref retunes)} · audio queue empty {audio?.EmptyQueueEvents??0} · dropped frames {audio?.DroppedFrames??0}";
                    Interlocked.Exchange(ref latest, new Frame(blockCenter, config.SampleRate, Spectrum.Analyze(iq, decoder.FilteredIq), target, Environment.TickCount64));
                    drawAfter = Environment.TickCount64 + 48;
                }
            }, () => Interlocked.Increment(ref retunes), deviceBias);
            if (token.IsCancellationRequested) message = "Starting receiver";
        }
        catch (Exception ex)
        {
            message = ex is DllNotFoundException ? "RTL-SDR native library unavailable" : ex.Message;
            if (!token.IsCancellationRequested) Interlocked.Exchange(ref retryAfter, Environment.TickCount64 + 1800);
        }
        finally { hdReception = null; audio?.Dispose(); }
    }

    private string MHz() => (frequency / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture);

    private void EditFrequency()
    {
        entry.Text = MHz();
        digits.Visible = false;
        entry.Visible = true;
        entry.Focus();
        entry.SelectAll();
    }

    private void ShowFrequency()
    {
        string value = (scanTarget?.Station ?? frequency).ToString("D10", CultureInfo.InvariantCulture);
        for (int i = 0; i < numbers.Count; i++) numbers[i].Text = value[i].ToString();
    }

    private void ClearBroadcast()
    {
        broadcast = null;
        hdChoice = null;
        hdReception = null;
        stationName.Visible = radioText.Visible = false;
        stationName.Text = radioText.Text = "";
    }

    private void SetFrequency(long hz, bool center, long? pointerOffset = null)
    {
        if (scanTarget != null) return;
        if (hz != frequency) ClearBroadcast();
        plot.View.Tune(hz, center, pointerOffset);
        Interlocked.Exchange(ref requestedCenter, plot.View.CaptureCenter);
        Interlocked.Exchange(ref frequency, plot.View.Station);
        dialChanged?.Invoke();
        ShowFrequency();
        RefreshHd();
        plot.Invalidate();
    }

    private void RefreshHd(bool? live = null)
    {
        bool visible = scanTarget == null && receiver.Mode == ListenMode.WFM && HdReceiver.InBand(frequency) && aircraftTraffic == null;
        hdPrograms.Visible = hdStatus.Visible = visible;
        if (!visible) return;
        var hd = hdReception;
        if (hd?.Station != frequency || live == false) hd = null;
        int mask = hd?.Services ?? 0;
        if (mask != hdMenuMask)
        {
            hdMenuMask = mask;
            hdPrograms.Items.Clear();
            hdPrograms.Items.Add(new ProgramItem(-1));
            for (int p = 0; p < 8; p++)
                if ((mask & (1 << p)) != 0) hdPrograms.Items.Add(new ProgramItem(p));
        }
        int selected = hdChoice?.Program ?? -1;
        if (!hdPrograms.DroppedDown && (hdPrograms.SelectedItem as ProgramItem)?.Program != selected)
            hdPrograms.SelectedIndex = hdPrograms.Items.IndexOf(new ProgramItem(selected));
        hdPrograms.Enabled = mask != 0 || selected >= 0;
        hdStatus.Text = hd is { Error.Length: > 0 } ? hd.Error :
            selected >= 0 && (mask & (1 << selected)) == 0 ? $"Waiting for HD{selected + 1}" :
            hd?.Locked == true ? ("HD locked  " + hd.Name).TrimEnd() :
            live == false ? "HD idle" : "Searching for HD";
    }
}
