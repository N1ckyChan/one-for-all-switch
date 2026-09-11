// SPDX-License-Identifier: GPL-2.0-only
#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KvmSwitch;

public enum KvmTarget : byte
{
    Main = 0,
    Laptop = 1
}

public sealed class KvmConfig
{
    private static readonly object SaveLock = new();
    internal string? StoragePath { get; set; }
    public int ConfigVersion { get; set; }
    public string Role { get; set; } = string.Empty;
    public string ServerAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 24800;
    // Separate authenticated control channel. Deskflow owns 24800.
    public int ControlPort { get; set; } = 24801;
    public bool AutoPort { get; set; }
    public bool AutoDiscover { get; set; } = true;
    public string AuthToken { get; set; } = string.Empty;
    public byte MainInput { get; set; } = 0x0F;
    public byte LaptopInput { get; set; } = 0x11;
    public bool SyncBrightness { get; set; }
    public bool BrightnessConfigured { get; set; }
    public int BrightnessPercent { get; set; } = 50;
    public int BrightnessDelayMs { get; set; } = 250;
    public int DdcRetryCount { get; set; } = 4;
    public string Hotkey { get; set; } = "F12";
    public string LastTarget { get; set; } = "main";
    public string DisplayName { get; set; } = "main";
    public string FirewallRuntimePath { get; set; } = string.Empty;
    public bool FirewallControlConfigured { get; set; }
    public bool AutoStart { get; set; }
    public bool KeepClientAwake { get; set; } = true;
    public bool ElevateClient { get; set; } = true;

    public bool IsHost => string.Equals(Role, "host", StringComparison.OrdinalIgnoreCase);
    public bool IsClient => string.Equals(Role, "client", StringComparison.OrdinalIgnoreCase);
    public bool IsConfigured => ConfigVersion >= 2 && (IsHost || IsClient);

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KvmSwitch");

    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static KvmConfig Load(string[] args)
    {
        Directory.CreateDirectory(DirectoryPath);
        KvmConfig config;
        if (File.Exists(FilePath))
        {
            try
            {
                config = JsonSerializer.Deserialize<KvmConfig>(AtomicConfigFile.Read(FilePath)) ?? new KvmConfig();
            }
            catch (JsonException)
            {
                config = new KvmConfig();
            }
        }
        else
        {
            config = new KvmConfig();
        }

        config.BrightnessPercent = Math.Clamp(config.BrightnessPercent, 0, 100);
        config.BrightnessDelayMs = Math.Clamp(config.BrightnessDelayMs, 50, 2000);
        config.DdcRetryCount = Math.Clamp(config.DdcRetryCount, 1, 8);
        config.ControlPort = Math.Clamp(config.ControlPort == 0 ? 24801 : config.ControlPort, 1024, 65535);
        if (!config.BrightnessConfigured)
            config.SyncBrightness = false;

        var changedByArguments = false;
        var explicitToken = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--host", StringComparison.OrdinalIgnoreCase))
            {
                config.Role = "host";
                config.DisplayName = "main";
                changedByArguments = true;
            }
            else if (arg.Equals("--client", StringComparison.OrdinalIgnoreCase))
            {
                config.Role = "client";
                config.DisplayName = "laptop";
                changedByArguments = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    config.ServerAddress = args[++i];
            }
            else if (arg.Equals("--server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                config.ServerAddress = args[++i];
                changedByArguments = true;
            }
            else if (arg.Equals("--token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                config.AuthToken = args[++i];
                explicitToken = true;
                changedByArguments = true;
            }
            else if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out var port))
            {
                config.Port = port == 0 ? 0 : Math.Clamp(port, 1024, 65535);
                config.AutoPort = port == 0;
                changedByArguments = true;
            }
            else if (arg.Equals("--main-input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                config.MainInput = ParseByte(args[++i], config.MainInput);
            }
            else if (arg.Equals("--laptop-input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                config.LaptopInput = ParseByte(args[++i], config.LaptopInput);
            }
        }

        if (config.IsHost && string.IsNullOrWhiteSpace(config.AuthToken) && changedByArguments)
            config.AuthToken = CreateToken();
        if (config.IsClient && explicitToken && !config.AutoPort)
            config.AutoDiscover = false;
        if (changedByArguments)
        {
            config.ConfigVersion = 2;
            config.Save();
        }
        return config;
    }

    public static string CreateToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public void Save()
    {
        lock (SaveLock)
        {
            var path = StoragePath ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            AtomicConfigFile.Write(path, json);
        }
    }

    private static byte ParseByte(string value, byte fallback)
    {
        value = value.Trim();
        try
        {
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToByte(value[2..], 16)
                : Convert.ToByte(value, 10);
        }
        catch
        {
            return fallback;
        }
    }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KvmSwitch";

    public static void Apply(bool enabled, bool elevatedClient = false)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 启动项注册表");
        if (!enabled)
        {
            ElevatedStartupTask.Apply(false, Application.ExecutablePath);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            executable = Application.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("无法确定 one-for-all-switch.exe 的启动路径", executable);

        ElevatedStartupTask.Apply(elevatedClient, executable);
        if (elevatedClient)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        else
            key.SetValue(ValueName, $"\"{executable}\" --autostart", RegistryValueKind.String);
    }
}

public sealed class HotkeyBinding
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;

    public uint Modifiers { get; }
    public Keys Key { get; }

    public HotkeyBinding(uint modifiers, Keys key)
    {
        Modifiers = modifiers;
        Key = key & Keys.KeyCode;
        if (Key == Keys.None) throw new ArgumentException("热键必须包含一个按键", nameof(key));
    }

    public static HotkeyBinding Parse(string? value)
    {
        uint modifiers = 0;
        Keys key = Keys.F12;
        foreach (var raw in (value ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= Control; break;
                case "alt": modifiers |= Alt; break;
                case "shift": modifiers |= Shift; break;
                case "win":
                case "windows": modifiers |= Win; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0]))
                        key = Keys.D0 + (raw[0] - '0');
                    else if (Enum.TryParse<Keys>(raw, true, out var parsed))
                        key = parsed & Keys.KeyCode;
                    break;
            }
        }
        return new HotkeyBinding(modifiers, key);
    }

    public static HotkeyBinding FromKeyEvent(KeyEventArgs e)
    {
        uint modifiers = 0;
        if (e.Control) modifiers |= Control;
        if (e.Alt) modifiers |= Alt;
        if (e.Shift) modifiers |= Shift;
        if ((e.KeyData & Keys.LWin) == Keys.LWin || (e.KeyData & Keys.RWin) == Keys.RWin) modifiers |= Win;
        return new HotkeyBinding(modifiers, e.KeyCode);
    }

    public bool ContainsVirtualKey(ushort virtualKey)
    {
        if ((ushort)Key == virtualKey) return true;
        if ((Modifiers & Control) != 0 && virtualKey is 0x11 or 0xA2 or 0xA3) return true;
        if ((Modifiers & Alt) != 0 && virtualKey is 0x12 or 0xA4 or 0xA5) return true;
        if ((Modifiers & Shift) != 0 && virtualKey is 0x10 or 0xA0 or 0xA1) return true;
        return (Modifiers & Win) != 0 && virtualKey is 0x5B or 0x5C;
    }

    public override string ToString()
    {
        var parts = new StringBuilder();
        void Add(string value)
        {
            if (parts.Length > 0) parts.Append('+');
            parts.Append(value);
        }
        if ((Modifiers & Control) != 0) Add("Ctrl");
        if ((Modifiers & Alt) != 0) Add("Alt");
        if ((Modifiers & Shift) != 0) Add("Shift");
        if ((Modifiers & Win) != 0) Add("Win");
        Add(Key >= Keys.D0 && Key <= Keys.D9
            ? ((char)('0' + (Key - Keys.D0))).ToString()
            : Key.ToString());
        return parts.ToString();
    }

    public string ToDeskflow()
    {
        var parts = new StringBuilder();
        void Add(string value)
        {
            if (parts.Length > 0) parts.Append('+');
            parts.Append(value);
        }
        if ((Modifiers & Control) != 0) Add("Control");
        if ((Modifiers & Alt) != 0) Add("Alt");
        if ((Modifiers & Shift) != 0) Add("Shift");
        if ((Modifiers & Win) != 0) Add("Super");
        Add(ToDeskflowKey(Key));
        return parts.ToString();
    }

    private static string ToDeskflowKey(Keys key)
    {
        key &= Keys.KeyCode;
        if (key >= Keys.D0 && key <= Keys.D9)
            return ((char)('0' + (key - Keys.D0))).ToString();
        if (key >= Keys.A && key <= Keys.Z)
            return key.ToString();
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return $"KP_{key - Keys.NumPad0}";

        return key switch
        {
            Keys.Enter => "Return",
            Keys.Back => "BackSpace",
            Keys.Prior => "PageUp",
            Keys.Next => "PageDown",
            Keys.Capital => "CapsLock",
            Keys.Apps => "Menu",
            Keys.Space => "Space",
            Keys.Add => "KP_Add",
            Keys.Subtract => "KP_Subtract",
            Keys.Multiply => "KP_Multiply",
            Keys.Divide => "KP_Divide",
            Keys.Decimal => "KP_Decimal",
            Keys.Separator => "KP_Separator",
            Keys.Oem1 => "Semicolon",
            Keys.Oem2 => "Slash",
            Keys.Oem3 => "Grave",
            Keys.Oem4 => "BracketL",
            Keys.Oem5 => "Backslash",
            Keys.Oem6 => "BracketR",
            Keys.Oem7 => "Apostrophe",
            Keys.Oemcomma => "Comma",
            Keys.OemPeriod => "Period",
            Keys.OemMinus => "Minus",
            Keys.Oemplus => "Plus",
            _ => key.ToString()
        };
    }
}

internal static class Protocol
{
    public const byte Hello = 1;
    public const byte Key = 2;
    public const byte MouseMove = 3;
    public const byte MouseButton = 4;
    public const byte MouseWheel = 5;
    public const byte SetInput = 6;
    public const byte Route = 7;
    public const byte Ack = 8;
    public const byte Ping = 9;
    public const byte Wake = 10;

    public static byte[] Frame(byte type, Action<BinaryWriter>? payload = null)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            writer.Write(type);
            payload?.Invoke(writer);
        }

        var bytes = body.ToArray();
        using var frame = new MemoryStream();
        using (var writer = new BinaryWriter(frame, Encoding.UTF8, true))
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        return frame.ToArray();
    }
}

internal sealed class KvmDiscoveryEndpoint
{
    public required string Address { get; init; }
    public required int Port { get; init; }
    public int ControlPort { get; init; } = 24801;
    public required string Token { get; init; }
    public string Name { get; init; } = "main";
}

internal static class KvmDiscovery
{
    public const int DiscoveryPort = 37998;
    private const string Request = "KvmSwitch.Discover.v1";

    private sealed class Advertisement
    {
        public string Kind { get; set; } = string.Empty;
        public int Port { get; set; }
        public int ControlPort { get; set; } = 24801;
        public string Token { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public static async Task<KvmDiscoveryEndpoint?> FindHostAsync(string? preferredAddress, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        var targets = await ResolveTargets(preferredAddress).ConfigureAwait(false);
        if (targets.Length == 0) targets = new[] { IPAddress.Broadcast };

        var request = Encoding.UTF8.GetBytes(Request);
        foreach (var address in targets)
            await socket.SendAsync(request, request.Length, new IPEndPoint(address, DiscoveryPort)).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                Advertisement? advertisement;
                try
                {
                    advertisement = JsonSerializer.Deserialize<Advertisement>(result.Buffer);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (advertisement?.Kind == "host" && advertisement.Port is >= 1024 and <= 65535 && !string.IsNullOrWhiteSpace(advertisement.Token))
                {
                    return new KvmDiscoveryEndpoint
                    {
                        Address = result.RemoteEndPoint.Address.ToString(),
                        Port = advertisement.Port,
                        ControlPort = advertisement.ControlPort is >= 1024 and <= 65535
                            ? advertisement.ControlPort
                            : 24801,
                        Token = advertisement.Token,
                        Name = string.IsNullOrWhiteSpace(advertisement.Name) ? "main" : advertisement.Name
                    };
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public static UdpClient CreateListener() => new(new IPEndPoint(IPAddress.Any, DiscoveryPort));

    public static byte[] CreateAdvertisement(int port, int controlPort, string token, string name) =>
        JsonSerializer.SerializeToUtf8Bytes(new Advertisement
        {
            Kind = "host",
            Port = port,
            ControlPort = controlPort,
            Token = token,
            Name = name
        });

    private static async Task<IPAddress[]> ResolveTargets(string? preferredAddress)
    {
        var targets = new System.Collections.Generic.HashSet<IPAddress>();
        if (!string.IsNullOrWhiteSpace(preferredAddress))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(preferredAddress).ConfigureAwait(false);
                foreach (var address in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
                    targets.Add(address);
            }
            catch { }
        }

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.PrefixLength is <= 0 or >= 32)
                    continue;
                var bytes = address.Address.GetAddressBytes();
                var hostBits = 32 - address.PrefixLength;
                var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                var mask = uint.MaxValue << hostBits;
                var broadcast = value | ~mask;
                targets.Add(new IPAddress(new[]
                {
                    (byte)(broadcast >> 24),
                    (byte)(broadcast >> 16),
                    (byte)(broadcast >> 8),
                    (byte)broadcast
                }));
            }
        }
        targets.Add(IPAddress.Broadcast);
        return targets.ToArray();
    }

    public static bool IsRequest(byte[] data) =>
        string.Equals(Encoding.UTF8.GetString(data), Request, StringComparison.Ordinal);
}

internal sealed class KvmDiscoveryHost : IDisposable
{
    private readonly int _port;
    private readonly int _controlPort;
    private readonly string _token;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private readonly UdpClient _socket;

    public KvmDiscoveryHost(int port, int controlPort, string token, string name)
    {
        _port = port;
        _controlPort = controlPort;
        _token = token;
        _name = name;
        _socket = KvmDiscovery.CreateListener();
    }

    public void Start() => _ = Task.Run(Loop);

    private async Task Loop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var request = await _socket.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (!KvmDiscovery.IsRequest(request.Buffer)) continue;
                var response = KvmDiscovery.CreateAdvertisement(_port, _controlPort, _token, _name);
                await _socket.SendAsync(response, response.Length, request.RemoteEndPoint).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }
}

internal sealed class KvmConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sendLock = new();
    private Task? _reader;
    private volatile int _closed;

    public event Action<KvmConnection, byte, byte[]>? MessageReceived;
    public event Action<KvmConnection>? Closed;

    public KvmConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public void Start()
    {
        _reader ??= Task.Run(ReadLoop);
    }

    public bool Send(byte[] frame)
    {
        if (_closed != 0) return false;
        lock (_sendLock)
        {
            if (_closed != 0) return false;
            try
            {
                _stream.Write(frame, 0, frame.Length);
                return true;
            }
            catch
            {
                Close();
                return false;
            }
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            var lengthBuffer = new byte[4];
            while (!_cts.IsCancellationRequested)
            {
                await ReadExact(lengthBuffer, _cts.Token).ConfigureAwait(false);
                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length < 1 || length > 1024 * 1024) throw new InvalidDataException("Invalid frame length");
                var frame = new byte[length];
                await ReadExact(frame, _cts.Token).ConfigureAwait(false);
                MessageReceived?.Invoke(this, frame[0], frame[1..]);
            }
        }
        catch
        {
            Close();
        }
    }

    private async Task ReadExact(byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await _stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }

    public void Close()
    {
        if (_closed != 0) return;
        _closed = 1;
        try { _cts.Cancel(); } catch { }
        try { _client.Close(); } catch { }
        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        Close();
        _cts.Dispose();
    }
}

internal static class TcpConnectionSettings
{
    public static void EnableKeepAlive(TcpClient client)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ArgumentException)
        {
            // The wake protocol still has application-level retries when a
            // Windows/network-driver combination does not expose these options.
        }
    }
}

internal sealed class KvmHost : IDisposable
{
    private readonly KvmConfig _config;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private KvmConnection? _client;
    private KvmDiscoveryHost? _discovery;
    private int _authorized;

    public event Action<bool>? ConnectionChanged;
    public event Action<string>? StatusChanged;
    public KvmConnection? Client => _authorized != 0 ? _client : null;

    public KvmHost(KvmConfig config)
    {
        _config = config;
        _listener = new TcpListener(IPAddress.Any, config.AutoPort ? 0 : config.Port);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        try
        {
            _discovery = new KvmDiscoveryHost(port, _config.ControlPort, _config.AuthToken, _config.DisplayName);
            _discovery.Start();
            StatusChanged?.Invoke($"已启动，自动端口 {port}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"已启动端口 {port}，局域网自动发现不可用: {ex.Message}");
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                TcpConnectionSettings.EnableKeepAlive(tcp);
                var connection = new KvmConnection(tcp);
                connection.MessageReceived += OnMessage;
                connection.Closed += OnClosed;
                connection.Start();
                var old = _client;
                _client = connection;
                _authorized = 0;
                old?.Dispose();
                StatusChanged?.Invoke("客户端连接中...");
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { StatusChanged?.Invoke($"监听失败: {ex.Message}"); }
    }

    private void OnMessage(KvmConnection connection, byte type, byte[] payload)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            if (type == Protocol.Hello)
            {
                var token = reader.ReadString();
                var name = reader.ReadString();
                if (!CryptographicEquals(token, _config.AuthToken))
                {
                    StatusChanged?.Invoke("客户端令牌不匹配");
                    connection.Close();
                    return;
                }
                _authorized = 1;
                StatusChanged?.Invoke($"已连接: {name}");
                ConnectionChanged?.Invoke(true);
                connection.Send(Protocol.Frame(Protocol.Route, writer => writer.Write((byte)ParseTarget(_config.LastTarget))));
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"客户端消息错误: {ex.Message}");
        }
    }

    private void OnClosed(KvmConnection connection)
    {
        if (ReferenceEquals(_client, connection))
        {
            _authorized = 0;
            ConnectionChanged?.Invoke(false);
            StatusChanged?.Invoke("客户端已断开");
        }
    }

    public void Send(byte[] frame) => Client?.Send(frame);

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _discovery?.Dispose();
        var client = _client;
        _client = null;
        client?.Dispose();
        _cts.Dispose();
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static KvmTarget ParseTarget(string target) =>
        target.Equals("laptop", StringComparison.OrdinalIgnoreCase) ? KvmTarget.Laptop : KvmTarget.Main;
}

internal sealed class KvmClient : IDisposable
{
    private readonly KvmConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private KvmConnection? _connection;

    public event Action<bool>? ConnectionChanged;
    public event Action<string>? StatusChanged;
    public bool IsConnected => _connection != null;

    public KvmClient(KvmConfig config) => _config = config;

    public void Start() => _ = Task.Run(ConnectLoop);

    public void Send(byte[] frame) => _connection?.Send(frame);

    private async Task ConnectLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_config.AutoDiscover || string.IsNullOrWhiteSpace(_config.ServerAddress) || _config.Port <= 0 || string.IsNullOrWhiteSpace(_config.AuthToken))
                {
                    StatusChanged?.Invoke("正在自动发现主机...");
                    var endpoint = await KvmDiscovery.FindHostAsync(_config.ServerAddress, _cts.Token).ConfigureAwait(false);
                    if (endpoint == null)
                    {
                        StatusChanged?.Invoke("未发现主机，稍后重试");
                        await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    _config.ServerAddress = endpoint.Address;
                    _config.Port = endpoint.Port;
                    _config.ControlPort = endpoint.ControlPort;
                    _config.AuthToken = endpoint.Token;
                    _config.Save();
                    StatusChanged?.Invoke($"发现主机 {endpoint.Name} ({endpoint.Address})");
                }

                using var tcp = new TcpClient { NoDelay = true };
                StatusChanged?.Invoke($"连接主机 {_config.ServerAddress}:{_config.Port}...");
                await tcp.ConnectAsync(_config.ServerAddress, _config.Port, _cts.Token).ConfigureAwait(false);
                var connection = new KvmConnection(tcp);
                _connection = connection;
                connection.MessageReceived += OnMessage;
                connection.Closed += OnClosed;
                connection.Start();
                connection.Send(Protocol.Frame(Protocol.Hello, writer =>
                {
                    writer.Write(_config.AuthToken);
                    writer.Write(_config.DisplayName);
                }));
                ConnectionChanged?.Invoke(true);
                StatusChanged?.Invoke("已连接主机");
                while (!_cts.IsCancellationRequested && ReferenceEquals(_connection, connection))
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { StatusChanged?.Invoke($"连接失败: {ex.Message}"); }

            ConnectionChanged?.Invoke(false);
            await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
        }
    }

    private void OnMessage(KvmConnection connection, byte type, byte[] payload)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            switch (type)
            {
                case Protocol.Key:
                    InputInjector.Key(reader.ReadUInt16(), reader.ReadBoolean());
                    break;
                case Protocol.MouseMove:
                    InputInjector.Move(reader.ReadInt32(), reader.ReadInt32());
                    break;
                case Protocol.MouseButton:
                    InputInjector.Button(reader.ReadByte(), reader.ReadBoolean());
                    break;
                case Protocol.MouseWheel:
                    InputInjector.Wheel(reader.ReadInt32(), reader.ReadInt32());
                    break;
                case Protocol.SetInput:
                    var input = reader.ReadByte();
                    _ = Task.Run(() =>
                    {
                        var ok = DdcController.SetInput(
                            input,
                            _config.BrightnessConfigured && _config.SyncBrightness,
                            _config.BrightnessPercent,
                            _config.BrightnessDelayMs,
                            out var error);
                        connection.Send(Protocol.Frame(Protocol.Ack, writer =>
                        {
                            writer.Write(Protocol.SetInput);
                            writer.Write(ok);
                            writer.Write(error ?? string.Empty);
                        }));
                    });
                    break;
                case Protocol.Route:
                    StatusChanged?.Invoke(reader.ReadByte() == (byte)KvmTarget.Laptop ? "当前路由: 笔记本" : "当前路由: 主机");
                    break;
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke($"输入处理失败: {ex.Message}"); }
    }

    private void OnClosed(KvmConnection connection)
    {
        if (ReferenceEquals(_connection, connection))
        {
            _connection = null;
            ConnectionChanged?.Invoke(false);
            StatusChanged?.Invoke("主机连接断开");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
        _cts.Dispose();
    }
}

internal sealed class InputCapture : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyUp = 0x0101;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const uint LlkhfInjected = 0x10;
    private const uint LlkhfUp = 0x80;

    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VkCode; public uint ScanCode; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MsLlHookStruct { public Point Point; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly Func<bool> _shouldForward;
    private readonly Func<ushort, bool> _isHotkeyKey;
    private readonly Action<ushort, bool> _onKey;
    private readonly Action<int, int> _onMove;
    private readonly Action<byte, bool> _onButton;
    private readonly Action<int, int> _onWheel;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Point _lastPoint;
    private bool _hasPoint;
    private int _disposed;

    public InputCapture(Func<bool> shouldForward, Func<ushort, bool> isHotkeyKey, Action<ushort, bool> onKey, Action<int, int> onMove, Action<byte, bool> onButton, Action<int, int> onWheel)
    {
        _shouldForward = shouldForward;
        _isHotkeyKey = isHotkeyKey;
        _onKey = onKey;
        _onMove = onMove;
        _onButton = onButton;
        _onWheel = onWheel;
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public void Install()
    {
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            throw new InvalidOperationException($"无法安装全局输入钩子，错误码 {Marshal.GetLastWin32Error()}");
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var up = (data.Flags & LlkhfUp) != 0 || wParam == (IntPtr)WmKeyUp;
            if (_shouldForward() && !_isHotkeyKey((ushort)data.VkCode))
            {
                _onKey((ushort)data.VkCode, up);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            if ((data.Flags & LlkhfInjected) != 0)
                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

            if (!_hasPoint) { _lastPoint = data.Point; _hasPoint = true; }
            if (_shouldForward())
            {
                var message = wParam.ToInt32();
                if (message == WmMouseMove)
                {
                    // Suppressed events do not move the host cursor, so keep the switch-time point as the anchor.
                    var dx = data.Point.X - _lastPoint.X;
                    var dy = data.Point.Y - _lastPoint.Y;
                    if (dx != 0 || dy != 0) _onMove(dx, dy);
                }
                else if (message is WmLButtonDown or WmLButtonUp) _onButton(1, message == WmLButtonDown);
                else if (message is WmRButtonDown or WmRButtonUp) _onButton(2, message == WmRButtonDown);
                else if (message is WmMButtonDown or WmMButtonUp) _onButton(3, message == WmMButtonDown);
                else if (message is WmXButtonDown or WmXButtonUp) _onButton((byte)(3 + ((data.MouseData >> 16) & 0xffff)), message == WmXButtonDown);
                else if (message == WmMouseWheel) _onWheel(0, unchecked((short)(data.MouseData >> 16)));
                else if (message == WmMouseHWheel) _onWheel(unchecked((short)(data.MouseData >> 16)), 0);
                return (IntPtr)1;
            }
            _lastPoint = data.Point;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void ResetPointerAnchor()
    {
        if (GetCursorPos(out var point))
        {
            _lastPoint = point;
            _hasPoint = true;
        }
    }

    public void Dispose()
    {
        if (_disposed != 0) return;
        _disposed = 1;
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }
}

internal static class InputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHWheel = 0x01000;
    private const uint KeyUp = 0x0002;
    private const uint ExtendedKey = 0x0001;

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    private const uint WmCancelMode = 0x001F;
    private const int SwHide = 0;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    public static void Key(ushort vk, bool up)
    {
        var input = KeyboardEvent(vk, up);
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    internal static uint KeyFlags(ushort vk, bool up) =>
        (up ? KeyUp : 0) | (vk is 0xA3 or 0xA5 or 0x5B or 0x5C ? ExtendedKey : 0);

    private static Input KeyboardEvent(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { Vk = vk, Flags = KeyFlags(vk, up) } }
    };

    public static void TapRouteKey(ushort vk)
    {
        var events = new[] { KeyboardEvent(vk, false), KeyboardEvent(vk, true) };
        if (SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) != events.Length)
            throw new InvalidOperationException("无法向 Deskflow 发送切换键，请检查程序权限");
    }

    public static void Move(int dx, int dy) => SendMouse(dx, dy, 0, MouseMove);

    public static void Button(byte button, bool down)
    {
        uint flags = button switch
        {
            1 => down ? MouseLeftDown : MouseLeftUp,
            2 => down ? MouseRightDown : MouseRightUp,
            3 => down ? MouseMiddleDown : MouseMiddleUp,
            4 => down ? MouseXDown : MouseXUp,
            5 => down ? MouseXDown : MouseXUp,
            _ => 0
        };
        var data = button >= 4 ? (uint)(button - 3) : 0;
        if (flags != 0) SendMouse(0, 0, data, flags);
    }

    public static void Wheel(int horizontal, int vertical)
    {
        if (vertical != 0) SendMouse(0, 0, unchecked((uint)vertical), MouseWheel);
        if (horizontal != 0) SendMouse(0, 0, unchecked((uint)horizontal), MouseHWheel);
    }

    public static void ReleaseTransientState()
    {
        // A route hotkey can complete while Windows is still processing input
        // from the previous desktop. Key/button-up events are idempotent and
        // prevent the first remote click from inheriting a stale drag or Alt state.
        Button(1, false);
        Button(2, false);
        Button(3, false);
        Button(4, false);
        Button(5, false);
        var events = new ushort[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }
            .Select(key => KeyboardEvent(key, true)).ToArray();
        SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
    }

    public static void WakeDisplayAndInput()
    {
        // Reset Windows' idle timers without creating a persistent power request.
        SetThreadExecutionState(EsSystemRequired | EsDisplayRequired);
    }

    public static void CancelDeskflowMouseCapture(int processId)
    {
        if (processId <= 0) return;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != (uint)processId) return true;

            var className = new StringBuilder(64);
            if (GetClassName(window, className, className.Capacity) > 0 &&
                className.ToString().Equals("DeskflowDesk", StringComparison.Ordinal))
            {
                PostMessage(window, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
                ShowWindowAsync(window, SwHide);
            }
            return true;
        }, IntPtr.Zero);
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }
}

internal static class DdcController
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr monitor, byte code, out uint version, out uint currentValue, out uint maximumValue);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);

    private const byte BrightnessVcp = 0x10;
    private const byte InputVcp = 0x60;

    public static bool ReadBrightness(out string result)
    {
        var readings = new StringBuilder();
        var failures = new StringBuilder();
        var success = 0;
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count))
            {
                failures.Append($"枚举物理显示器失败({Marshal.GetLastWin32Error()}); ");
                return true;
            }

            var physical = new PhysicalMonitor[count];
            try
            {
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
                {
                    failures.Append($"读取物理显示器失败({Marshal.GetLastWin32Error()}); ");
                    return true;
                }

                foreach (var item in physical)
                {
                    if (GetVCPFeatureAndVCPFeatureReply(
                            item.Handle, BrightnessVcp, out _, out var current, out var maximum))
                    {
                        if (readings.Length > 0) readings.Append("; ");
                        readings.Append($"{item.Description}: current={current}, max={maximum}");
                        success++;
                    }
                    else
                    {
                        failures.Append($"{item.Description}: 读取亮度失败({Marshal.GetLastWin32Error()}); ");
                    }
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        result = success > 0 ? readings.ToString() : failures.ToString().TrimEnd();
        return success > 0;
    }

    public static bool SetInput(
        byte input,
        bool syncBrightness,
        int brightnessPercent,
        int brightnessDelayMs,
        out string error)
    {
        var success = 0;
        var brightnessSuccess = 0;
        var failures = new StringBuilder();
        brightnessPercent = Math.Clamp(brightnessPercent, 0, 100);
        brightnessDelayMs = Math.Clamp(brightnessDelayMs, 50, 2000);
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count))
            {
                failures.Append($"枚举物理显示器失败({Marshal.GetLastWin32Error()}); ");
                return true;
            }
            var physical = new PhysicalMonitor[count];
            try
            {
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
                {
                    failures.Append($"读取物理显示器失败({Marshal.GetLastWin32Error()}); ");
                    return true;
                }
                foreach (var item in physical)
                {
                    uint brightnessValue = (uint)brightnessPercent;
                    if (syncBrightness && GetVCPFeatureAndVCPFeatureReply(
                            item.Handle, BrightnessVcp, out _, out _, out var maximumBrightness) &&
                        maximumBrightness > 0)
                    {
                        brightnessValue = (uint)Math.Round(brightnessPercent * maximumBrightness / 100d);
                    }

                    if (!SetVCPFeature(item.Handle, InputVcp, input))
                    {
                        failures.Append($"{item.Description}: DDC 输入切换错误({Marshal.GetLastWin32Error()}); ");
                        continue;
                    }

                    success++;
                    if (syncBrightness)
                    {
                        // The monitor may apply a per-input profile after VCP 0x60.
                        // Give it a moment, then apply one configured percentage
                        // to both DP1 and HDMI1.
                        Thread.Sleep(brightnessDelayMs);
                        if (SetVCPFeature(item.Handle, BrightnessVcp, brightnessValue))
                            brightnessSuccess++;
                        else
                            failures.Append($"{item.Description}: 亮度同步失败({Marshal.GetLastWin32Error()}); ");
                    }
                }
            }
            finally { DestroyPhysicalMonitors(count, physical); }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        error = failures.Length == 0 ? string.Empty : failures.ToString();
        return success > 0 && (!syncBrightness || brightnessSuccess > 0);
    }
}

internal sealed class HotkeyForm : Form
{
    private HotkeyBinding _binding;

    public HotkeyForm(HotkeyBinding binding)
    {
        _binding = binding;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Width = 1;
        Height = 1;
        Text = "one-for-all-switch";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
    }

    public void SetHotkey(HotkeyBinding binding)
    {
        _binding = binding;
    }
}

internal sealed class HotkeyDialog : Form
{
    private readonly TextBox _capture;
    private HotkeyBinding _binding;

    private HotkeyDialog(HotkeyBinding current)
    {
        _binding = current;
        Text = "设置切换热键";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(390, 150);

        var label = new Label
        {
            AutoSize = true,
            Location = new Point(18, 18),
            Text = "点击输入框后按下组合键："
        };
        _capture = new TextBox
        {
            ReadOnly = true,
            Location = new Point(18, 48),
            Width = 354,
            Text = _binding.ToString(),
            TabStop = true
        };
        _capture.KeyDown += CaptureKeyDown;
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(216, 98), Width = 74 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(298, 98), Width = 74 };
        Controls.AddRange(new Control[] { label, _capture, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public static HotkeyBinding? Choose(IWin32Window owner, HotkeyBinding current)
    {
        using var dialog = new HotkeyDialog(current);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._binding : null;
    }

    private void CaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
        {
            e.SuppressKeyPress = true;
            return;
        }

        _binding = HotkeyBinding.FromKeyEvent(e);
        _capture.Text = _binding.ToString();
        e.SuppressKeyPress = true;
        e.Handled = true;
    }
}

internal static class NetworkInfo
{
    public static string LocalIpv4Text()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address))
                .Select(item => item.Address.ToString())
                .Distinct()
                .ToArray();
            return addresses.Length == 0 ? "未检测到局域网 IPv4" : string.Join(" / ", addresses);
        }
        catch
        {
            return "未检测到局域网 IPv4";
        }
    }
}

internal static class FirewallHelper
{
    private const string TcpRuleName = "KvmSwitch Deskflow TCP";
    private const string ControlRuleName = "KvmSwitch Control TCP";
    private const string DiscoveryRuleName = "KvmSwitch Discovery UDP";

    public static bool EnsureRules(string corePath, out string error)
    {
        var appPath = Environment.ProcessPath ?? Application.ExecutablePath;
        static string Quote(string value) => $"'{value.Replace("'", "''")}'";
        var command = string.Join("; ", new[]
        {
            $"Get-NetFirewallRule -DisplayName {Quote(TcpRuleName)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            $"Get-NetFirewallRule -DisplayName {Quote(ControlRuleName)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            $"Get-NetFirewallRule -DisplayName {Quote(DiscoveryRuleName)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            $"New-NetFirewallRule -DisplayName {Quote(TcpRuleName)} -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 24800 -Program {Quote(corePath)} | Out-Null",
            $"New-NetFirewallRule -DisplayName {Quote(ControlRuleName)} -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 24801 -Program {Quote(appPath)} | Out-Null",
            $"New-NetFirewallRule -DisplayName {Quote(DiscoveryRuleName)} -Direction Inbound -Action Allow -Profile Any -Protocol UDP -LocalPort {KvmDiscovery.DiscoveryPort} -Program {Quote(appPath)} | Out-Null"
        });

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(command);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动防火墙配置程序");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                error = $"防火墙配置程序退出码 {process.ExitCode}";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

internal sealed class SetupForm : Form
{
    private sealed class InputChoice
    {
        public string Name { get; }
        public byte Value { get; }

        public InputChoice(string name, byte value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => $"{Name} (0x{Value:X2})";
    }

    private readonly KvmConfig _config;
    private readonly ComboBox _role;
    private readonly TextBox _address;
    private readonly ComboBox _mainInput;
    private readonly ComboBox _laptopInput;
    private readonly Button _hotkey;
    private readonly CheckBox _syncBrightness;
    private readonly NumericUpDown _brightness;
    private readonly NumericUpDown _brightnessDelay;
    private readonly NumericUpDown _ddcRetries;
    private readonly CheckBox _autoStart;
    private readonly CheckBox _keepClientAwake;
    private readonly CheckBox _elevateClient;
    private readonly Label _hint;
    private HotkeyBinding _binding;

    public SetupForm(KvmConfig config)
    {
        _config = config;
        _binding = HotkeyBinding.Parse(config.Hotkey);
        Text = "one-for-all-switch 配置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(560, 625);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(20, 18),
            Text = "选择这台电脑在 one-for-all-switch 中的角色"
        };
        var roleLabel = new Label { AutoSize = true, Location = new Point(20, 58), Text = "角色" };
        _role = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(120, 54),
            Width = 350
        };
        _role.Items.AddRange(new object[] { "主机（键盘鼠标连接在此电脑）", "客户端（接收主机键鼠）" });
        _role.SelectedIndex = config.IsClient ? 1 : 0;
        _role.SelectedIndexChanged += (_, _) => UpdateRoleState();

        var addressLabel = new Label { AutoSize = true, Location = new Point(20, 96), Text = "主机地址" };
        _address = new TextBox
        {
            Location = new Point(120, 92),
            Width = 350,
            Text = config.ServerAddress,
            PlaceholderText = "留空即可自动发现主机"
        };
        var addressTip = new Label
        {
            AutoSize = false,
            Location = new Point(120, 122),
            Size = new Size(350, 32),
            Text = "客户端留空时自动发现主机；广播受限时可填写主机 IPv4。"
        };

        var mainInputLabel = new Label { AutoSize = true, Location = new Point(20, 164), Text = "主机输入源" };
        _mainInput = CreateInputCombo(120, 160);
        SelectInput(_mainInput, config.MainInput);
        var laptopInputLabel = new Label { AutoSize = true, Location = new Point(20, 202), Text = "笔记本输入源" };
        _laptopInput = CreateInputCombo(120, 198);
        SelectInput(_laptopInput, config.LaptopInput);

        var hotkeyLabel = new Label { AutoSize = true, Location = new Point(20, 240), Text = "切换热键" };
        _hotkey = new Button { Location = new Point(120, 236), Width = 170, Text = _binding.ToString() };
        _hotkey.Click += (_, _) =>
        {
            var selected = HotkeyDialog.Choose(this, _binding);
            if (selected != null)
            {
                _binding = selected;
                _hotkey.Text = selected.ToString();
            }
        };

        _syncBrightness = new CheckBox
        {
            AutoSize = true,
            Location = new Point(120, 276),
            Text = "切换输入源后统一亮度",
            Checked = config.BrightnessConfigured && config.SyncBrightness
        };
        var brightnessLabel = new Label { AutoSize = true, Location = new Point(20, 316), Text = "统一亮度" };
        _brightness = new NumericUpDown
        {
            Location = new Point(120, 312),
            Width = 80,
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(config.BrightnessPercent, 0, 100)
        };
        var brightnessUnit = new Label { AutoSize = true, Location = new Point(205, 316), Text = "%" };
        var delayLabel = new Label { AutoSize = true, Location = new Point(260, 316), Text = "切源等待" };
        _brightnessDelay = new NumericUpDown
        {
            Location = new Point(350, 312),
            Width = 90,
            Minimum = 50,
            Maximum = 2000,
            Increment = 50,
            Value = Math.Clamp(config.BrightnessDelayMs, 50, 2000)
        };
        var delayUnit = new Label { AutoSize = true, Location = new Point(445, 316), Text = "ms" };
        var retryLabel = new Label { AutoSize = true, Location = new Point(20, 354), Text = "DDC 重试" };
        _ddcRetries = new NumericUpDown
        {
            Location = new Point(120, 350),
            Width = 80,
            Minimum = 1,
            Maximum = 8,
            Value = Math.Clamp(config.DdcRetryCount, 1, 8)
        };
        var retryUnit = new Label { AutoSize = true, Location = new Point(205, 354), Text = "次" };
        void UpdateBrightnessControls()
        {
            _brightness.Enabled = _syncBrightness.Checked;
            _brightnessDelay.Enabled = _syncBrightness.Checked;
        }
        _syncBrightness.CheckedChanged += (_, _) => UpdateBrightnessControls();
        _autoStart = new CheckBox
        {
            AutoSize = true,
            Location = new Point(120, 388),
            Text = "Windows 登录后自动启动并读取此配置",
            Checked = config.AutoStart
        };
        _keepClientAwake = new CheckBox
        {
            AutoSize = true,
            Location = new Point(120, 420),
            Text = "客户端运行时保持唤醒（防止长时间空闲断开）",
            Checked = config.KeepClientAwake
        };
        _elevateClient = new CheckBox
        {
            AutoSize = true,
            Location = new Point(120, 452),
            Text = "兼容管理员悬浮窗（客户端请求管理员权限）",
            Checked = config.ElevateClient
        };
        _hint = new Label
        {
            AutoSize = false,
            Location = new Point(20, 490),
            Size = new Size(520, 48)
        };

        var ok = new Button { Text = "保存并启动", DialogResult = DialogResult.OK, Location = new Point(354, 576), Width = 100 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(460, 576), Width = 70 };
        ok.Click += (_, _) => Apply();
        Controls.AddRange(new Control[]
        {
            title, roleLabel, _role, addressLabel, _address, addressTip,
            mainInputLabel, _mainInput, laptopInputLabel, _laptopInput,
            hotkeyLabel, _hotkey, _syncBrightness,
            brightnessLabel, _brightness, brightnessUnit,
            delayLabel, _brightnessDelay, delayUnit,
            retryLabel, _ddcRetries, retryUnit,
            _autoStart, _keepClientAwake, _elevateClient, _hint, ok, cancel
        });
        AcceptButton = ok;
        CancelButton = cancel;
        UpdateBrightnessControls();
        UpdateRoleState();
    }

    private void UpdateRoleState()
    {
        var client = _role.SelectedIndex == 1;
        _address.Enabled = client;
        _keepClientAwake.Enabled = client;
        _elevateClient.Enabled = client;
        _hint.Text = client
            ? "填写主机 IPv4 可跳过广播发现；留空时使用局域网自动发现。"
            : $"本机 IPv4: {NetworkInfo.LocalIpv4Text()}；Deskflow 使用 TCP 24800。";
    }

    private void Apply()
    {
        var client = _role.SelectedIndex == 1;
        var wasHost = _config.IsHost;
        _config.ConfigVersion = 2;
        _config.Role = client ? "client" : "host";
        _config.DisplayName = client ? "laptop" : "main";
        _config.ServerAddress = client ? _address.Text.Trim() : string.Empty;
        _config.Port = 24800;
        _config.ControlPort = 24801;
        _config.AutoPort = false;
        _config.AutoDiscover = client && string.IsNullOrWhiteSpace(_config.ServerAddress);
        _config.MainInput = ((InputChoice)_mainInput.SelectedItem!).Value;
        _config.LaptopInput = ((InputChoice)_laptopInput.SelectedItem!).Value;
        _config.BrightnessConfigured = true;
        _config.SyncBrightness = _syncBrightness.Checked;
        _config.BrightnessPercent = (int)_brightness.Value;
        _config.BrightnessDelayMs = (int)_brightnessDelay.Value;
        _config.DdcRetryCount = (int)_ddcRetries.Value;
        _config.AutoStart = _autoStart.Checked;
        _config.KeepClientAwake = _keepClientAwake.Checked;
        _config.ElevateClient = _elevateClient.Checked;
        if (client)
            _config.AuthToken = string.Empty;
        else if (!wasHost || string.IsNullOrWhiteSpace(_config.AuthToken))
            _config.AuthToken = KvmConfig.CreateToken();
        _config.Hotkey = _binding.ToString();
        _config.Save();

        if (!client)
        {
            try
            {
                var corePath = DeskflowBackend.FindCorePath();
                if (!string.Equals(_config.FirewallRuntimePath, corePath, StringComparison.OrdinalIgnoreCase) || !_config.FirewallControlConfigured)
                {
                    if (FirewallHelper.EnsureRules(corePath, out var firewallError))
                    {
                        _config.FirewallRuntimePath = corePath;
                        _config.FirewallControlConfigured = true;
                        _config.Save();
                    }
                    else
                    {
                        MessageBox.Show(
                            $"主机防火墙规则未配置，笔记本可能无法连接。\n{firewallError}",
                            "one-for-all-switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"找不到 Deskflow 或无法配置防火墙。请将 Deskflow 文件夹放在 one-for-all-switch.exe 旁边。\n{ex.Message}",
                    "one-for-all-switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private static ComboBox CreateInputCombo(int x, int y)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(x, y),
            Width = 350
        };
        combo.Items.AddRange(new object[]
        {
            new InputChoice("DP1", 0x0F),
            new InputChoice("DP2", 0x10),
            new InputChoice("HDMI1", 0x11),
            new InputChoice("HDMI2", 0x12)
        });
        return combo;
    }

    private static void SelectInput(ComboBox combo, byte value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is InputChoice choice && choice.Value == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }
}

internal static class KvmControl
{
    private const int Port = 37997;

    public static bool TryNotifyRoute(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (!args[i].Equals("--notify-route", StringComparison.OrdinalIgnoreCase)) continue;
            var target = args[i + 1].Equals("laptop", StringComparison.OrdinalIgnoreCase)
                ? KvmTarget.Laptop
                : KvmTarget.Main;
            using var udp = new UdpClient();
            var payload = Encoding.UTF8.GetBytes(target == KvmTarget.Laptop ? "laptop" : "main");
            udp.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, Port));
            return true;
        }
        return false;
    }
}

internal sealed class KvmControlListener : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 37997));

    public event Action<KvmTarget>? RouteReceived;

    public void Start() => _ = Task.Run(Loop);

    private async Task Loop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var packet = await _socket.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                var value = Encoding.UTF8.GetString(packet.Buffer);
                if (value.Equals("laptop", StringComparison.OrdinalIgnoreCase))
                    RouteReceived?.Invoke(KvmTarget.Laptop);
                else if (value.Equals("main", StringComparison.OrdinalIgnoreCase))
                    RouteReceived?.Invoke(KvmTarget.Main);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }
}

// Route commands deliberately use a small authenticated channel outside of
// Deskflow. This keeps the tray menu usable while Deskflow is reconnecting.
internal sealed class RouteControlHost : IDisposable
{
    private readonly KvmConfig _config;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private KvmConnection? _connection;
    private int _authorized;
    private long _nextWakeId;
    private long _pendingWakeId;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private KvmConnection? _authorizedConnection;
    private int _publishedTarget;

    public event Action<KvmTarget>? RouteRequested;
    public event Action<string>? StatusChanged;

    public RouteControlHost(KvmConfig config)
    {
        _config = config;
        _listener = new TcpListener(IPAddress.Any, config.ControlPort);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
        StatusChanged?.Invoke($"控制通道已启动: {_config.ControlPort}");
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                TcpConnectionSettings.EnableKeepAlive(tcp);
                tcp.SendTimeout = 3000;
                var connection = new KvmConnection(tcp);
                connection.MessageReceived += OnMessage;
                connection.Closed += OnClosed;
                var old = Interlocked.Exchange(ref _connection, connection);
                Volatile.Write(ref _authorizedConnection, null);
                Volatile.Write(ref _authorized, 0);
                old?.Dispose();
                connection.Start();
                StatusChanged?.Invoke("客户端控制连接中...");
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { StatusChanged?.Invoke($"控制通道监听失败: {ex.Message}"); }
    }

    private void OnMessage(KvmConnection connection, byte type, byte[] payload)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            if (type == Protocol.Hello)
            {
                var token = reader.ReadString();
                var name = reader.ReadString();
                if (!CryptographicEquals(token, _config.AuthToken))
                {
                    StatusChanged?.Invoke("客户端控制令牌不匹配");
                    connection.Close();
                    return;
                }

                if (!ReferenceEquals(Volatile.Read(ref _connection), connection)) return;
                Volatile.Write(ref _authorized, 1);
                Volatile.Write(ref _authorizedConnection, connection);
                connection.Send(Protocol.Frame(Protocol.Ack, writer =>
                {
                    writer.Write(Protocol.Hello);
                    writer.Write(_sessionId);
                    writer.Write(_config.Port);
                }));
                StatusChanged?.Invoke($"控制连接已建立: {name}");
                connection.Send(Protocol.Frame(Protocol.Route, writer => writer.Write((byte)Volatile.Read(ref _publishedTarget))));
                TrySendPendingWake(connection);
                return;
            }

            if (!ReferenceEquals(Volatile.Read(ref _authorizedConnection), connection) || !ReferenceEquals(_connection, connection))
                return;

            if (type == Protocol.Route)
            {
                RouteRequested?.Invoke(reader.ReadByte() == (byte)KvmTarget.Laptop ? KvmTarget.Laptop : KvmTarget.Main);
            }
            else if (type == Protocol.Ack && reader.ReadByte() == Protocol.Wake)
            {
                var wakeId = reader.ReadInt64();
                Interlocked.CompareExchange(ref _pendingWakeId, 0, wakeId);
            }
            else if (type == Protocol.Ping)
                connection.Send(Protocol.Frame(Protocol.Ping));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"控制消息错误: {ex.Message}");
            connection.Close();
        }
    }

    private void OnClosed(KvmConnection connection)
    {
        if (ReferenceEquals(_connection, connection))
        {
            Volatile.Write(ref _authorized, 0);
            Interlocked.CompareExchange(ref _authorizedConnection, null, connection);
            StatusChanged?.Invoke("客户端控制连接断开");
        }
    }

    public void RequestInputRecovery()
    {
        var wakeId = Interlocked.Increment(ref _nextWakeId);
        Volatile.Write(ref _pendingWakeId, wakeId);
        TrySendPendingWake();
        _ = Task.Run(() => RetryPendingWake(wakeId));
    }

    public void PublishTarget(KvmTarget target)
    {
        Volatile.Write(ref _publishedTarget, (int)target);
        if (target == KvmTarget.Main)
            Interlocked.Exchange(ref _pendingWakeId, 0);
        var connection = Volatile.Read(ref _authorizedConnection);
        if (connection != null && ReferenceEquals(Volatile.Read(ref _connection), connection))
            connection.Send(Protocol.Frame(Protocol.Route, writer => writer.Write((byte)target)));
        if (target == KvmTarget.Laptop) RequestInputRecovery();
    }

    private async Task RetryPendingWake(long wakeId)
    {
        try
        {
            foreach (var delay in new[] { 500, 1000, 2000, 3000 })
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _pendingWakeId) != wakeId) return;
                TrySendPendingWake();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void TrySendPendingWake(KvmConnection? expectedConnection = null)
    {
        if (Volatile.Read(ref _authorized) == 0) return;
        var connection = Volatile.Read(ref _connection);
        if (!ReferenceEquals(connection, Volatile.Read(ref _authorizedConnection))) return;
        if (connection == null || (expectedConnection != null && !ReferenceEquals(connection, expectedConnection))) return;
        var wakeId = Volatile.Read(ref _pendingWakeId);
        if (wakeId == 0) return;
        connection.Send(Protocol.Frame(Protocol.Wake, writer => writer.Write(wakeId)));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        var connection = Interlocked.Exchange(ref _connection, null);
        connection?.Dispose();
        _cts.Dispose();
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}

internal sealed class RouteControlClient : IDisposable
{
    private readonly KvmConfig _config;
    private readonly Func<string?, CancellationToken, Task<KvmDiscoveryEndpoint?>> _discover;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sendLock = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _loopActive;
    private int _pendingTarget = -1;

    public event Action? InputRecoveryRequested;
    public event Action<bool>? ConnectionChanged;
    public event Action<KvmTarget>? TargetPublished;
    public event Action<string>? StatusChanged;

    public RouteControlClient(KvmConfig config, Func<string?, CancellationToken, Task<KvmDiscoveryEndpoint?>>? discover = null)
    {
        _config = config;
        _discover = discover ?? KvmDiscovery.FindHostAsync;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _loopActive, 1) == 0)
            _ = Task.Run(ConnectLoop);
    }

    public void SendTarget(KvmTarget target)
    {
        var frame = Protocol.Frame(Protocol.Route, writer => writer.Write((byte)target));
        lock (_sendLock)
        {
            if (_stream == null)
            {
                Volatile.Write(ref _pendingTarget, (int)target);
                StatusChanged?.Invoke("控制通道正在重连，切换命令已排队");
                return;
            }

            try
            {
                SendFrameLocked(frame);
                Interlocked.Exchange(ref _pendingTarget, -1);
                StatusChanged?.Invoke(target == KvmTarget.Main ? "已请求切换到主机" : "已请求切换到笔记本");
            }
            catch
            {
                Volatile.Write(ref _pendingTarget, (int)target);
                try { _tcp?.Close(); } catch { }
                StatusChanged?.Invoke("控制通道中断，切换命令已排队");
            }
        }
    }

    private async Task ConnectLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var authenticated = false;
                try
                {
                    // Refresh pairing on every reconnect, including a manually entered
                    // address whose server has restarted with a new token.
                    var endpoint = await _discover(_config.ServerAddress, _cts.Token).ConfigureAwait(false);
                    if (endpoint != null)
                    {
                        _config.ServerAddress = endpoint.Address;
                        _config.Port = endpoint.Port;
                        _config.ControlPort = endpoint.ControlPort;
                        _config.AuthToken = endpoint.Token;
                        _config.Save();
                    }
                    if (string.IsNullOrWhiteSpace(_config.ServerAddress) || string.IsNullOrWhiteSpace(_config.AuthToken))
                        throw new IOException("尚未发现可配对的主机");

                    using var tcp = new TcpClient { NoDelay = true, SendTimeout = 3000 };
                    TcpConnectionSettings.EnableKeepAlive(tcp);
                    StatusChanged?.Invoke($"连接控制通道 {_config.ServerAddress}:{_config.ControlPort}...");
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    await tcp.ConnectAsync(_config.ServerAddress, _config.ControlPort, connectTimeout.Token).ConfigureAwait(false);
                    var stream = tcp.GetStream();
                    var hello = Protocol.Frame(Protocol.Hello, writer =>
                    {
                        writer.Write(_config.AuthToken);
                        writer.Write(_config.DisplayName);
                    });
                    await stream.WriteAsync(hello, connectTimeout.Token).ConfigureAwait(false);
                    var (helloType, helloPayload) = await ReadFrameAsync(stream, connectTimeout.Token).ConfigureAwait(false);
                    using (var reader = new BinaryReader(new MemoryStream(helloPayload), Encoding.UTF8))
                    {
                        if (helloType != Protocol.Ack || reader.ReadByte() != Protocol.Hello)
                            throw new IOException("主机未确认配对，请确保两端均已更新");
                        _ = reader.ReadString(); // Each successful handshake starts a fresh input session.
                        _config.Port = reader.ReadInt32();
                        if (_config.Port is < 1024 or > 65535) throw new InvalidDataException("Invalid Deskflow port");
                    }
                    _config.Save();
                    lock (_sendLock)
                    {
                        _tcp = tcp;
                        _stream = stream;
                        var pending = Volatile.Read(ref _pendingTarget);
                        if (pending >= 0)
                        {
                            SendFrameLocked(Protocol.Frame(Protocol.Route, writer => writer.Write((byte)pending)));
                            Interlocked.CompareExchange(ref _pendingTarget, -1, pending);
                        }
                    }
                    authenticated = true;
                    ConnectionChanged?.Invoke(true);
                    StatusChanged?.Invoke("主机控制通道已连接");

                    using var session = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    var heartbeat = SendHeartbeatAsync(stream, session.Token);
                    long lastWakeId = 0;
                    try
                    {
                        while (!session.IsCancellationRequested)
                        {
                            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                            var (type, payload) = await ReadFrameAsync(stream, readTimeout.Token).ConfigureAwait(false);
                            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
                            if (type == Protocol.Route)
                            {
                                var target = reader.ReadByte();
                                if (target > 1) throw new InvalidDataException("Invalid route target");
                                TargetPublished?.Invoke((KvmTarget)target);
                                continue;
                            }
                            if (type != Protocol.Wake) continue;
                            var wakeId = reader.ReadInt64();
                            if (wakeId > lastWakeId)
                            {
                                lastWakeId = wakeId;
                                InputRecoveryRequested?.Invoke();
                            }
                            lock (_sendLock)
                            {
                                SendFrameLocked(Protocol.Frame(Protocol.Ack, ack =>
                                {
                                    ack.Write(Protocol.Wake);
                                    ack.Write(wakeId);
                                }));
                            }
                        }
                    }
                    finally
                    {
                        session.Cancel();
                        await heartbeat.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch (Exception ex) { StatusChanged?.Invoke($"控制通道连接失败: {ex.Message}"); }
                finally
                {
                    lock (_sendLock)
                    {
                        _stream = null;
                        _tcp = null;
                    }
                    if (authenticated) ConnectionChanged?.Invoke(false);
                }

                if (!_cts.IsCancellationRequested)
                    await Task.Delay(1500, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { Volatile.Write(ref _loopActive, 0); }
    }

    private void SendFrameLocked(byte[] frame) => _stream!.Write(frame, 0, frame.Length);

    private async Task SendHeartbeatAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                lock (_sendLock)
                {
                    if (!ReferenceEquals(_stream, stream)) return;
                    SendFrameLocked(Protocol.Frame(Protocol.Ping));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { stream.Close(); }
        catch (ObjectDisposedException) { }
    }

    private static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length < 1 || length > 1024 * 1024)
            throw new InvalidDataException("Invalid control frame length");

        var frame = new byte[length];
        await ReadExactAsync(stream, frame, cancellationToken).ConfigureAwait(false);
        return (frame[0], frame[1..]);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_sendLock)
        {
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }
        _cts.Dispose();
    }
}

internal sealed class KvmRouteChannel : IDisposable
{
    private readonly RouteControlHost? _host;
    private readonly RouteControlClient? _client;

    public event Action<KvmTarget>? RouteRequested;
    public event Action? InputRecoveryRequested;
    public event Action<bool>? ConnectionChanged;
    public event Action<KvmTarget>? TargetPublished;
    public event Action<string>? StatusChanged;

    public KvmRouteChannel(KvmConfig config)
    {
        if (config.IsHost)
        {
            _host = new RouteControlHost(config);
            _host.RouteRequested += target => RouteRequested?.Invoke(target);
            _host.StatusChanged += status => StatusChanged?.Invoke(status);
        }
        else
        {
            _client = new RouteControlClient(config);
            _client.InputRecoveryRequested += () => InputRecoveryRequested?.Invoke();
            _client.ConnectionChanged += connected => ConnectionChanged?.Invoke(connected);
            _client.TargetPublished += target => TargetPublished?.Invoke(target);
            _client.StatusChanged += status => StatusChanged?.Invoke(status);
        }
    }

    public void Start()
    {
        _host?.Start();
        _client?.Start();
    }

    public void SendTarget(KvmTarget target)
    {
        if (_client != null) _client.SendTarget(target);
        else RouteRequested?.Invoke(target);
    }

    public void PublishTarget(KvmTarget target) => _host?.PublishTarget(target);

    public void Dispose()
    {
        _client?.Dispose();
        _host?.Dispose();
    }
}

internal sealed class DeskflowBackend : IDisposable
{
    private const ushort InternalMainKey = 0x86; // VK_F23, reserved for the local route bridge.
    private const ushort InternalLaptopKey = 0x87; // VK_F24, reserved for the local route bridge.
    private readonly KvmConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _coreLock = new();
    private readonly object _recoveryLock = new();
    private readonly string? _corePathOverride;
    private readonly string? _stateDirectoryOverride;
    private Process? _core;
    private CancellationTokenSource? _inputRecovery;
    private KvmDiscoveryHost? _discovery;
    private int _clientLoopActive;
    private int _disposed;
    private int _controlConnected;
    private int _restartVersion;
    private int _inputPeerReady;
    private int _requestedTarget = -1;
    private readonly object _logLock = new();
    private string? _logPath;

    public event Action<string>? StatusChanged;
    public event Action<KvmTarget>? RouteChanged;
    public event Action? InputSessionReset;
    public bool IsRunning { get { lock (_coreLock) return _core is { HasExited: false }; } }
    public string? LogPath => _logPath;

    public DeskflowBackend(KvmConfig config, string? corePath = null, string? stateDirectory = null)
    {
        _config = config;
        _corePathOverride = corePath;
        _stateDirectoryOverride = stateDirectory;
    }

    public void Start()
    {
        if (_corePathOverride == null) CleanupManagedOrphan();
        if (Interlocked.Exchange(ref _clientLoopActive, 1) == 0)
            _ = Task.Run(StartClientLoop);
    }

    public void Restart()
    {
        Interlocked.Increment(ref _restartVersion);
    }

    public void ControlConnectionChanged(bool connected)
    {
        if (!_config.IsClient) return;
        Volatile.Write(ref _controlConnected, connected ? 1 : 0);
        CancelClientInputRecovery();
        Restart();
    }

    private void StartHost()
    {
        if (_config.AutoPort || _config.Port <= 0)
        {
            _config.Port = 24800;
            _config.AutoPort = false;
        }
        _config.Save();

        try
        {
            _discovery = new KvmDiscoveryHost(_config.Port, _config.ControlPort, _config.AuthToken, _config.DisplayName);
            _discovery.Start();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Deskflow 自动发现不可用: {ex.Message}");
        }

        StartCore(true, _config.ServerAddress);
    }

    private void StopCore()
    {
        CancelClientInputRecovery();
        lock (_coreLock)
        {
            _discovery?.Dispose();
            _discovery = null;
            var process = _core;
            _core = null;
            Volatile.Write(ref _inputPeerReady, 0);
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(2000); } catch { }
                process.Dispose();
                if (_config.IsClient && _corePathOverride == null)
                    InputInjector.ReleaseTransientState();
            }
        }
    }

    public static void CleanupManagedOrphan()
    {
        string expected;
        try
        {
            expected = Path.GetFullPath(FindCorePath());
        }
        catch
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName("deskflow-core"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (!string.Equals(executable, expected, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private async Task StartClientLoop()
    {
        var startedVersion = -1;
        var startedAt = 0L;
        var lastHealthCheck = 0L;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (_config.IsClient && Volatile.Read(ref _controlConnected) == 0)
                    {
                        StopCore();
                        await Task.Delay(250, _cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    var version = Volatile.Read(ref _restartVersion);
                    var stalled = false;
                    var now = Environment.TickCount64;
                    if (_config.IsClient && now - startedAt > 20000 && now - lastHealthCheck > 5000)
                    {
                        lastHealthCheck = now;
                        lock (_coreLock)
                        {
                            stalled = _core is { HasExited: false } && TcpProcessConnections.IsConnected(_core.Id) == false;
                        }
                    }
                    if (!IsRunning || version != startedVersion || stalled)
                    {
                        StopCore();
                        InputSessionReset?.Invoke();
                        if (_config.IsHost) StartHost();
                        else
                        {
                            if (_corePathOverride == null) InputInjector.ReleaseTransientState();
                            StartCore(false, _config.ServerAddress);
                        }
                        startedVersion = version;
                        startedAt = Environment.TickCount64;
                    }
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Deskflow 客户端启动失败: {ex.Message}");
                    await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            StopCore();
            Volatile.Write(ref _clientLoopActive, 0);
        }
    }

    private void StartCore(bool server, string? remoteHost)
    {
        var corePath = _corePathOverride ?? FindCorePath();
        var roleDir = Path.Combine(_stateDirectoryOverride ?? KvmConfig.DirectoryPath, "deskflow", server ? "host" : "client");
        Directory.CreateDirectory(roleDir);
        var settingsPath = Path.Combine(roleDir, "Deskflow.conf");
        var serverConfigPath = Path.Combine(roleDir, "server.conf");
        _logPath = Path.Combine(roleDir, "deskflow.log");
        File.AppendAllText(_logPath, $"\r\n--- {DateTime.Now:O} {(server ? "server" : "client")} ---\r\n", Encoding.UTF8);
        WriteSettings(settingsPath, server, remoteHost, serverConfigPath);
        if (server) WriteServerConfig(serverConfigPath);

        var start = new ProcessStartInfo
        {
            FileName = corePath,
            WorkingDirectory = Path.GetDirectoryName(corePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(server ? "server" : "client");
        start.ArgumentList.Add("--new-instance");
        start.ArgumentList.Add("--settings");
        start.ArgumentList.Add(settingsPath);

        Process process;
        lock (_coreLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_core is { HasExited: false }) return;
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Deskflow core");
            _core = process;
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try { StatusChanged?.Invoke($"Deskflow 已停止 (退出码 {process.ExitCode})"); }
            catch (InvalidOperationException) { }
        };
        _ = Task.Run(() => ReadLog(process, process.StandardError));
        _ = Task.Run(() => ReadLog(process, process.StandardOutput));
        StatusChanged?.Invoke(server
            ? $"Deskflow 主机已启动，等待客户端: {NetworkInfo.LocalIpv4Text()}:{_config.Port}"
            : $"Deskflow 客户端已启动，连接 {_config.ServerAddress}:{_config.Port}");
    }

    private void ReadLog(Process process, StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!ReferenceEquals(Volatile.Read(ref _core), process)) continue;
                lock (_logLock)
                {
                    if (_logPath != null)
                        File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                if (line.Contains("leaving screen", StringComparison.OrdinalIgnoreCase))
                    RouteChanged?.Invoke(_config.IsHost ? KvmTarget.Laptop : KvmTarget.Main);
                else if (line.Contains("entering screen", StringComparison.OrdinalIgnoreCase))
                    RouteChanged?.Invoke(_config.IsHost ? KvmTarget.Main : KvmTarget.Laptop);
                else if (line.Contains("client \"laptop\" has connected", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("IPC: connected to server", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref _inputPeerReady, 1);
                    StatusChanged?.Invoke("Deskflow 已连接另一台电脑");
                    TryTriggerPendingTarget();
                }
                else if (line.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("connecting to server", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("disconnected from server", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("server is dead", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("error writing to client", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("client \"laptop\" is dead", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref _inputPeerReady, 0);
                    CancelClientInputRecovery();
                    StatusChanged?.Invoke($"Deskflow 正在连接: {line}");
                }
                else if (line.Contains("failed to register hotkey", StringComparison.OrdinalIgnoreCase))
                    StatusChanged?.Invoke("Deskflow 热键已被其他程序占用，请在托盘中设置其他热键");
                else if (line.Contains("already has a connected client", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("client with name", StringComparison.OrdinalIgnoreCase))
                    StatusChanged?.Invoke("Deskflow 客户端名称冲突，请退出其他 one-for-all-switch 实例");
                else if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
                    StatusChanged?.Invoke($"Deskflow: {line}");
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException ex) { StatusChanged?.Invoke($"Deskflow 日志读取失败: {ex.Message}"); }
    }

    private void WriteSettings(string path, bool server, string? remoteHost, string serverConfigPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[core]");
        builder.AppendLine($"port={_config.Port}");
        builder.AppendLine($"computerName={(_config.IsHost ? "main" : "laptop")}");
        builder.AppendLine("useHooks=true");
        builder.AppendLine("processMode=1");
        builder.AppendLine($"preventSleep={(_config.IsClient && _config.KeepClientAwake ? "true" : "false")}");
        builder.AppendLine("[security]");
        builder.AppendLine("tlsEnabled=false");
        if (server)
        {
            builder.AppendLine("[server]");
            builder.AppendLine("externalConfig=true");
            builder.AppendLine($"externalConfigFile={serverConfigPath.Replace('\\', '/')}");
        }
        else
        {
            builder.AppendLine("[client]");
            builder.AppendLine($"remoteHost={remoteHost}");
            builder.AppendLine("dynamicConnectionInterval=false");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private void WriteServerConfig(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("section: screens");
        builder.AppendLine("\tmain:");
        builder.AppendLine("\tlaptop:");
        builder.AppendLine("end");
        builder.AppendLine("section: links");
        builder.AppendLine("\tmain:");
        builder.AppendLine("\tlaptop:");
        builder.AppendLine("end");
        builder.AppendLine("section: options");
        // Deskflow captures the user hotkey before forwarding it to the laptop.
        // switchToNextScreen cycles the two named screens without edge links.
        var userHotkey = HotkeyBinding.Parse(_config.Hotkey).ToDeskflow();
        builder.AppendLine($"\tkeystroke({userHotkey}) = switchToNextScreen");
        // A route request must not introduce modifiers into either desktop.
        builder.AppendLine("\tkeystroke(F23) = switchToScreen(main)");
        builder.AppendLine("\tkeystroke(F24) = switchToScreen(laptop)");
        builder.AppendLine("end");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    public void TriggerTarget(KvmTarget target)
    {
        if (!_config.IsHost || Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _requestedTarget, (int)target);
        if (!IsRunning || (target == KvmTarget.Laptop && Volatile.Read(ref _inputPeerReady) == 0))
        {
            StatusChanged?.Invoke("正在恢复键鼠连接，切换命令已排队");
            return;
        }
        TryTriggerPendingTarget();
    }

    private void TryTriggerPendingTarget()
    {
        if (!_config.IsHost || !IsRunning || Volatile.Read(ref _disposed) != 0) return;
        var pending = Volatile.Read(ref _requestedTarget);
        if (pending < 0 || (pending == (int)KvmTarget.Laptop && Volatile.Read(ref _inputPeerReady) == 0)) return;
        if (Interlocked.CompareExchange(ref _requestedTarget, -1, pending) != pending) return;
        var target = (KvmTarget)pending;
        var key = target == KvmTarget.Laptop ? InternalLaptopKey : InternalMainKey;
        try { InputInjector.TapRouteKey(key); }
        catch (InvalidOperationException ex) { StatusChanged?.Invoke(ex.Message); }
    }

    public void RecoverClientInput()
    {
        if (_config.IsHost || Volatile.Read(ref _disposed) != 0) return;
        int processId;
        lock (_coreLock)
        {
            try
            {
                if (_core == null || _core.HasExited) return;
                processId = _core.Id;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        var recovery = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        CancellationTokenSource? previous;
        lock (_recoveryLock)
        {
            if (_inputRecovery != null)
            {
                recovery.Dispose();
                return;
            }
            previous = _inputRecovery;
            _inputRecovery = recovery;
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        _ = Task.Run(() => RecoverClientInputAsync(processId, recovery));
    }

    public void CancelClientInputRecovery()
    {
        CancellationTokenSource? recovery;
        lock (_recoveryLock)
        {
            recovery = _inputRecovery;
            _inputRecovery = null;
        }
        try { recovery?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RecoverClientInputAsync(int processId, CancellationTokenSource recovery)
    {
        try
        {
            var attemptTimes = new[] { 80, 250, 1000, 3000, 6000 };
            var previousTime = 0;
            for (var attempt = 0; attempt < attemptTimes.Length; attempt++)
            {
                var delay = attemptTimes[attempt] - previousTime;
                if (delay > 0)
                    await Task.Delay(delay, recovery.Token).ConfigureAwait(false);
                recovery.Token.ThrowIfCancellationRequested();
                lock (_coreLock)
                {
                    if (recovery.IsCancellationRequested || _core == null || _core.HasExited || _core.Id != processId) return;
                    if (attempt == 0)
                        InputInjector.ReleaseTransientState();
                    InputInjector.WakeDisplayAndInput();
                    InputInjector.CancelDeskflowMouseCapture(processId);
                }
                previousTime = attemptTimes[attempt];
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_recoveryLock)
            {
                if (ReferenceEquals(_inputRecovery, recovery))
                    _inputRecovery = null;
            }
            recovery.Dispose();
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static string FindCorePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Deskflow", "deskflow-core.exe"),
            Path.Combine(AppContext.BaseDirectory, "deskflow-core.exe"),
            Path.Combine(KvmConfig.DirectoryPath, "Deskflow", "deskflow-core.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Deskflow", "deskflow-core.exe")
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("找不到 Deskflow。请先运行 setup-deskflow.ps1，或将 Deskflow 文件夹与 one-for-all-switch.exe 放在一起。", candidates[0]);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        StopCore();
    }
}

public sealed class KvmApplication : IDisposable
{
    private readonly KvmConfig _config;
    private readonly HotkeyForm _form;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _laptopMenu;
    private readonly ToolStripMenuItem _mainMenu;
    private readonly ToolStripMenuItem _statusMenu;
    private readonly ToolStripMenuItem _hotkeyMenu;
    private readonly DeskflowBackend _deskflow;
    private readonly KvmControlListener _control;
    private readonly KvmRouteChannel _routeChannel;
    private HotkeyBinding _hotkeyBinding;
    private KvmTarget _target;
    private volatile int _switching;
    private int _disposed;
    private int _routeInitialized;
    private int _pendingTarget = -1;

    public KvmApplication(KvmConfig config)
    {
        _config = config;
        _hotkeyBinding = HotkeyBinding.Parse(config.Hotkey);
        // This hidden form owns the UI message loop used by the keyboard watcher.
        _form = new HotkeyForm(_hotkeyBinding);
        _ = _form.Handle;
        _deskflow = new DeskflowBackend(config);
        _control = new KvmControlListener();
        _routeChannel = new KvmRouteChannel(config);
        _target = config.LastTarget.Equals("laptop", StringComparison.OrdinalIgnoreCase) ? KvmTarget.Laptop : KvmTarget.Main;
        _statusMenu = new ToolStripMenuItem("正在启动...") { Enabled = false };
        _laptopMenu = new ToolStripMenuItem("切换到笔记本", null, (_, _) => RequestTarget(KvmTarget.Laptop));
        _mainMenu = new ToolStripMenuItem("切换到主机", null, (_, _) => RequestTarget(KvmTarget.Main));
        var reconnect = new ToolStripMenuItem("重新连接", null, (_, _) => _deskflow.Restart());
        var diagnose = new ToolStripMenuItem("只读检测显示器亮度（5 次）", null, (_, _) => Diagnose());
        var openLog = new ToolStripMenuItem("打开 Deskflow 日志", null, (_, _) => OpenLog());
        _hotkeyMenu = new ToolStripMenuItem($"设置切换热键 ({_hotkeyBinding})", null, (_, _) => ConfigureHotkey());
        var exit = new ToolStripMenuItem("退出", null, (_, _) => Application.Exit());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_laptopMenu);
        menu.Items.Add(_mainMenu);
        if (_config.IsHost) menu.Items.Add(_hotkeyMenu);
        if (_config.IsClient) menu.Items.Add(reconnect);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(diagnose);
        menu.Items.Add(openLog);
        menu.Items.Add(exit);
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = true, Text = "one-for-all-switch", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => RequestTarget(_target == KvmTarget.Main ? KvmTarget.Laptop : KvmTarget.Main);
        _deskflow.StatusChanged += UpdateStatus;
        _deskflow.InputSessionReset += () => OnUi(() =>
        {
            _target = KvmTarget.Main;
            Volatile.Write(ref _routeInitialized, 0);
            if (_config.IsHost) _routeChannel.PublishTarget(KvmTarget.Main);
        });
        _deskflow.RouteChanged += target => OnUi(() => SetTarget(target, true));
        _control.RouteReceived += target => OnUi(() => SetTarget(target, true));
        _routeChannel.StatusChanged += UpdateStatus;
        _routeChannel.RouteRequested += target => OnUi(() => RequestTarget(target));
        _routeChannel.ConnectionChanged += _deskflow.ControlConnectionChanged;
        _routeChannel.TargetPublished += target => OnUi(() => SetTarget(target, true));
        _routeChannel.InputRecoveryRequested += () => OnUi(() =>
        {
            if (_target == KvmTarget.Laptop) _deskflow.RecoverClientInput();
        });
        _form.FormClosed += (_, _) => Dispose();
    }

    public void Run()
    {
        try
        {
            _control.Start();
            _routeChannel.Start();
            _deskflow.Start();
            UpdateStatus("已启动，等待快捷键切换");
            Application.Run(_form);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "one-for-all-switch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Dispose();
        }
    }

    private void OnUi(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _form.BeginInvoke(action); }
        catch (InvalidOperationException) when (Volatile.Read(ref _disposed) != 0) { }
    }

    private void RequestTarget(KvmTarget target)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (_config.IsClient)
        {
            _routeChannel.SendTarget(target);
            return;
        }

        _deskflow.TriggerTarget(target);
    }

    private void ConfigureHotkey()
    {
        var selected = HotkeyDialog.Choose(_form, _hotkeyBinding);
        if (selected == null || selected.ToString() == _hotkeyBinding.ToString()) return;
        try
        {
            _config.Hotkey = selected.ToString();
            _form.SetHotkey(selected);
            _config.Save();
            _hotkeyBinding = selected;
            _deskflow.Restart();
            _hotkeyMenu.Text = $"设置切换热键 ({selected})";
            Show($"切换热键已设置为 {selected}");
        }
        catch (Exception ex)
        {
            Show(ex.Message);
        }
    }

    private void SetTarget(KvmTarget target, bool fromDeskflow)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (!fromDeskflow)
        {
            RequestTarget(target);
            return;
        }
        if (Volatile.Read(ref _routeInitialized) != 0 && _target == target)
        {
            if (_config.IsHost)
                _routeChannel.PublishTarget(target);
            else if (_config.IsClient && target == KvmTarget.Laptop)
                _deskflow.RecoverClientInput();
            else if (_config.IsClient)
                _deskflow.CancelClientInputRecovery();
            return;
        }
        if (_switching != 0)
        {
            Volatile.Write(ref _pendingTarget, (int)target);
            return;
        }
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _switching = 1;
        try
        {
            var input = target == KvmTarget.Laptop ? _config.LaptopInput : _config.MainInput;
            if (_config.IsHost)
                _routeChannel.PublishTarget(target);
            else if (_config.IsClient && target == KvmTarget.Main)
                _deskflow.CancelClientInputRecovery();
            // The monitor is controlled from the host side only. Writing DDC
            // from the inactive laptop GPU was the source of client stalls and
            // made Deskflow drop the connection after a route change.
            var localTask = _config.IsHost
                ? Task.Run(() => SetInputWithRetry(input))
                : Task.FromResult((true, string.Empty));
            (bool ok, string error) result;
            try
            {
                result = localTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                result = (false, "DDC/CI 操作超时");
            }
            if (!result.Item1) Show($"显示器输入切换失败: {result.Item2}");
            _target = target;
            if (_config.IsClient && target == KvmTarget.Laptop)
                _deskflow.RecoverClientInput();
            Volatile.Write(ref _routeInitialized, 1);
            _config.LastTarget = target == KvmTarget.Laptop ? "laptop" : "main";
            _config.Save();
            var routeText = target == KvmTarget.Laptop ? "笔记本" : "主机";
            UpdateStatus(result.Item1 ? $"当前: {routeText}" : $"当前: {routeText}（DDC/CI 失败）");
        }
        finally
        {
            _switching = 0;
            var pending = Interlocked.Exchange(ref _pendingTarget, -1);
            if (pending >= 0 && pending != (int)_target)
                SetTarget((KvmTarget)pending, true);
        }
    }

    private (bool ok, string error) SetInputWithRetry(byte input)
    {
        string error = string.Empty;
        var retries = Math.Clamp(_config.DdcRetryCount, 1, 8);
        for (var attempt = 0; attempt < retries; attempt++)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return (false, "程序正在退出");
            if (DdcController.SetInput(
                    input,
                    _config.BrightnessConfigured && _config.SyncBrightness,
                    _config.BrightnessPercent,
                    _config.BrightnessDelayMs,
                    out error))
                return (true, string.Empty);
            if (attempt + 1 < retries) Thread.Sleep(150 * (attempt + 1));
        }
        return (false, error);
    }

    private void Diagnose()
    {
        var results = new StringBuilder();
        for (var round = 1; round <= 5; round++)
        {
            var ok = DdcController.ReadBrightness(out var reading);
            results.AppendLine($"第 {round} 次: {(ok ? reading : $"失败 - {reading}")}");
            if (round < 5) Thread.Sleep(120);
        }
        Show(results.ToString().TrimEnd());
    }

    private void OpenLog()
    {
        var path = _deskflow.LogPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Show("Deskflow 尚未生成日志。");
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void UpdateStatus(string status)
    {
        if (_form.IsDisposed) return;
        void Update()
        {
            _statusMenu.Text = status;
            _tray.Text = status.Length > 63 ? status[..63] : status;
            _laptopMenu.Checked = _target == KvmTarget.Laptop;
            _mainMenu.Checked = _target == KvmTarget.Main;
        }
        OnUi(Update);
    }

    private void Show(string message)
    {
        if (_form.IsDisposed) return;
        void Notify() => _tray.ShowBalloonTip(2500, "one-for-all-switch", message, ToolTipIcon.Warning);
        OnUi(Notify);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _control.Dispose();
        _routeChannel.Dispose();
        _deskflow.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _form.Dispose();
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var parentArgument = Array.IndexOf(args, "--wait-for-parent");
        if (parentArgument >= 0 && parentArgument + 1 < args.Length && int.TryParse(args[parentArgument + 1], out var parentId))
        {
            try { using var parent = Process.GetProcessById(parentId); parent.WaitForExit(10000); }
            catch (ArgumentException) { }
        }
        if (KvmControl.TryNotifyRoute(args)) return;
        using var mutex = new Mutex(true, "Local\\KvmSwitch.Singleton", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("one-for-all-switch 或兼容旧版已经在运行，请使用系统托盘中的实例。", "one-for-all-switch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        DeskflowBackend.CleanupManagedOrphan();
        KvmConfig config;
        try { config = KvmConfig.Load(args); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"无法读取已保存的配置：{ex.Message}", "one-for-all-switch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var autoStart = args.Any(arg => arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (autoStart && config.IsConfigured)
        {
            RunConfigured(config);
            return;
        }
        using var setup = new SetupForm(config);
        if (setup.ShowDialog() != DialogResult.OK) return;
        RunConfigured(config);
    }

    private static void RunConfigured(KvmConfig config)
    {
        try
        {
            if (RuntimePrivileges.RelaunchIfNeeded(config)) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动权限或开机启动配置未完成：{ex.Message}\n请重新打开配置窗口调整。", "one-for-all-switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { StartupManager.Apply(config.AutoStart, config.IsClient && config.ElevateClient); }
        catch (Exception ex)
        {
            MessageBox.Show($"开机启动项更新失败，本次继续运行：{ex.Message}", "one-for-all-switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        using var app = new KvmApplication(config);
        app.Run();
    }
}
