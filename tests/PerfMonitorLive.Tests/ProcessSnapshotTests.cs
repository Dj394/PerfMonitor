using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    /// <summary>Test d'intégration Windows : NtQuerySystemInformation vs System.Diagnostics.Process sur la machine de test.</summary>
    public class ProcessSnapshotTests
    {
        [Fact]
        public void Supporte_en_64_bits()
        {
            Assert.True(ProcessSnapshot.Supported);
        }

        [Fact]
        public void Pids_et_noms_concordent_avec_System_Diagnostics_Process()
        {
            using var snap = new ProcessSnapshot();
            var entries = new List<ProcEntry>();
            Assert.Equal(0, snap.Read(entries));
            Assert.NotEmpty(entries);

            var byPid = entries.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());
            Assert.Equal(entries.Count, byPid.Count);   // PID uniques

            int compared = 0; var mismatches = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string name; int pid;
                    try { pid = p.Id; name = p.ProcessName; } catch { continue; }   // disparu entre-temps
                    if (!byPid.TryGetValue(pid, out var e)) continue;                // apparu après le snapshot
                    compared++;
                    if (!string.Equals(e.Name, name, StringComparison.Ordinal)) mismatches.Add(pid + ": '" + e.Name + "' != '" + name + "'");
                }
            }
            Assert.True(compared >= 10, "Trop peu de processus comparés : " + compared);
            Assert.True(mismatches.Count == 0, "Noms différents : " + string.Join(" | ", mismatches.Take(10)));
        }

        [Fact]
        public void Processus_courant_present_avec_des_mesures_coherentes()
        {
            using var snap = new ProcessSnapshot();
            var entries = new List<ProcEntry>();
            Assert.Equal(0, snap.Read(entries));
            using var me = Process.GetCurrentProcess();
            var e = entries.Single(x => x.Pid == me.Id);
            Assert.Equal(me.ProcessName, e.Name);
            Assert.True(e.Handles > 0);
            Assert.True(e.PrivateBytes > 1_000_000);
            Assert.InRange(e.PrivateBytes / (double)me.PrivateMemorySize64, 0.5, 2.0);
            me.Refresh();
            Assert.InRange(Math.Abs(e.CpuTime / 1e7 - me.TotalProcessorTime.TotalSeconds), 0, 3);
            Assert.Contains(entries, x => x.Pid == 0);   // Idle inclus
            Assert.Contains(entries, x => x.Pid == 4 && x.Name == "System");
        }

        [Fact]
        public void Lectures_successives_reutilisent_les_noms_sans_les_changer()
        {
            using var snap = new ProcessSnapshot();
            var a = new List<ProcEntry>(); var b = new List<ProcEntry>();
            Assert.Equal(0, snap.Read(a));
            Assert.Equal(0, snap.Read(b));
            int me = Environment.ProcessId;
            Assert.Equal(a.Single(x => x.Pid == me).Name, b.Single(x => x.Pid == me).Name);
            Assert.Same(a.Single(x => x.Pid == me).Name, b.Single(x => x.Pid == me).Name);   // même instance de chaîne (pas de réallocation)
        }
    }
}
