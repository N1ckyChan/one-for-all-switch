// SPDX-License-Identifier: GPL-2.0-only
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace KvmSwitch;

internal static class TcpProcessConnections
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    public static bool? IsConnected(int processId)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0); // IPv4, owner PID for all TCP states.
        if (size < 4) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) != 0) return null;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(buffer, 4 + i * rowSize));
                if (row.ProcessId == (uint)processId && row.State == 5) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

internal static class AtomicConfigFile
{
    public static string Read(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 2 or 32 or 33)
            {
                System.Threading.Thread.Sleep(20);
            }
        }
    }

    public static void Write(string path, string json)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class RuntimePrivileges
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool RelaunchIfNeeded(KvmConfig config)
    {
        if (!config.IsClient || !config.ElevateClient || IsElevated) return false;
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Application.ExecutablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--autostart");
        start.ArgumentList.Add("--wait-for-parent");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process = Process.Start(start) ?? throw new IOException("无法启动管理员窗口兼容模式");
        return true;
    }
}

internal static class ElevatedStartupTask
{
    internal static void DeleteIfPresent(Action deleteTask)
    {
        try { deleteTask(); }
        // COM interop can map missing-task HRESULTs to File/DirectoryNotFoundException.
        catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x80070003) { }
    }

    public static void Apply(bool enabled, string executable)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 用户");
        var taskName = "KvmSwitch-" + sid;
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows 任务计划服务不可用");
        dynamic service = Activator.CreateInstance(serviceType)!;
        object? rootObject = null;
        object? definitionObject = null;
        try
        {
            service.Connect();
            dynamic root = rootObject = service.GetFolder("\\");
            if (!enabled)
            {
                DeleteIfPresent(() => root.DeleteTask(taskName, 0));
                return;
            }

            dynamic definition = definitionObject = service.NewTask(0);
            definition.RegistrationInfo.Description = "one-for-all-switch client at Windows login";
            definition.Principal.UserId = sid;
            definition.Principal.LogonType = 3; // InteractiveToken, no stored password.
            definition.Principal.RunLevel = 1; // HighestAvailable.
            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2; // IgnoreNew.
            dynamic trigger = definition.Triggers.Create(9); // Logon.
            trigger.UserId = sid;
            dynamic action = definition.Actions.Create(0);
            action.Path = executable;
            action.Arguments = "--autostart";
            action.WorkingDirectory = Path.GetDirectoryName(executable)!;
            object registered = root.RegisterTaskDefinition(taskName, definition, 6, sid, null, 3, null);
            Marshal.FinalReleaseComObject(registered);
            Marshal.FinalReleaseComObject(action);
            Marshal.FinalReleaseComObject(trigger);
        }
        finally
        {
            if (definitionObject != null) Marshal.FinalReleaseComObject(definitionObject);
            if (rootObject != null) Marshal.FinalReleaseComObject(rootObject);
            Marshal.FinalReleaseComObject(service);
        }
    }
}

