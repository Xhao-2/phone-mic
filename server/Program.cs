using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneMic;

/// <summary>
/// PhoneMic 服务端：接收手机的 PCM 音频，降噪后播放进虚拟声卡，
/// 于是手机在系统里变成一个麦克风。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
        catch (Exception error)
        {
            MessageBox.Show(error.ToString(), "PhoneMic 启动失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

/// <summary>Wire format shared with the Android client.</summary>
internal static class Protocol
{
    public const uint Magic = 0x43494D50; // 'PMIC'
    public const int HeaderBytes = 16;
    public const int SampleRate = 48000;
    public const int PacketMs = 10;
    public const int FrameSamples = SampleRate / 1000 * PacketMs; // 480
    public const int FrameBytes = FrameSamples * 2;               // 960
}

internal sealed class DeviceItem
{
    public DeviceItem(string name, MMDevice device)
    {
        Name = name;
        Device = device;
    }

    public string Name { get; }
    public MMDevice Device { get; }

    public override string ToString() => Name;
}

internal sealed class MainForm : Form
{
    private const string Version = "1.2";

    private readonly ComboBox _deviceBox = new();
    private readonly Button _refreshButton = new();
    private readonly NumericUpDown _portBox = new();
    private readonly NumericUpDown _latencyBox = new();
    private readonly Label _addressLabel = new();
    private readonly CheckBox _denoiseBox = new();
    private readonly TrackBar _strengthBar = new();
    private readonly Label _strengthLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _levelBar = new();
    private readonly Label _statsLabel = new();
    private readonly TextBox _logBox = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private MMDeviceEnumerator? _enumerator;
    private MicReceiver? _receiver;
    private bool _closing;

    public MainForm()
    {
        Text = "PhoneMic —— 手机当麦克风";
        ClientSize = new Size(760, 604);
        MinimumSize = new Size(720, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 249, 251);
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildLayout();
        ReloadDevices();
        UpdateStrengthLabel();

        _startButton.Click += (_, _) => StartCapture();
        _stopButton.Click += (_, _) => StopCapture();
        _refreshButton.Click += (_, _) => ReloadDevices();
        _denoiseBox.CheckedChanged += (_, _) => UpdateStrengthLabel();
        _strengthBar.ValueChanged += (_, _) => UpdateStrengthLabel();

        _timer.Interval = 100;
        _timer.Tick += OnTick;
        _timer.Start();

        FormClosing += (_, _) => { _closing = true; StopCapture(); };
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "PhoneMic",
            Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 28, 36),
            Location = new Point(16, 10),
            Size = new Size(400, 34),
        };

        var subtitle = new Label
        {
            Text = $"把手机麦克风送到电脑 · v{Version}",
            ForeColor = Color.FromArgb(110, 118, 132),
            Location = new Point(18, 44),
            Size = new Size(400, 20),
        };

        // ---- 输出设备 ----
        var deviceGroup = new GroupBox
        {
            Text = "输出设备（应当选 CABLE Input）",
            Location = new Point(16, 72),
            Size = new Size(728, 74),
        };
        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceBox.Location = new Point(14, 28);
        _deviceBox.Size = new Size(600, 26);
        _refreshButton.Text = "刷新";
        _refreshButton.Location = new Point(624, 27);
        _refreshButton.Size = new Size(90, 28);
        deviceGroup.Controls.Add(_deviceBox);
        deviceGroup.Controls.Add(_refreshButton);

        // ---- 网络 ----
        var networkGroup = new GroupBox
        {
            Text = "网络",
            Location = new Point(16, 154),
            Size = new Size(728, 78),
        };
        var portCaption = new Label { Text = "端口", Location = new Point(14, 31), Size = new Size(40, 22) };
        _portBox.Minimum = 1;
        _portBox.Maximum = 65535;
        _portBox.Value = 8765;
        _portBox.Location = new Point(58, 28);
        _portBox.Size = new Size(84, 24);

        var latencyCaption = new Label { Text = "缓冲(ms)", Location = new Point(164, 31), Size = new Size(72, 22) };
        _latencyBox.Minimum = 20;
        _latencyBox.Maximum = 300;
        _latencyBox.Increment = 10;
        _latencyBox.Value = 30;
        _latencyBox.Location = new Point(240, 28);
        _latencyBox.Size = new Size(72, 24);

        _addressLabel.Text = "本机地址：" + string.Join("  ·  ", LocalAddresses());
        _addressLabel.ForeColor = Color.FromArgb(70, 78, 90);
        _addressLabel.Location = new Point(330, 31);
        _addressLabel.Size = new Size(384, 22);
        _addressLabel.AutoEllipsis = true;

        networkGroup.Controls.Add(portCaption);
        networkGroup.Controls.Add(_portBox);
        networkGroup.Controls.Add(latencyCaption);
        networkGroup.Controls.Add(_latencyBox);
        networkGroup.Controls.Add(_addressLabel);

        // ---- 降噪 ----
        var denoiseGroup = new GroupBox
        {
            Text = "降噪",
            Location = new Point(16, 240),
            Size = new Size(728, 104),
        };
        _denoiseBox.Text = "启用降噪";
        _denoiseBox.Checked = true;
        _denoiseBox.Location = new Point(14, 26);
        _denoiseBox.Size = new Size(130, 24);

        _strengthBar.Minimum = 0;
        _strengthBar.Maximum = 10;
        _strengthBar.Value = 5;
        _strengthBar.TickStyle = TickStyle.None;
        _strengthBar.Location = new Point(146, 22);
        _strengthBar.Size = new Size(300, 36);

        _strengthLabel.Location = new Point(456, 30);
        _strengthLabel.Size = new Size(250, 22);
        _strengthLabel.ForeColor = Color.FromArgb(70, 78, 90);

        var denoiseHint = new Label
        {
            Text = "高通滤波去掉低频轰鸣 + 自适应噪声门压掉底噪。强度越高，安静时越干净，但说话开头可能被轻微削掉。",
            ForeColor = Color.FromArgb(120, 128, 140),
            Location = new Point(14, 66),
            Size = new Size(700, 22),
        };

        denoiseGroup.Controls.Add(_denoiseBox);
        denoiseGroup.Controls.Add(_strengthBar);
        denoiseGroup.Controls.Add(_strengthLabel);
        denoiseGroup.Controls.Add(denoiseHint);

        // ---- 操作 ----
        _startButton.Text = "开始";
        _startButton.Location = new Point(16, 356);
        _startButton.Size = new Size(150, 44);
        _startButton.BackColor = Color.FromArgb(38, 99, 235);
        _startButton.ForeColor = Color.White;
        _startButton.FlatStyle = FlatStyle.Flat;
        _startButton.FlatAppearance.BorderSize = 0;

        _stopButton.Text = "停止";
        _stopButton.Location = new Point(178, 356);
        _stopButton.Size = new Size(150, 44);
        _stopButton.Enabled = false;
        _stopButton.FlatStyle = FlatStyle.Flat;
        _stopButton.FlatAppearance.BorderColor = Color.FromArgb(200, 206, 216);

        _statusLabel.Text = "等待启动";
        _statusLabel.Location = new Point(348, 362);
        _statusLabel.Size = new Size(396, 30);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.ForeColor = Color.FromArgb(70, 78, 90);

        var levelCaption = new Label { Text = "电平", Location = new Point(16, 414), Size = new Size(40, 20) };
        _levelBar.Location = new Point(60, 412);
        _levelBar.Size = new Size(684, 20);
        _levelBar.Minimum = 0;
        _levelBar.Maximum = 100;

        _statsLabel.Text = "收包 0   丢包 0   缓冲 0 ms";
        _statsLabel.Location = new Point(16, 440);
        _statsLabel.Size = new Size(728, 22);
        _statsLabel.ForeColor = Color.FromArgb(120, 128, 140);

        _logBox.Location = new Point(16, 468);
        _logBox.Size = new Size(728, 118);
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.BackColor = Color.White;
        _logBox.BorderStyle = BorderStyle.FixedSingle;

        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(deviceGroup);
        Controls.Add(networkGroup);
        Controls.Add(denoiseGroup);
        Controls.Add(_startButton);
        Controls.Add(_stopButton);
        Controls.Add(_statusLabel);
        Controls.Add(levelCaption);
        Controls.Add(_levelBar);
        Controls.Add(_statsLabel);
        Controls.Add(_logBox);
    }

    private void ReloadDevices()
    {
        _deviceBox.Items.Clear();
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _deviceBox.Items.Add(new DeviceItem(device.FriendlyName, device));
            }

            var preferred = -1;
            for (var i = 0; i < _deviceBox.Items.Count; i++)
            {
                if (_deviceBox.Items[i] is DeviceItem item &&
                    item.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                {
                    preferred = i;
                    break;
                }
            }

            if (_deviceBox.Items.Count > 0)
            {
                _deviceBox.SelectedIndex = preferred >= 0 ? preferred : 0;
            }

            if (preferred >= 0)
            {
                Log("已自动选中虚拟声卡: " + _deviceBox.Items[preferred]);
            }
            else
            {
                Log("没有找到 CABLE Input。请先安装 VB-Audio Virtual Cable，否则声音只会从音箱放出来。");
            }
        }
        catch (Exception error)
        {
            Log("枚举播放设备失败: " + error.Message);
        }
    }

    private void StartCapture()
    {
        if (_receiver != null) return;

        if (_deviceBox.SelectedItem is not DeviceItem item)
        {
            Log("请先选择一个输出设备");
            return;
        }

        // Hearing your own voice is almost always this: the program is playing the
        // phone audio into the speakers instead of the virtual cable. Ask before
        // doing that, and offer to switch.
        if (!item.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
        {
            var cable = FindCableDevice();
            if (cable != null)
            {
                var answer = MessageBox.Show(
                    "当前输出设备不是虚拟声卡：\n\n" + item.Name +
                    "\n\n这样手机的声音会直接从音箱或耳机放出来——你会听到自己说话，而对方仍然听不到。\n\n" +
                    "要切换到 " + cable.Name + " 吗？",
                    "输出设备不是虚拟声卡",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes)
                {
                    item = cable;
                    _deviceBox.SelectedItem = cable;
                }
            }
            else
            {
                var answer = MessageBox.Show(
                    "没有找到 CABLE Input，声音会被放到音箱或耳机里，你会听到自己。\n\n仍然继续吗？",
                    "没有找到虚拟声卡",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }
        }

        var receiver = new MicReceiver();
        try
        {
            receiver.Start(item.Device,
                (int)_portBox.Value,
                (int)_latencyBox.Value,
                _denoiseBox.Checked,
                _strengthBar.Value / 10.0);
        }
        catch (Exception error)
        {
            receiver.Dispose();
            Log("启动失败: " + error.Message);
            return;
        }

        _receiver = receiver;
        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        _deviceBox.Enabled = false;
        _portBox.Enabled = false;
        _latencyBox.Enabled = false;
        _denoiseBox.Enabled = false;
        _strengthBar.Enabled = false;
        Log($"开始监听 UDP {_portBox.Value}，输出到 {item.Name}");
    }

    private DeviceItem? FindCableDevice()
    {
        foreach (var entry in _deviceBox.Items)
        {
            if (entry is DeviceItem candidate &&
                candidate.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private void StopCapture()
    {
        if (_receiver == null) return;

        _receiver.Dispose();
        _receiver = null;

        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        _deviceBox.Enabled = true;
        _portBox.Enabled = true;
        _latencyBox.Enabled = true;
        _denoiseBox.Enabled = true;
        _strengthBar.Enabled = true;

        _statusLabel.Text = "已停止";
        _levelBar.Value = 0;
        _statsLabel.Text = "收包 0   丢包 0   缓冲 0 ms";
        if (!_closing) Log("已停止");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var receiver = _receiver;
        if (receiver == null) return;

        _statusLabel.Text = receiver.ClientText.Length > 0
            ? "已连接 " + receiver.ClientText
            : "等待手机连接…";

        var level = (int)Math.Round(receiver.Level * 100);
        _levelBar.Value = Math.Clamp(level, 0, 100);

        _statsLabel.Text =
            $"收包 {receiver.PacketsReceived}   丢包 {receiver.PacketsDropped}   缓冲 {receiver.BufferedMs} ms";
    }

    private void UpdateStrengthLabel()
    {
        var weight = _denoiseBox.Checked ? "" : "（已关闭）";
        _strengthLabel.Text = $"强度 {_strengthBar.Value} / 10 {weight}";
        _strengthBar.Enabled = _denoiseBox.Checked;
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logBox.AppendText(line + Environment.NewLine);
    }

    private static IEnumerable<string> LocalAddresses()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                list.Add(unicast.Address.ToString());
            }
        }

        return list.Count > 0 ? list : new List<string> { "没有可用的 IPv4 地址" };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _receiver?.Dispose();
            _enumerator?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Owns the socket, the jitter buffer and the audio sink for one session.
/// Everything it exposes to the form is a plain volatile value, so the UI
/// timer can read it without locking.
/// </summary>
internal sealed class MicReceiver : IDisposable
{
    private Socket? _socket;
    private Thread? _receiveThread;
    private Thread? _pumpThread;
    private AudioSink? _sink;
    private JitterBuffer? _jitter;
    private volatile bool _running;

    private long _received;
    private long _dropped;

    public volatile string ClientText = "";
    public volatile float Level;

    public long PacketsReceived => Interlocked.Read(ref _received);
    public long PacketsDropped => Interlocked.Read(ref _dropped);
    public int BufferedMs => _sink?.BufferedMs ?? 0;

    public void Start(MMDevice device, int port, int latencyMs, bool denoise, double strength)
    {
        _sink = new AudioSink(device, latencyMs);
        var denoiser = new Denoiser(denoise, strength);
        _jitter = new JitterBuffer(_sink, denoiser, latencyMs, this);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 1 << 20;
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        _socket = socket;

        _running = true;

        _receiveThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "phonemic-rx",
        };
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "phonemic-jitter",
        };

        _receiveThread.Start();
        _pumpThread.Start();
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[4096];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                var count = _socket!.ReceiveFrom(buffer, ref remote);
                if (count < Protocol.HeaderBytes + 2) continue;
                if (BitConverter.ToUInt32(buffer, 0) != Protocol.Magic) continue;

                var sequence = BitConverter.ToUInt32(buffer, 4);
                var payloadBytes = count - Protocol.HeaderBytes;
                var pcm = new byte[payloadBytes];
                Buffer.BlockCopy(buffer, Protocol.HeaderBytes, pcm, 0, payloadBytes);

                ClientText = remote.ToString() ?? "";
                Interlocked.Increment(ref _received);
                _jitter!.Push(sequence, pcm);
            }
            catch (SocketException)
            {
                if (!_running) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void PumpLoop()
    {
        while (_running)
        {
            _jitter?.Drain();
            Thread.Sleep(5);
        }
    }

    internal void NoteDropped() => Interlocked.Increment(ref _dropped);

    internal void NoteLevel(float peak) => Level = peak;

    public void Dispose()
    {
        _running = false;

        try { _socket?.Close(); } catch { /* already closed */ }
        _socket = null;

        try { _receiveThread?.Join(800); } catch { /* ignore */ }
        try { _pumpThread?.Join(800); } catch { /* ignore */ }
        _receiveThread = null;
        _pumpThread = null;

        try { _sink?.Dispose(); } catch { /* ignore */ }
        _sink = null;
        _jitter = null;
    }
}

/// <summary>Plays frames into a Windows render device.</summary>
internal sealed class AudioSink : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;

    public AudioSink(MMDevice device, int latencyMs)
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(Protocol.SampleRate, 16, 1))
        {
            // ReadFully keeps the stream alive when the gate closes: an underrun
            // would produce a stutter, and a silent gate must still be silent,
            // not choppy.
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(400),
        };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, Math.Max(20, latencyMs / 3));
        _output.Init(_buffer);
        _output.Play();
    }

    public int BufferedMs => (int)_buffer.BufferedDuration.TotalMilliseconds;

    public void Write(byte[] pcm) => _buffer.AddSamples(pcm, 0, pcm.Length);

    public void Dispose()
    {
        try { _output.Stop(); } catch { /* already stopped */ }
        _output.Dispose();
    }
}

/// <summary>
/// Re-orders datagrams, then runs each frame through the denoiser before it
/// reaches the sink.
/// </summary>
internal sealed class JitterBuffer
{
    private readonly AudioSink _sink;
    private readonly Denoiser _denoiser;
    private readonly MicReceiver _owner;
    private readonly object _gate = new();
    private readonly Dictionary<uint, byte[]> _held = new();
    private readonly int _targetPackets;

    private uint _next;
    private bool _primed;
    private long _lastEmit;

    public JitterBuffer(AudioSink sink, Denoiser denoiser, int latencyMs, MicReceiver owner)
    {
        _sink = sink;
        _denoiser = denoiser;
        _owner = owner;
        _targetPackets = Math.Max(2, latencyMs / Protocol.PacketMs);
    }

    public void Push(uint sequence, byte[] pcm)
    {
        lock (_gate)
        {
            if (_primed && sequence < _next)
            {
                _owner.NoteDropped();
                return;
            }

            if (_held.Count >= 256)
            {
                _held.Remove(_held.Keys.Min());
                _owner.NoteDropped();
            }

            _held[sequence] = pcm;
        }
    }

    public void Drain()
    {
        lock (_gate)
        {
            if (_held.Count == 0) return;

            if (!_primed)
            {
                if (_held.Count < _targetPackets) return;
                _primed = true;
                _next = _held.Keys.Min();
                _lastEmit = Environment.TickCount64;
            }

            var now = Environment.TickCount64;
            while (_held.TryGetValue(_next, out var pcm))
            {
                _held.Remove(_next);
                var peak = _denoiser.Process(pcm);
                _owner.NoteLevel(peak);
                _sink.Write(pcm);
                _next++;
                _lastEmit = now;
            }

            // A packet that never arrives must not freeze the stream forever.
            if (_held.Count > 0 && now - _lastEmit > 40)
            {
                _next = _held.Keys.Min();
                _lastEmit = now;
            }
        }
    }
}

/// <summary>
/// Two stage cleanup for a fan in the room:
///   1. a second order high pass at 80 Hz, which removes rumble and the low
///      half of most fan noise without touching speech;
///   2. an adaptive downward expander. The noise floor is tracked from the
///      quietest recent level and the gate closes below floor + margin, so a
///      steady hiss is pushed down between phrases while speech passes.
/// Gain is smoothed per sample, so the gate never clicks.
/// </summary>
internal sealed class Denoiser
{
    private readonly bool _enabled;
    private readonly double _marginDb;

    // High pass biquad state (direct form I).
    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;
    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;

    // Envelope / gate state.
    private double _envelope;
    private double _noiseFloorDb = -70;
    private float _gain = 0.03f;
    private readonly double _attackCoef;
    private readonly double _releaseCoef;
    private readonly int _holdSamples;
    private int _aboveSamples;

    private const double ClosedGain = 0.03;   // about -30 dB, near silent
    private const double AttackMs = 15;
    private const double ReleaseMs = 140;
    private const double HoldMs = 12;

    public Denoiser(bool enabled, double strength)
    {
        _enabled = enabled;

        // 0..1 strength maps to a 4..14 dB margin above the tracked floor.
        _marginDb = 4 + 10 * Math.Clamp(strength, 0, 1);

        const double cutoff = 80.0;
        var sampleRate = Protocol.SampleRate;
        var w0 = 2 * Math.PI * cutoff / sampleRate;
        var cosw = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * 0.7071);

        var a0 = 1 + alpha;
        _b0 = (1 + cosw) / 2 / a0;
        _b1 = -(1 + cosw) / a0;
        _b2 = (1 + cosw) / 2 / a0;
        _a1 = -2 * cosw / a0;
        _a2 = (1 - alpha) / a0;

        _attackCoef = 1 - Math.Exp(-1.0 / (AttackMs / 1000.0 * sampleRate));
        _releaseCoef = 1 - Math.Exp(-1.0 / (ReleaseMs / 1000.0 * sampleRate));
        _holdSamples = (int)(HoldMs / 1000.0 * sampleRate);
    }

    /// <summary>Processes one frame in place and returns its peak level (0..1).</summary>
    public float Process(byte[] pcm)
    {
        var peak = 0f;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var value = sample / 32768f;

            if (_enabled)
            {
                value = HighPass(value);
                value = ApplyGate(value);
            }

            if (value > 1f) value = 1f;
            else if (value < -1f) value = -1f;

            var magnitude = Math.Abs(value);
            if (magnitude > peak) peak = magnitude;

            var encoded = (short)Math.Clamp((int)(value * 32767f), short.MinValue, short.MaxValue);
            pcm[i] = (byte)(encoded & 0xFF);
            pcm[i + 1] = (byte)((encoded >> 8) & 0xFF);
        }

        return peak;
    }

    private float HighPass(double input)
    {
        var output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = input;
        _y2 = _y1;
        _y1 = output;
        return (float)output;
    }

    private float ApplyGate(float input)
    {
        var magnitude = Math.Abs(input);

        // Fast attack, slower release: the envelope should catch a consonant
        // immediately but not flicker between syllables.
        var coefficient = magnitude > _envelope ? _attackCoef : _releaseCoef;
        _envelope += (magnitude - _envelope) * coefficient;

        var levelDb = 20 * Math.Log10(_envelope + 1e-9);

        // Track the quietest recent level. Falling is instant, rising is slow,
        // so a passing noise burst cannot drag the floor up with it.
        if (levelDb < _noiseFloorDb)
        {
            _noiseFloorDb = levelDb;
        }
        else
        {
            _noiseFloorDb += 0.0004 * (levelDb - _noiseFloorDb);
        }

        if (_noiseFloorDb < -70) _noiseFloorDb = -70;

        var thresholdDb = _noiseFloorDb + _marginDb;
        if (thresholdDb < -55) thresholdDb = -55;

        // A mouse click is a 1-5 ms impulse: far above the threshold, but gone
        // again almost immediately. Requiring the level to stay above the
        // threshold for HoldMs before the gate may open means such an impulse is
        // never amplified — the gate is still at ClosedGain when it is over.
        // Speech sustains far longer than 12 ms, so this costs the voice nothing.
        bool open;
        if (_gain < 0.2f)
        {
            if (levelDb > thresholdDb) _aboveSamples++;
            else _aboveSamples = 0;
            open = _aboveSamples > _holdSamples;
        }
        else
        {
            // Already open: follow the level directly, so a syllable gap does not
            // have to re-arm the hold.
            open = levelDb > thresholdDb;
            _aboveSamples = _holdSamples + 1;
        }

        var target = open ? 1.0 : ClosedGain;
        var smoothing = target > _gain ? _attackCoef : _releaseCoef;
        _gain += (float)((target - _gain) * smoothing);

        return input * _gain;
    }
}
