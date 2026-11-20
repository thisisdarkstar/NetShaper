using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using NetShaper.Models;

namespace NetShaper.Core;

public static class NetworkMonitor
{
    private const int AF_INET = 2; // IPv4
    private const int AF_INET6 = 23; // IPv6

    public enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;

        public IPAddress LocalAddress => new(BitConverter.GetBytes(dwLocalAddr));
        public ushort LocalPort => (ushort)(((dwLocalPort & 0xFF) << 8) | ((dwLocalPort >> 8) & 0xFF));
        public IPAddress RemoteAddress => new(BitConverter.GetBytes(dwRemoteAddr));
        public ushort RemotePort => (ushort)(((dwRemotePort & 0xFF) << 8) | ((dwRemotePort >> 8) & 0xFF));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        TCP_TABLE_CLASS TableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MIB_TCPROW pTcpRow);

    public record TcpConnectionEntry(
        IPEndPoint LocalEndPoint,
        IPEndPoint RemoteEndPoint,
        uint State,
        int OwningPid);

    /// <summary>
    /// Calls Windows iphlpapi.dll GetExtendedTcpTable directly to retrieve all active TCP sockets and their owning PIDs.
    /// </summary>
    public static List<TcpConnectionEntry> GetActiveTcpConnections()
    {
        var result = new List<TcpConnectionEntry>();
        int bufferSize = 0;

        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
        IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);

        try
        {
            ret = GetExtendedTcpTable(tcpTablePtr, ref bufferSize, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0) return result;

            int numEntries = Marshal.ReadInt32(tcpTablePtr);
            IntPtr rowPtr = IntPtr.Add(tcpTablePtr, sizeof(int));
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                result.Add(new TcpConnectionEntry(
                    new IPEndPoint(row.LocalAddress, row.LocalPort),
                    new IPEndPoint(row.RemoteAddress, row.RemotePort),
                    row.dwState,
                    (int)row.dwOwningPid));

                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        catch
        {
            // Fallback gracefully on query error
        }
        finally
        {
            Marshal.FreeHGlobal(tcpTablePtr);
        }

        return result;
    }

    /// <summary>
    /// Builds a fast port-to-PID lookup map.
    /// </summary>
    public static Dictionary<int, int> GetPortToPidMap()
    {
        var map = new Dictionary<int, int>();
        var conns = GetActiveTcpConnections();
        foreach (var c in conns)
        {
            if (c.OwningPid > 0)
            {
                map[c.LocalEndPoint.Port] = c.OwningPid;
            }
        }
        return map;
    }

    /// <summary>
    /// Forcibly resets existing un-throttled direct TCP connections for a target application
    /// so the application immediately reconnects through the active proxy interceptor.
    /// </summary>
    public static int ResetSocketsForApp(string appName, int excludePort = 0)
    {
        string target = Path.GetFileNameWithoutExtension(appName).ToLowerInvariant();
        var pids = Process.GetProcesses()
            .Where(p => p.ProcessName.ToLowerInvariant().Contains(target))
            .Select(p => p.Id)
            .ToHashSet();

        if (pids.Count == 0) return 0;

        var conns = GetActiveTcpConnections();
        int resetCount = 0;

        foreach (var c in conns)
        {
            if (pids.Contains(c.OwningPid))
            {
                // Never reset connections that are already routing through our local proxy
                if (excludePort > 0 && (c.RemoteEndPoint.Port == excludePort || c.LocalEndPoint.Port == excludePort))
                    continue;

                // Established connections (State == 5)
                if (c.State == 5)
                {
                    var row = new MIB_TCPROW
                    {
                        dwState = 12, // MIB_TCP_STATE_DELETE_TCB
                        dwLocalAddr = BitConverter.ToUInt32(c.LocalEndPoint.Address.GetAddressBytes(), 0),
                        dwLocalPort = (uint)(((c.LocalEndPoint.Port & 0xFF) << 8) | ((c.LocalEndPoint.Port >> 8) & 0xFF)),
                        dwRemoteAddr = BitConverter.ToUInt32(c.RemoteEndPoint.Address.GetAddressBytes(), 0),
                        dwRemotePort = (uint)(((c.RemoteEndPoint.Port & 0xFF) << 8) | ((c.RemoteEndPoint.Port >> 8) & 0xFF))
                    };
                    uint ret = SetTcpEntry(ref row);
                    if (ret == 0) resetCount++;
                }
            }
        }
        return resetCount;
    }
}
