using System.Diagnostics;
using NAudio.Wave;

namespace VoiceHook;

public sealed class MainForm : Form
{
    Settings settings;
    readonly HttpClient http = Net.Client();
    readonly Engine engine;
    readonly Outbox outbox;
    readonly ListView messages = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    readonly TextBox messageBody = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly TextBox compose = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 8000, AccessibleName = "Message to send" };
    readonly Label messageStatus = new() { AutoSize = true, MaximumSize = new Size(720, 0) };
    readonly SpeechQueue speech;
    SpeechServer speechServer = new();
    readonly Label speechStatus = new() { AutoSize = true, MaximumSize = new Size(730, 0) };
    readonly CheckBox speechOn = new() { Text = "Accept incoming text and speak it", AutoSize = true };
    readonly ComboBox speechProvider = List("Windows (local)", "OpenAI-compatible API");
    readonly ComboBox windowsVoice = List();
    readonly ComboBox speechVoice = List("coral", "alloy", "ash", "ballad", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse", "marin", "cedar");
    readonly NumericUpDown speechPort = new() { Minimum = 1024, Maximum = 65535, Width = 100 };
    readonly TextBox speechEndpoint = new() { Width = 430 };
    readonly TextBox speechModel = new() { Width = 240 };
    readonly TextBox speechKey = new() { Width = 430, UseSystemPasswordChar = true };
    readonly CheckBox reuseKey = new() { Text = "Use the transcription API key", AutoSize = true };
    Controls? controls;
    KeyboardPtt? keyboard;
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Label status = new()
    {
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        Padding = new Padding(0, 8, 0, 8),
    };
    readonly Label pending = new() { AutoSize = true };
    readonly TextBox transcript = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
    };
    readonly ComboBox provider = List("Windows (local)", "OpenAI-compatible API");
    readonly ComboBox microphone = List();
    readonly ComboBox hotkey = List(Enumerable.Range(1, 24).Select(i => "F" + i).ToArray());
    readonly TextBox endpoint = new() { Width = 430 };
    readonly TextBox model = new() { Width = 240 };
    readonly TextBox language = new() { Width = 120 };
    readonly TextBox apiKey = new() { Width = 430, UseSystemPasswordChar = true };
    readonly TextBox webhook = new() { Width = 430 };
    readonly TextBox webhookToken = new() { Width = 430, UseSystemPasswordChar = true };
    readonly CheckBox keyboardOn = new() { Text = "Enable keyboard PTT", AutoSize = true };
    readonly CheckBox ctrl = new() { Text = "Ctrl", AutoSize = true };
    readonly CheckBox alt = new() { Text = "Alt", AutoSize = true };
    readonly CheckBox shift = new() { Text = "Shift", AutoSize = true };
    readonly CheckBox udpOn = new() { Text = "Enable UDP (127.0.0.1 only)", AutoSize = true };
    readonly CheckBox auto = new()
    {
        Text = "Automatically send finished transcripts",
        AutoSize = true,
    };
    readonly NumericUpDown max = new()
    {
        Minimum = 1,
        Maximum = 300,
        Width = 100,
    };
    readonly NumericUpDown port = new()
    {
        Minimum = 1024,
        Maximum = 65535,
        Width = 100,
    };
    readonly TextBox udpToken = new()
    {
        ReadOnly = true,
        Width = 430,
        UseSystemPasswordChar = true,
    };
    readonly Button talk = new()
    {
        Text = "Hold to talk",
        AutoSize = true,
        Padding = new Padding(16, 10, 16, 10),
    };
    string? buttonId;
    bool exiting;

    static ComboBox List(params string[] choices)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        c.Items.AddRange(choices);
        return c;
    }

    public MainForm(bool preview = false, string? controlPipeName = null)
    {
        settings = Storage.Load();
        Text = "VoiceHook";
        Width = 850;
        Height = 780;
        MinimumSize = new Size(680, 620);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(245, 248, 249);
        Padding = new Padding(20);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        outbox = new(Path.Combine(Storage.DirectoryPath, "outbox"), http);
        engine = new(() => settings, () => new Microphone(), new Transcriber(http), outbox);
        speech = new(Path.Combine(Storage.DirectoryPath, "speech"), () => settings, new SpeechPlayer(http));
        if (preview) speech.Pause(true);
        engine.RecordingStarting += () => speech.Pause(true);
        engine.Changed += () => { if (engine.State == "idle") speech.Pause(false); };
        speech.Changed += () => Ui(() => { UpdateSpeech(); messageStatus.Text = speech.Status + $" Queued: {speech.Pending}"; UpdateMessages(); });
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var record = new TabPage("Capture") { Padding = new Padding(16) };
        var setup = new TabPage("Settings") { Padding = new Padding(16), AutoScroll = true };
        tabs.TabPages.AddRange([record, setup]);
        var messagesTab = new TabPage("Speak") { Padding = new Padding(16) };
        tabs.TabPages.Insert(1, messagesTab);
        var chat = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        chat.RowStyles.Add(new(SizeType.AutoSize));
        chat.RowStyles.Add(new(SizeType.Percent, 45));
        chat.RowStyles.Add(new(SizeType.Percent, 55));
        chat.RowStyles.Add(new(SizeType.AutoSize));
        chat.RowStyles.Add(new(SizeType.Absolute, 85));
        chat.RowStyles.Add(new(SizeType.AutoSize));
        messagesTab.Controls.Add(chat);
        chat.Controls.Add(new Label { Text = "Speech history · select a row for text and timing", AutoSize = true });
        messages.Columns.Add("Time", 145);
        messages.Columns.Add("From", 65);
        messages.Columns.Add("Status", 100);
        messages.Columns.Add("Message", 380);
        chat.Controls.Add(messages);
        chat.Controls.Add(messageBody);
        compose.AccessibleName = "Text to speak";
        chat.Controls.Add(new Label { Text = "Text to speak locally · Ctrl+Enter to speak · never sent to the transcript webhook", AutoSize = true, Padding = new Padding(0, 8, 0, 4) });
        chat.Controls.Add(compose);
        var chatActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        AddButton(chatActions, "Speak", SendText);
        AddButton(chatActions, "Stop speech", speech.Stop);
        chatActions.Controls.Add(messageStatus);
        chat.Controls.Add(chatActions);
        messages.SelectedIndexChanged += (_, _) =>
        {
            if (messages.SelectedItems.Count == 1 && messages.SelectedItems[0].Tag is TextMessage m)
                messageBody.Text = $"{m.Direction} · {m.At.LocalDateTime:g} · {m.Status}\r\n\r\n{m.Text}";
        };
        compose.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendText(); } };
        Controls.Add(tabs);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        record.Controls.Add(layout);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(talk);
        AddButton(buttons, "Cancel", () => engine.CancelActive());
        AddButton(
            buttons,
            "Send last transcript",
            () =>
            {
                try
                {
                    engine.SendLast();
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message, "VoiceHook");
                }
            }
        );
        layout.Controls.Add(buttons);
        layout.Controls.Add(status);
        layout.Controls.Add(transcript);
        layout.Controls.Add(pending);
        var footer = new FlowLayoutPanel { AutoSize = true };
        AddButton(footer, "Retry pending", async () => await Deliver(true));
        AddButton(
            footer,
            "Open data folder",
            () =>
                Process.Start(
                    new ProcessStartInfo(Storage.DirectoryPath) { UseShellExecute = true }
                )
        );
        layout.Controls.Add(footer);
        AddButton(footer, "Stop speech", speech.Stop);
        footer.Controls.Add(speechStatus);
        talk.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                StartButton();
        };
        talk.MouseUp += (_, _) => StopButton();
        talk.MouseCaptureChanged += (_, _) =>
        {
            if (!talk.Capture)
                StopButton();
        };
        talk.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                e.SuppressKeyPress = true;
                StartButton();
            }
        };
        talk.KeyUp += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                e.SuppressKeyPress = true;
                StopButton();
            }
        };
        Deactivate += (_, _) => StopButton();
        var form = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
        };
        form.ColumnStyles.Add(new(SizeType.Absolute, 180));
        form.ColumnStyles.Add(new(SizeType.Percent, 100));
        setup.Controls.Add(form);
        void Row(string name, System.Windows.Forms.Control value)
        {
            var row = form.RowCount++;
            form.RowStyles.Add(new(SizeType.AutoSize));
            var label = new Label
            {
                Text = name,
                AutoSize = true,
                Margin = new Padding(0, 8, 8, 8),
            };
            value.Margin = new Padding(0, 6, 0, 6);
            form.Controls.Add(label, 0, row);
            form.Controls.Add(value, 1, row);
        }
        microphone.Items.Add("Windows default microphone");
        for (var i = 0; i < WaveIn.DeviceCount; i++)
            microphone.Items.Add(WaveIn.GetCapabilities(i).ProductName);
        Row("Microphone", microphone);
        Row("Transcription", provider);
        Row("Language", language);
        Row("Service endpoint", endpoint);
        Row("Model", model);
        Row("API key", apiKey);
        Row(
            "",
            new Label
            {
                Text =
                    "Service mode sends recorded audio to this endpoint. Windows mode stays local.\nKeys are encrypted for your Windows account. Blank clears a key.",
                AutoSize = true,
                MaximumSize = new Size(430, 0),
            }
        );
        Row("Webhook URL", webhook);
        Row("Webhook bearer token", webhookToken);
        Row("", auto);
        var keys = new FlowLayoutPanel { AutoSize = true };
        keys.Controls.AddRange([ctrl, alt, shift]);
        Row("", keyboardOn);
        Row("PTT key", hotkey);
        Row("Modifiers", keys);
        Row("Maximum seconds", max);
        Row("", udpOn);
        Row("UDP port", port);
        Row("UDP token", udpToken);
        var tokenActions = new FlowLayoutPanel { AutoSize = true };
        AddButton(tokenActions, "Copy UDP token", () => Clipboard.SetText(settings.UdpToken));
        Row("", tokenActions);
        var save = new FlowLayoutPanel { AutoSize = true };
        AddButton(save, "Save settings", Save);
        Row("", save);
        var speechTab = new TabPage("Speech output") { Padding = new Padding(16), AutoScroll = true };
        tabs.TabPages.Add(speechTab);
        form = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        form.ColumnStyles.Add(new(SizeType.Absolute, 180));
        form.ColumnStyles.Add(new(SizeType.Percent, 100));
        speechTab.Controls.Add(form);
        Row("", speechOn);
        Row("Incoming port", speechPort);
        Row("", new Label { Text = "Authenticated HTTP on 127.0.0.1 only. POST text to /speech.\nPTT interrupts speech; queued replies wait until capture finishes.", AutoSize = true, MaximumSize = new Size(430, 0) });
        var speechActions = new FlowLayoutPanel { AutoSize = true };
        AddButton(speechActions, "Copy incoming URL", () => Clipboard.SetText($"http://127.0.0.1:{speechPort.Value}/speech"));
        AddButton(speechActions, "Copy access token", () => Clipboard.SetText(Storage.Reveal(settings.SpeechToken)));
        Row("", speechActions);
        Row("Speech provider", speechProvider);
        windowsVoice.Items.Add("Windows default voice");
        using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
            foreach (var voice in synth.GetInstalledVoices().Where(v => v.Enabled)) windowsVoice.Items.Add(voice.VoiceInfo.Name);
        Row("Windows voice", windowsVoice);
        Row("Speech endpoint", speechEndpoint);
        Row("Speech model", speechModel);
        Row("Service voice", speechVoice);
        Row("Speech API key", speechKey);
        Row("", reuseKey);
        Row("", new Label { Text = "OpenAI mode sends incoming text to the configured service and plays AI-generated speech. Windows speech stays local. Keys are encrypted for your Windows account.", AutoSize = true, MaximumSize = new Size(430, 0) });
        var speechSave = new FlowLayoutPanel { AutoSize = true };
        AddButton(speechSave, "Save settings", Save);
        AddButton(speechSave, "Test saved voice", () => { try { if (!settings.SpeechEnabled) throw new InvalidOperationException("Enable speech and save settings first."); speech.Accept(Guid.NewGuid().ToString("N"), "VoiceHook speech output is ready."); } catch (Exception e) { MessageBox.Show(e.Message, "VoiceHook speech"); } });
        AddButton(speechSave, "Stop speech", speech.Stop);
        Row("", speechSave);
        speechOn.Checked = settings.SpeechEnabled;
        speechPort.Value = settings.SpeechPort;
        speechProvider.SelectedIndex = settings.SpeechProvider == "windows" ? 0 : 1;
        windowsVoice.SelectedIndex = Math.Max(0, windowsVoice.Items.IndexOf(settings.WindowsVoice));
        speechEndpoint.Text = settings.SpeechEndpoint;
        speechModel.Text = settings.SpeechModel;
        speechVoice.DropDownStyle = ComboBoxStyle.DropDown;
        speechVoice.Text = settings.SpeechVoice;
        speechKey.Text = Storage.Reveal(settings.SpeechApiKey);
        reuseKey.Checked = settings.SpeechUseTranscriptionKey;
        speechProvider.SelectedIndexChanged += (_, _) => SpeechProviderState();
        reuseKey.CheckedChanged += (_, _) => SpeechProviderState();
        SpeechProviderState();
        provider.SelectedIndex = settings.Provider == "windows" ? 0 : 1;
        microphone.SelectedIndex =
            settings.Device + 1 < microphone.Items.Count ? settings.Device + 1 : 0;
        language.Text = settings.Language;
        endpoint.Text = settings.Endpoint;
        model.Text = settings.Model;
        apiKey.Text = Storage.Reveal(settings.ApiKey);
        webhook.Text = settings.Webhook;
        webhookToken.Text = Storage.Reveal(settings.WebhookToken);
        auto.Checked = settings.AutoSend;
        hotkey.SelectedIndex = settings.Hotkey - 112;
        keyboardOn.Checked = settings.Keyboard;
        ctrl.Checked = settings.Ctrl;
        alt.Checked = settings.Alt;
        shift.Checked = settings.Shift;
        max.Value = settings.MaxSeconds;
        udpOn.Checked = settings.Udp;
        port.Value = settings.UdpPort;
        udpToken.Text = settings.UdpToken;
        provider.SelectedIndexChanged += (_, _) => ProviderState();
        ProviderState();
        var menu = new ContextMenuStrip();
        menu.Items.Add(
            "Open VoiceHook",
            null,
            (_, _) =>
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }
        );
        menu.Items.Add("Cancel recording", null, (_, _) => engine.CancelActive());
        menu.Items.Add("Stop speech", null, (_, _) => speech.Stop());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        tray.Icon = Icon;
        tray.Text = "VoiceHook";
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) =>
        {
            Show();
            Activate();
        };
        tray.Visible = !preview;
        engine.Changed += () => Ui(UpdateView);
        outbox.Changed += message =>
            Ui(() =>
            {
                status.Text = message;
                pending.Text = $"Pending webhook deliveries: {outbox.Count}";
            });
        timer.Tick += async (_, _) => await Deliver(false);
        if (!preview)
            timer.Start();
        Shown += async (_, _) =>
        {
            if (preview)
                return;
            UpdateView();
            try
            {
                Storage.Save(settings);
                controls = new(engine, () => settings, controlPipeName);
                controls.ShowRequested += () => Ui(() => { Show(); WindowState = FormWindowState.Normal; Activate(); });
                controls.Start();
                keyboard = new(engine, () => settings);
                await speechServer.Start(settings, speech);
                UpdateSpeech();
            }
            catch (Exception e)
            {
                status.Text = "Control setup failed: " + e.Message;
            }
        };
        FormClosing += async (_, e) =>
        {
            if (!exiting)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            engine.CancelActive();
            controls?.Dispose();
            keyboard?.Dispose();
            timer.Stop();
            tray.Visible = false;
            if (!shutdownComplete)
            {
                e.Cancel = true;
                Enabled = false;
                try { await speechServer.DisposeAsync(); await speech.DisposeAsync(); }
                finally { shutdownComplete = true; Close(); }
            }
        };
        FormClosed += (_, _) =>
        {
            tray.Dispose();
            timer.Dispose();
        };
        UpdateView();
        UpdateMessages();
    }

    void SendText()
    {
        try { speech.SpeakText(compose.Text); compose.Clear(); messageStatus.Text = "Queued for speech."; UpdateMessages(); }
        catch (Exception e) { messageStatus.Text = e.Message; }
    }

    void UpdateMessages()
    {
        var selected = messages.SelectedItems.Count == 1 ? messages.SelectedItems[0].Tag as TextMessage : null;
        var latestWasSelected = messages.Items.Count == 0 || selected?.Id == (messages.Items[^1].Tag as TextMessage)?.Id;
        var rows = speech.Recent().Select(r => new TextMessage(r.Id, r.Text, r.At, r.Source, r.Description))
            .OrderBy(m => m.At).TakeLast(200).ToArray();
        messages.BeginUpdate();
        messages.Items.Clear();
        foreach (var m in rows)
        {
            var item = new ListViewItem([m.At.LocalDateTime.ToString("g"), m.Direction, m.Status, m.Text.ReplaceLineEndings(" ")]) { Tag = m };
            messages.Items.Add(item);
            if (!latestWasSelected && selected?.Id == m.Id) item.Selected = true;
        }
        if (latestWasSelected && messages.Items.Count > 0) { messages.Items[^1].Selected = true; messages.Items[^1].EnsureVisible(); }
        messages.EndUpdate();
    }

    bool shutdownComplete;
    void UpdateSpeech() => speechStatus.Text = settings.SpeechEnabled ? $"{speech.Status} Queued: {speech.Pending}" : "Speech output is off.";
    void SpeechProviderState()
    {
        var service = speechProvider.SelectedIndex == 1;
        windowsVoice.Enabled = !service;
        speechEndpoint.Enabled = speechModel.Enabled = speechVoice.Enabled = reuseKey.Enabled = service;
        speechKey.Enabled = service && !reuseKey.Checked;
    }

    public void ExitApplication()
    {
        exiting = true;
        Close();
    }

    async Task Deliver(bool retry)
    {
        try
        {
            if (retry)
                await outbox.RetryAll();
            else
                await outbox.Flush();
        }
        catch
        {
            status.Text =
                "Cannot access the outbox. Check files and permissions in the data folder.";
        }
    }

    void ProviderState()
    {
        endpoint.Enabled = model.Enabled = apiKey.Enabled = provider.SelectedIndex == 1;
    }

    void Ui(Action action)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(action);
    }

    void UpdateView()
    {
        status.Text = engine.Message;
        transcript.Text = engine.Last?.Text ?? "";
        pending.Text = $"Pending webhook deliveries: {outbox.Count}";
        talk.BackColor = engine.State == "recording" ? Color.LightCoral : SystemColors.Control;
        talk.Text = engine.State == "recording" ? "Listening — release to finish" : "Hold to talk";
        tray.Text = "VoiceHook · " + engine.State;
    }

    void StartButton()
    {
        if (buttonId != null)
            return;
        buttonId = Guid.NewGuid().ToString("N");
        engine.Command(new("start", buttonId), "button");
    }

    void StopButton()
    {
        if (buttonId == null)
            return;
        var id = buttonId;
        buttonId = null;
        engine.Command(new("stop", id), "button");
    }

    static void AddButton(System.Windows.Forms.Control parent, string label, Action action)
    {
        var b = new Button
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(0, 4, 8, 4),
        };
        b.Click += (_, _) => action();
        parent.Controls.Add(b);
    }

    async void Save()
    {
        try
        {
            if (engine.State != "idle")
                throw new InvalidOperationException(
                    "Finish or cancel recording/transcription before changing settings."
                );
            var next = settings with
            {
                Provider = provider.SelectedIndex == 0 ? "windows" : "openai",
                Device = microphone.SelectedIndex - 1,
                Language = language.Text.Trim(),
                Endpoint = endpoint.Text.Trim(),
                Model = model.Text.Trim(),
                ApiKey = Storage.Protect(apiKey.Text),
                Webhook = webhook.Text.Trim(),
                WebhookToken = Storage.Protect(webhookToken.Text),
                AutoSend = auto.Checked,
                Keyboard = keyboardOn.Checked,
                Hotkey = hotkey.SelectedIndex + 112,
                Ctrl = ctrl.Checked,
                Alt = alt.Checked,
                Shift = shift.Checked,
                MaxSeconds = (int)max.Value,
                Udp = udpOn.Checked,
                UdpPort = (int)port.Value,
                SpeechEnabled = speechOn.Checked,
                SpeechPort = (int)speechPort.Value,
                SpeechProvider = speechProvider.SelectedIndex == 0 ? "windows" : "openai",
                WindowsVoice = windowsVoice.SelectedIndex <= 0 ? "" : windowsVoice.Text,
                SpeechEndpoint = speechEndpoint.Text.Trim(),
                SpeechModel = speechModel.Text.Trim(),
                SpeechVoice = speechVoice.Text.Trim(),
                SpeechApiKey = Storage.Protect(speechKey.Text),
                SpeechUseTranscriptionKey = reuseKey.Checked,
            };
            next.Validate();
            Enabled = false;
            await speechServer.DisposeAsync();
            speechServer = new();
            try { await speechServer.Start(next, speech); }
            catch { await speechServer.Start(settings, speech); throw; }
            controls?.Dispose();
            keyboard?.Dispose();
            settings = next;
            Storage.Save(settings);
            speech.SettingsChanged();
            UpdateSpeech();
            controls = new(engine, () => settings);
            controls.Start();
            keyboard = new(engine, () => settings);
            status.Text = "Settings saved.";
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "VoiceHook settings");
        }
        finally { Enabled = true; }
    }
}
