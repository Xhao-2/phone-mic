using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneMic;

/// <summary>
/// Receives PCM audio from the Android client over UDP and plays it into a
/// Windows render device. Point that device at VB-Audio CABLE Input and the
/// phone becomes a real microphone for every other application.
/// </summary>
internal static class Program
{
    // ---- Wire format (must match the Android client) --------------------
    // Header: 16 bytes, little-endian
    //   0..3   magic  'PMIC'
    //   4..7   uint32 sequence number
    //   8..15  uint64 sender timestamp in milliseconds (monotonic)
    // Payload: 960 bytes = 480 samples * 2 bytes = 10 ms of 48 kHz mono S16LE
    internal const int SampleRate = 48000;
    internal const int Channels = 1;
    internal const int BitsPerSample = 16;
    internal const int PacketMs = 10;
    internal const int HeaderBytes = 16;
    internal const uint Magic = 0x43494D50; // 'PMIC'

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"参数错误: {ex.Message}");
            PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.ListDevices)
        {
            PrintDevices();
            return 0;
        }

        MMDevice device;
        try
        {
            device = ResolveDevice(options.DeviceQuery);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"找不到可用的播放设备: {ex.Message}");
            Console.Error.WriteLine("用 --list 列出全部设备，再用 --device <名称片段> 指定。");
            return 3;
        }

        Console.WriteLine("PhoneMic Server 1.0");
        Console.WriteLine($"输出设备: {device.FriendlyName}");
        Console.WriteLine($"监听端口: UDP {options.Port}");
        Console.WriteLine();
        Console.WriteLine("在手机上填入下面任意一个地址：");
        foreach (var address in LocalAddresses())
        {
            Console.WriteLine($"    {address}");
        }
        Console.WriteLine();
        Console.WriteLine("提示: 本程序把音频输出到所选设备。要让它变成麦克风，");
        Console.WriteLine("      请先把所选设备设为 VB-Audio \"CABLE Input\"，");
        Console.WriteLine("      然后在其他软件里把麦克风选成 \"CABLE Output\"。");
        Console.WriteLine();
        Console.WriteLine("等待手机连接... 按 Ctrl+C 退出。");

        try
        {
            using var sink = new AudioSink(device, options.LatencyMs);
            var jitter = new JitterBuffer(sink, options.LatencyMs);

            var pump = new Thread(jitter.Run) { IsBackground = true, Name = "jitter-pump" };
            pump.Start();

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveBufferSize = 1 << 20;
            socket.Bind(new IPEndPoint(IPAddress.Any, options.Port));

            var buffer = new byte[4096];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            var firstPacket = true;

            while (true)
            {
                int received;
                try
                {
                    received = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine($"接收失败: {ex.Message}");
                    continue;
                }

                if (received < HeaderBytes + 2) continue;
                if (BitConverter.ToUInt32(buffer, 0) != Magic) continue;

                uint seq = BitConverter.ToUInt32(buffer, 4);
                int payloadBytes = received - HeaderBytes;
                var pcm = new byte[payloadBytes];
                Buffer.BlockCopy(buffer, HeaderBytes, pcm, 0, payloadBytes);
                jitter.Push(seq, pcm);

                if (firstPacket)
                {
                    firstPacket = false;
                    Console.WriteLine($"收到来自 {remote} 的音频，开始播放。");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("PhoneMic Server —— 把手机麦克风变成电脑麦克风");
        Console.WriteLine();
        Console.WriteLine("用法: PhoneMicServer.exe [选项]");
        Console.WriteLine();
        Console.WriteLine("  --port <n>         UDP 监听端口，默认 8765");
        Console.WriteLine("  --device <文本>    输出设备名称片段，默认自动找 CABLE Input");
        Console.WriteLine("  --latency <ms>     抖动缓冲目标，默认 60");
        Console.WriteLine("  --list             列出所有播放设备后退出");
        Console.WriteLine("  --help             显示本帮助");
    }

    private static void PrintDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        Console.WriteLine($"共 {devices.Count} 个播放设备：");
        for (var i = 0; i < devices.Count; i++)
        {
            Console.WriteLine($"  [{i}] {devices[i].FriendlyName}");
        }
    }

    private static MMDevice ResolveDevice(string? query)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        if (devices.Count == 0)
            throw new InvalidOperationException("系统里没有任何活动的播放设备");

        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var candidate in devices)
            {
                if (candidate.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            throw new InvalidOperationException($"没有名称包含 \"{query}\" 的播放设备");
        }

        // Prefer the virtual cable: that is the whole point of this application.
        foreach (var candidate in devices)
        {
            if (candidate.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        Console.WriteLine("警告: 没有找到 VB-Audio \"CABLE Input\"，将使用系统默认播放设备。");
        Console.WriteLine("      这样声音只会从音箱放出来，不会变成麦克风。");
        Console.WriteLine("      请先安装 VB-Audio Virtual Cable，或用 --device 指定设备。");
        Console.WriteLine();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static IEnumerable<string> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                yield return $"{unicast.Address}  ({nic.Name})";
            }
        }
    }
}

/// <summary>Command line options.</summary>
internal sealed class Options
{
    public int Port { get; private set; } = 8765;
    public string? DeviceQuery { get; private set; }
    public int LatencyMs { get; private set; } = 60;
    public bool ListDevices { get; private set; }
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    options.ShowHelp = true;
                    break;
                case "--list":
                    options.ListDevices = true;
                    break;
                case "--port":
                    options.Port = ParseInt(Next(args, ref i, arg), arg, 1, 65535);
                    break;
                case "--latency":
                    options.LatencyMs = ParseInt(Next(args, ref i, arg), arg, 10, 500);
                    break;
                case "--device":
                    options.DeviceQuery = Next(args, ref i, arg);
                    break;
                default:
                    throw new ArgumentException($"未知参数 \"{arg}\"");
            }
        }
        return options;
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{name} 需要一个值");
        return args[++index];
    }

    private static int ParseInt(string text, string name, int min, int max)
    {
        if (!int.TryParse(text, out var value))
            throw new ArgumentException($"{name} 需要一个整数，收到 \"{text}\"");
        if (value < min || value > max)
            throw new ArgumentException($"{name} 必须在 {min} 到 {max} 之间");
        return value;
    }
}

/// <summary>Plays received frames into a Windows render device.</summary>
internal sealed class AudioSink : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;

    public AudioSink(MMDevice device, int latencyMs)
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(Program.SampleRate, Program.BitsPerSample, Program.Channels))
        {
            // ReadFully keeps the stream continuous: an underrun becomes silence
            // instead of a stutter, which matters for a microphone feed.
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(400),
        };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, Math.Max(20, latencyMs / 2));
        _output.Init(_buffer);
        _output.Play();
    }

    public void Write(byte[] pcm) => _buffer.AddSamples(pcm, 0, pcm.Length);

    public void Dispose()
    {
        try { _output.Stop(); } catch { /* already stopped */ }
        _output.Dispose();
    }
}

/// <summary>
/// Re-orders datagrams and absorbs Wi-Fi jitter before they reach the sink.
/// Without this a single late packet would be played twice or skipped, and
/// the delay would drift instead of staying bounded.
/// </summary>
internal sealed class JitterBuffer
{
    private readonly AudioSink _sink;
    private readonly object _gate = new();
    private readonly Dictionary<uint, byte[]> _held = new();
    private readonly int _targetPackets;

    private uint _next;
    private bool _primed;
    private long _lastEmit;

    public JitterBuffer(AudioSink sink, int latencyMs)
    {
        _sink = sink;
        _targetPackets = Math.Max(2, latencyMs / Program.PacketMs);
    }

    /// <summary>Called from the receive thread for every datagram.</summary>
    public void Push(uint sequence, byte[] pcm)
    {
        lock (_gate)
        {
            if (_primed && sequence < _next) return;      // arrived too late
            if (_held.Count >= 256)                        // sender is far ahead
            {
                _held.Remove(_held.Keys.Min());
            }
            _held[sequence] = pcm;
        }
    }

    /// <summary>Continuously drains the buffer in order.</summary>
    public void Run()
    {
        while (true)
        {
            Drain();
            Thread.Sleep(5);
        }
    }

    private void Drain()
    {
        lock (_gate)
        {
            if (_held.Count == 0) return;

            if (!_primed)
            {
                // Wait for a cushion before the first sample, otherwise the
                // opening second stutters.
                if (_held.Count < _targetPackets) return;
                _primed = true;
                _next = _held.Keys.Min();
                _lastEmit = Environment.TickCount64;
            }

            var now = Environment.TickCount64;
            while (_held.TryGetValue(_next, out var pcm))
            {
                _held.Remove(_next);
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
