using System;
using System.Runtime.InteropServices;

namespace PerfMonitorLive.Metrics
{
    /// <summary>Compteurs CPU et mémoire lus directement par le noyau, sans WMI : quelques microsecondes et,
    /// surtout, un temps de réponse insensible à la charge de la machine (WMI a été mesuré jusqu'à 19 s
    /// sur une machine saturée, ce qui trouait l'historique et retardait les alertes à maintien).</summary>
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buf);

        static ulong _idle, _busy;

        /// <summary>Charge processeur moyenne depuis l'appel précédent, en % (0 au tout premier appel).</summary>
        public static double CpuPercent()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            ulong i = idle.Value, busy = kernel.Value + user.Value;   // « kernel » inclut le temps d'inactivité
            ulong di = i - _idle, db = busy - _busy;
            _idle = i; _busy = busy;
            if (db == 0 || di > db) return 0;
            return Math.Round((db - di) * 100.0 / db, 1);
        }

        /// <summary>Mémoire physique totale et disponible, en Mo.</summary>
        public static void Memory(out double totalMB, out double availMB)
        {
            totalMB = 0; availMB = 0;
            var m = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(m)) return;
            totalMB = m.ullTotalPhys / 1048576.0;
            availMB = m.ullAvailPhys / 1048576.0;
        }
    }
}
