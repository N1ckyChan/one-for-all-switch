// SPDX-License-Identifier: GPL-2.0-only
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KvmSwitch.Regression;

internal static class Program
{
    private static string _directory = string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() is "client" or "server")
        {
            Console.WriteLine("IPC: connected to server");
            Thread.Sleep(60000);
            return 0;
        }

        _directory = Directory.CreateTempSubdirectory("KvmSwitch-regression-").FullName;
        try
        {
            if (args.Contains("--startup-only"))
            {
                CheckStartupTask();
                return 0;
            }
            CheckKeyAndUi();
            if (args.Contains("--ui-only"))
            {
                Console.WriteLine("Artifacts: " + _directory);
                return 0;
            }
            CheckStartupTask();
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("ALL REGRESSIONS PASSED");
            Console.WriteLine("Artifacts: " + _directory);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckStartupTask()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler is unavailable");
        object serviceObject = Activator.CreateInstance(serviceType)!;
        object? rootObject = null;
        try
        {
            dynamic service = serviceObject;
            service.Connect();
            dynamic root = rootObject = service.GetFolder("\\");
            var taskName = "KvmSwitch-regression-missing-" + Guid.NewGuid().ToString("N");
            var missing = false;
            try
            {
                object existing = root.GetTask(taskName);
                Marshal.FinalReleaseComObject(existing);
            }
            catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x80070003)
            {
                missing = true;
                Console.WriteLine($"Task Scheduler missing task: {ex.GetType().FullName} (0x{ex.HResult:X8})");
            }
            Check(missing, "Probe task unexpectedly exists; refusing to delete it");
            // Only probe a verified absent, unique task; never alter real startup preferences.
            ElevatedStartupTask.DeleteIfPresent(() => root.DeleteTask(taskName, 0));
            ElevatedStartupTask.DeleteIfPresent(() => root.DeleteTask(taskName, 0));
        }
        finally
        {
            if (rootObject != null) Marshal.FinalReleaseComObject(rootObject);
            Marshal.FinalReleaseComObject(serviceObject);
        }

        var calls = 0;
        ElevatedStartupTask.DeleteIfPresent(() => calls++);
        Check(calls == 1, "Task deletion was skipped or repeated");
        foreach (var missing in new Exception[]
        {
            new FileNotFoundException("Missing task"),
            new DirectoryNotFoundException("Missing task path"),
            new COMException("Missing task", unchecked((int)0x80070002)),
            new COMException("Missing task path", unchecked((int)0x80070003))
        })
            ElevatedStartupTask.DeleteIfPresent(() => throw missing);

        foreach (var failure in new Exception[]
        {
            new UnauthorizedAccessException("Task access denied"),
            new COMException("Task access denied", unchecked((int)0x80070005)),
            new COMException("Scheduler unavailable", unchecked((int)0x800706BA)),
            new IOException("Unexpected I/O failure"),
            new InvalidOperationException("Unexpected scheduler failure")
        })
        {
            Exception? observed = null;
            try { ElevatedStartupTask.DeleteIfPresent(() => throw failure); }
            catch (Exception ex) { observed = ex; }
            Check(ReferenceEquals(observed, failure), "Task deletion swallowed a real failure: " + failure.GetType().Name);
        }
        Console.WriteLine("PASS absent startup task deletion is idempotent, handles mapped/COM exceptions, preserves real failures");
    }

    private static void CheckKeyAndUi()
    {
        foreach (var key in new ushort[] { 0xA3, 0xA5, 0x5B, 0x5C })
            Check(InputInjector.KeyFlags(key, true) == 3, "Right-side/Windows modifier release needs EXTENDEDKEY + KEYUP");
        Check(InputInjector.KeyFlags(0xA4, true) == 2, "Left Alt must not be marked extended");
        Check(InputInjector.KeyFlags(0x86, false) == 0, "Internal route key must have no modifiers");

        using var form = new SetupForm(new KvmConfig { Role = "client" });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(_directory, "client-setup.png"), ImageFormat.Png);
        foreach (Control control in form.Controls)
            Check(control.Right <= form.ClientSize.Width && control.Bottom <= form.ClientSize.Height,
                "Configuration control clipped: " + control.Text);
        Console.WriteLine("PASS extended modifier releases and client configuration layout");
    }

    private static async Task RunAsync()
    {
        await AtomicConfigAsync();
        await ReconnectAsync();
        await FragmentedFramesAsync();
        await BackendLifecycleAsync();
    }

    private static KvmConfig Config(string name) => new()
    {
        Role = "client", DisplayName = "laptop", AuthToken = "regression-token",
        ServerAddress = "127.0.0.1", AutoDiscover = false,
        StoragePath = Path.Combine(_directory, name + ".json")
    };

    private static async Task AtomicConfigAsync()
    {
        var config = Config("atomic");
        config.Hotkey = "Ctrl+Alt+1";
        config.Save();
        var saves = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++) config.Save();
        })).ToArray();
        for (var i = 0; i < 100; i++)
        {
            using var parsed = JsonDocument.Parse(AtomicConfigFile.Read(config.StoragePath!));
            Check(parsed.RootElement.GetProperty("Hotkey").GetString() == "Ctrl+Alt+1", "Configuration lost hotkey");
        }
        await Task.WhenAll(saves);
        Check(Directory.GetFiles(_directory, "*.tmp").Length == 0, "Atomic save left temporary files");
        Console.WriteLine("PASS concurrent configuration writes remain valid JSON");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task ReconnectAsync()
    {
        var hostConfig = Config("host");
        hostConfig.Role = "host";
        hostConfig.ControlPort = FreePort();
        var clientConfig = Config("client");
        clientConfig.ControlPort = hostConfig.ControlPort;
        KvmDiscoveryEndpoint endpoint = new()
        {
            Address = "127.0.0.1", Port = 24800, ControlPort = hostConfig.ControlPort, Token = hostConfig.AuthToken
        };
        Task<KvmDiscoveryEndpoint?> Discover(string? _, CancellationToken token) => Task.FromResult<KvmDiscoveryEndpoint?>(endpoint);
        RouteControlHost? host = new(hostConfig);
        RouteControlClient? client = new(clientConfig, Discover);
        var connected = 0;
        var disconnected = 0;
        var wakes = 0;
        var routes = new ConcurrentQueue<KvmTarget>();
        void Observe(RouteControlClient control)
        {
            control.ConnectionChanged += online => { if (online) Interlocked.Increment(ref connected); else Interlocked.Increment(ref disconnected); };
            control.InputRecoveryRequested += () => Interlocked.Increment(ref wakes);
        }
        Observe(client);
        try
        {
            // Client starts first and retains a tray request while the host is down.
            client.Start();
            client.SendTarget(KvmTarget.Main);
            await Task.Delay(200);
            host.RouteRequested += routes.Enqueue;
            host.Start();
            await Until(() => Volatile.Read(ref connected) == 1 && routes.Count == 1);
            host.PublishTarget(KvmTarget.Laptop);
            await Until(() => Volatile.Read(ref wakes) == 1);
            await Task.Delay(1800);
            Check(Volatile.Read(ref wakes) == 1, "ACK retries ran input recovery more than once");
            Check(TcpProcessConnections.IsConnected(Environment.ProcessId) == true, "TCP connection owner inspection failed");

            host.Dispose();
            host = null;
            await Until(() => Volatile.Read(ref disconnected) >= 1);
            hostConfig.AuthToken = "new-token-after-server-reboot";
            hostConfig.ControlPort = FreePort();
            endpoint = new KvmDiscoveryEndpoint
            {
                Address = "127.0.0.1", Port = 24999, ControlPort = hostConfig.ControlPort, Token = hostConfig.AuthToken
            };
            hostConfig.Port = endpoint.Port;
            host = new RouteControlHost(hostConfig);
            host.Start();
            await Until(() => Volatile.Read(ref connected) == 2);
            Check(clientConfig.AuthToken == hostConfig.AuthToken && clientConfig.Port == 24999,
                "Server reboot did not refresh token/endpoint");
            host.PublishTarget(KvmTarget.Laptop);
            await Until(() => Volatile.Read(ref wakes) == 2);

            client.Dispose();
            client = null;
            await Task.Delay(200);
            host.PublishTarget(KvmTarget.Laptop);
            host.PublishTarget(KvmTarget.Main);
            client = new RouteControlClient(clientConfig, Discover);
            Observe(client);
            client.Start();
            await Until(() => Volatile.Read(ref connected) == 3);
            await Task.Delay(1200);
            Check(Volatile.Read(ref wakes) == 2, "Returning to main failed to cancel pending wake");
            host.PublishTarget(KvmTarget.Laptop);
            await Until(() => Volatile.Read(ref wakes) == 3);
            Console.WriteLine("PASS client-first startup, queued route, both peer reboots, token/port refresh, wake deduplication/cancellation");
        }
        finally { client?.Dispose(); host?.Dispose(); }
    }

    private static async Task FragmentedFramesAsync()
    {
        var config = Config("fragmented");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        config.ControlPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new RouteControlClient(config, (_, _) => Task.FromResult<KvmDiscoveryEndpoint?>(null));
        var wakes = 0;
        var connects = 0;
        var disconnects = 0;
        client.InputRecoveryRequested += () => Interlocked.Increment(ref wakes);
        client.ConnectionChanged += online => { if (online) Interlocked.Increment(ref connects); else Interlocked.Increment(ref disconnects); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        try
        {
            client.Start();
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            await ReadFrame(stream, timeout.Token);
            var helloAck = Protocol.Frame(Protocol.Ack, w => { w.Write(Protocol.Hello); w.Write("test-session"); w.Write(24800); });
            foreach (var value in helloAck)
                await stream.WriteAsync(new[] { value }, timeout.Token);
            await Until(() => Volatile.Read(ref connects) == 1);
            var wake = Protocol.Frame(Protocol.Wake, w => w.Write(7L));
            foreach (var value in wake)
                await stream.WriteAsync(new[] { value }, timeout.Token);
            var ack = await ReadFrame(stream, timeout.Token);
            Check(ack[0] == Protocol.Ack && ack[1] == Protocol.Wake, "Fragmented wake was not acknowledged");
            await stream.WriteAsync(wake, timeout.Token);
            await ReadFrame(stream, timeout.Token);
            Check(Volatile.Read(ref wakes) == 1, "Repeated ID triggered duplicate recovery");
            // Keep TCP open but never answer a heartbeat: this is the half-open failure case.
            await Until(() => Volatile.Read(ref disconnects) == 1, 18000);
            Console.WriteLine("PASS fragmented TCP frames, duplicate ACK, silent-peer heartbeat timeout");
        }
        finally { listener.Stop(); }
    }

    private static async Task<byte[]> ReadFrame(NetworkStream stream, CancellationToken token)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        var body = new byte[BitConverter.ToInt32(length)];
        await stream.ReadExactlyAsync(body, token);
        return body;
    }

    private static async Task BackendLifecycleAsync()
    {
        var config = Config("backend");
        var fakeCore = Environment.ProcessPath!;
        using var backend = new DeskflowBackend(config, fakeCore, _directory);
        var processField = typeof(DeskflowBackend).GetField("_core", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int CurrentPid()
        {
            try { return (processField.GetValue(backend) as Process)?.Id ?? 0; }
            catch (InvalidOperationException) { return 0; }
        }
        backend.Start();
        await Task.Delay(400);
        Check(!backend.IsRunning, "Deskflow started before control authentication");
        backend.ControlConnectionChanged(true);
        await Until(() => backend.IsRunning);
        var initialPid = CurrentPid();
        backend.ControlConnectionChanged(false);
        await Until(() => !backend.IsRunning);
        config.ServerAddress = "127.0.0.2";
        backend.ControlConnectionChanged(true);
        await Until(() => backend.IsRunning && CurrentPid() != initialPid);
        Check(File.ReadAllText(Path.Combine(_directory, "deskflow", "client", "Deskflow.conf")).Contains("remoteHost=127.0.0.2"),
            "Deskflow restart retained old host address");
        var currentPid = CurrentPid();
        await Until(() => backend.IsRunning && CurrentPid() != currentPid, 26000);
        currentPid = CurrentPid();
        using (var core = Process.GetProcessById(currentPid)) core.Kill();
        await Until(() => backend.IsRunning && CurrentPid() != currentPid);
        currentPid = CurrentPid();
        backend.Dispose();
        await Task.Delay(600);
        Check(!backend.IsRunning, "Deskflow process survived disposal");
        try { using var core = Process.GetProcessById(currentPid); Check(core.HasExited, "Orphan child process remains"); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS input process gated by authentication, rebuilt after reconnect/stalled TCP/crash, no child left after exit");
    }

    private static async Task Until(Func<bool> predicate, int milliseconds = 10000)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Regression condition did not become true");
            await Task.Delay(25);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
