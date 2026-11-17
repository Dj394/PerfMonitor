using System;
using System.Collections.Generic;
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
        static readonly MEMORYSTATUSEX _mem = new MEMORYSTATUSEX();   // réutilisé : n'est lu que par le thread de mesure

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
            lock (_mem)
            {
                if (!GlobalMemoryStatusEx(_mem)) return;
                totalMB = _mem.ullTotalPhys / 1048576.0;
                availMB = _mem.ullAvailPhys / 1048576.0;
            }
        }

        /// <summary>Copie une chaîne native terminée par zéro ; renvoie <paramref name="prev"/> (sans allouer) si elle est identique.</summary>
        internal static string ReuseString(IntPtr p, int maxChars, string prev, ref char[] scratch)
        {
            if (p == IntPtr.Zero) return "";
            int len = 0;
            while (len < maxChars && Marshal.ReadInt16(p, len * 2) != 0) len++;
            if (len == 0) return "";
            if (scratch.Length < len) scratch = new char[Math.Max(len, scratch.Length * 2)];
            Marshal.Copy(p, scratch, 0, len);
            var span = new ReadOnlySpan<char>(scratch, 0, len);
            if (prev != null && span.SequenceEqual(prev.AsSpan())) return prev;
            return new string(span);
        }
    }

    /// <summary>Requête PDH en processus (la même source que perfmon) : remplace les classes
    /// <c>Win32_PerfFormattedData_*</c>, dont chaque lecture réveillait WmiPrvSE. Les valeurs « formatées » sont des
    /// taux calculés par PDH entre les deux dernières <see cref="Collect"/>. N'est utilisée que par un seul thread.</summary>
    internal sealed class Pdh : IDisposable
    {
        public const uint FMT_DOUBLE = 0x00000200, FMT_LARGE = 0x00000400, FMT_NOCAP100 = 0x00008000;
        public const uint MORE_DATA = 0x800007D2, NO_DATA = 0x800007D5, INVALID_DATA = 0xC0000BC6;
        const uint CSTATUS_VALID_DATA = 0, CSTATUS_NEW_DATA = 1;

        [StructLayout(LayoutKind.Explicit)]
        struct FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; [FieldOffset(8)] public long Large; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FMT_COUNTERVALUE value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

        /// <summary>Une instance d'un compteur à joker. <see cref="Name"/> est réutilisé d'une lecture à l'autre s'il n'a pas changé.</summary>
        public struct Item { public string Name; public double Value; public bool Valid; }

        IntPtr _query, _buf; uint _bufSize;
        char[] _chars = new char[64];

        public uint Open() => PdhOpenQueryW(null, IntPtr.Zero, out _query);
        public uint Add(string englishPath, out IntPtr counter) => PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out counter);
        public uint Collect() => PdhCollectQueryData(_query);

        /// <summary>Compteur sans instance. Renvoie 0 (ERROR_SUCCESS) si la valeur est valide.</summary>
        public uint Value(IntPtr counter, uint format, out double value)
        {
            value = 0;
            uint st = PdhGetFormattedCounterValue(counter, format, out _, out var v);
            if (st != 0) return st;
            if (v.CStatus != CSTATUS_VALID_DATA && v.CStatus != CSTATUS_NEW_DATA) return v.CStatus;
            value = (format & FMT_LARGE) != 0 ? v.Large : v.Double;
            return 0;
        }

        /// <summary>Toutes les instances d'un compteur à joker, dans <paramref name="items"/> (réutilisé, agrandi au besoin).
        /// Aucune instance n'est un résultat normal (0 renvoyé, <paramref name="count"/> = 0).</summary>
        public uint Array(IntPtr counter, uint format, ref Item[] items, out int count)
        {
            count = 0;
            uint st = 0, n = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint size = _bufSize;
                st = PdhGetFormattedCounterArrayW(counter, format, ref size, out n, _buf);
                if (st != MORE_DATA) break;
                if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                _bufSize = Math.Max(size + 1024, _bufSize * 2);
                _buf = Marshal.AllocHGlobal((int)_bufSize);
            }
            if (st == NO_DATA || st == INVALID_DATA && n == 0) return 0;
            if (st != 0) return st;
            const int Stride = 24;   // PDH_FMT_COUNTERVALUE_ITEM_W : LPWSTR + PDH_FMT_COUNTERVALUE (aligné sur 8) — 24 octets en 32 et 64 bits
            if ((long)n * Stride > _bufSize) return INVALID_DATA;
            if (items == null || items.Length < n) { var old = items; items = new Item[Math.Max((int)n, 8)]; if (old != null) System.Array.Copy(old, items, old.Length); }
            for (int i = 0; i < n; i++)
            {
                IntPtr e = _buf + i * Stride;
                items[i].Name = Native.ReuseString(Marshal.ReadIntPtr(e), 512, items[i].Name, ref _chars);
                uint cs = (uint)Marshal.ReadInt32(e, 8);
                items[i].Valid = cs == CSTATUS_VALID_DATA || cs == CSTATUS_NEW_DATA;
                long raw = Marshal.ReadInt64(e, 16);
                items[i].Value = !items[i].Valid ? 0 : (format & FMT_LARGE) != 0 ? raw : BitConverter.Int64BitsToDouble(raw);
            }
            count = (int)n;
            return 0;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
            if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; _bufSize = 0; }
        }
    }

    /// <summary>Un processus vu par <see cref="ProcessSnapshot"/>.</summary>
    internal struct ProcEntry
    {
        public int Pid;
        /// <summary>Même forme que <c>Process.ProcessName</c> : chemin et « .exe » retirés.</summary>
        public string Name;
        /// <summary>KernelTime + UserTime, en unités de 100 ns (= <c>Process.TotalProcessorTime.Ticks</c>).</summary>
        public long CpuTime;
        /// <summary>PrivatePageCount, en octets (= <c>Process.PrivateMemorySize64</c>).</summary>
        public long PrivateBytes;
        public int Handles;
    }

    /// <summary>Tous les processus en un seul appel <c>NtQuerySystemInformation(SystemProcessInformation)</c> — ce que fait
    /// déjà <c>Process.GetProcesses</c> en interne, mais sans ouvrir chaque processus pour son temps CPU (300 OpenProcess
    /// et autant d'exceptions « accès refusé » toutes les 5 s). Tampon et noms réutilisés. Un seul thread.</summary>
    internal sealed class ProcessSnapshot : IDisposable
    {
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
        const int SystemProcessInformation = 5;
        const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        const int MaxBuffer = 64 << 20;

        // SYSTEM_PROCESS_INFORMATION, x64 (et ARM64)
        const int OffNext = 0x00, OffUserTime = 0x28, OffKernelTime = 0x30, OffNameLen = 0x38, OffNameBuf = 0x40,
                  OffPid = 0x50, OffHandles = 0x60, OffPrivatePageCount = 0xC8, EntryMin = 0xD0;

        /// <summary>Le format n'est lu qu'en 64 bits ; sinon l'appelant garde l'API .NET.</summary>
        public static bool Supported => IntPtr.Size == 8;

        IntPtr _buf; int _size = 512 * 1024;
        char[] _chars = new char[64];
        int _gen;
        readonly Dictionary<int, KeyValuePair<int, string>> _names = new Dictionary<int, KeyValuePair<int, string>>();
        readonly List<int> _dead = new List<int>();

        /// <summary>Remplit <paramref name="list"/> (vidée). Renvoie le NTSTATUS (0 = succès). Le processus inactif (PID 0) est inclus.</summary>
        public int Read(List<ProcEntry> list)
        {
            list.Clear();
            if (!Supported) return -1;
            int st, ret = 0;
            for (int attempt = 0; ; attempt++)
            {
                if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_size);
                st = NtQuerySystemInformation(SystemProcessInformation, _buf, _size, out ret);
                if (st != STATUS_INFO_LENGTH_MISMATCH || attempt >= 4) break;
                int want = Math.Max(ret + 64 * 1024, _size * 2);   // la liste peut encore grandir entre deux appels
                if (want > MaxBuffer) return st;
                Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; _size = want;
            }
            if (st != 0) return st;
            if (ret <= 0 || ret > _size) ret = _size;

            _gen++;
            long baseAddr = (long)_buf, end = baseAddr + ret;
            int off = 0;
            for (int guard = 0; guard < 100000; guard++)
            {
                if (off < 0 || off + EntryMin > ret) break;
                IntPtr e = _buf + off;
                var p = new ProcEntry
                {
                    Pid = (int)Marshal.ReadInt64(e, OffPid),
                    CpuTime = Marshal.ReadInt64(e, OffKernelTime) + Marshal.ReadInt64(e, OffUserTime),
                    PrivateBytes = Marshal.ReadInt64(e, OffPrivatePageCount),
                    Handles = Marshal.ReadInt32(e, OffHandles),
                };
                int nameBytes = (ushort)Marshal.ReadInt16(e, OffNameLen);
                long namePtr = (long)Marshal.ReadIntPtr(e, OffNameBuf);
                _names.TryGetValue(p.Pid, out var cached);
                if (namePtr == 0 || nameBytes == 0)
                    p.Name = p.Pid == 4 ? "System" : p.Pid == 0 ? "Idle" : p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                else if (namePtr < baseAddr || namePtr + nameBytes > end)
                    p.Name = cached.Value ?? p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);   // pointeur hors tampon : ne pas le lire
                else
                {
                    // forme courte de .NET (GetProcessShortName) : après le dernier « \ », sans « .exe » final
                    int len = nameBytes / 2;
                    if (_chars.Length < len) _chars = new char[Math.Max(len, _chars.Length * 2)];
                    Marshal.Copy((IntPtr)namePtr, _chars, 0, len);
                    var span = new ReadOnlySpan<char>(_chars, 0, len);
                    span = span.Slice(span.LastIndexOf('\\') + 1);
                    if (span.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) span = span.Slice(0, span.Length - 4);
                    p.Name = cached.Value != null && span.SequenceEqual(cached.Value.AsSpan()) ? cached.Value : new string(span);
                }
                _names[p.Pid] = new KeyValuePair<int, string>(_gen, p.Name);
                list.Add(p);
                int next = Marshal.ReadInt32(e, OffNext);
                if (next <= 0) break;
                off += next;
            }
            // oublie les noms des processus disparus
            _dead.Clear();
            foreach (var kv in _names) if (kv.Value.Key != _gen) _dead.Add(kv.Key);
            foreach (var k in _dead) _names.Remove(k);
            return 0;
        }

        public void Dispose() { if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; } }
    }
}
