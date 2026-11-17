using System;
using System.Collections.Generic;
using PerfMonitorLive.Metrics;

namespace PerfMonitorLive.Tests
{
    internal static class TestData
    {
        public static Sample FullSample(DateTime t) => new Sample
        {
            Time = t, cpu = 37.5, memMB = 8123, memPct = 51.2, pageIn = 12.5, rx = 1024.5, tx = 20,
            temp = 61.5, gpu = 55, cpuMHz = 4200, gpuMHz = 1850, cpuW = 65.25, gpuW = 120.5, fps = 143.9,
            v12 = 12.052, v5 = 5.01, v33 = 3.3,
            bat = 80, ac = true,
            disks = new List<DiskSample> { new DiskSample { n = "0 C:", pct = 12, r = 1.5, w = 0.25, q = 0.1, lat = 0.8 } },
            procs = new List<ProcSample> { new ProcSample { n = "chrome", cpu = 12.5, mem = 1500, h = 3000, Pid = 42 } },
            fans = new List<FanSample> { new FanSample { n = "CPU", rpm = 900 } },
            stor = new List<StorSample> { new StorSample { n = "Disk0", t = 38, tmax = 70, wear = 3, hours = 1200, health = 0 } },
            volts = new List<VoltSample> { new VoltSample { n = "+12V", v = 12.05 } },
            rb = new RebootInfo { score = 10, up = 5.5, wu = false, cbs = false, pfr = true, commit = 40, hnd = 90 },
        };

        public static Sample MinimalSample(DateTime t) => new Sample { Time = t, cpu = 1, memMB = 2, memPct = 3 };
    }
}
