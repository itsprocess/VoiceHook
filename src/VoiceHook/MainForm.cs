using System.Diagnostics;
using NAudio.Wave;

namespace VoiceHook;

public sealed class MainForm : Form
{
    Settings settings;
    readonly HttpClient http = Net.Client();
    readonly Engine engine;
    readonly Outbox outbox;
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

    public MainForm(bool preview = false)
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
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var record = new TabPage("Capture") { Padding = new Padding(16) };
        var setup = new TabPage("Settings") { Padding = new Padding(16), AutoScroll = true };
        tabs.TabPages.AddRange([record, setup]);
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
        Shown += (_, _) =>
        {
            if (preview)
                return;
            UpdateView();
            try
            {
                Storage.Save(settings);
                controls = new(engine, () => settings);
                controls.Start();
                keyboard = new(engine, () => settings);
            }
            catch (Exception e)
            {
                status.Text = "Control setup failed: " + e.Message;
            }
        };
        FormClosing += (_, e) =>
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
        };
        FormClosed += (_, _) =>
        {
            tray.Dispose();
            timer.Dispose();
        };
        UpdateView();
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

    void Save()
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
            };
            next.Validate();
            controls?.Dispose();
            keyboard?.Dispose();
            settings = next;
            Storage.Save(settings);
            controls = new(engine, () => settings);
            controls.Start();
            keyboard = new(engine, () => settings);
            status.Text = "Settings saved.";
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "VoiceHook settings");
        }
    }
}
