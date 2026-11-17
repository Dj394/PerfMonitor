using System;
using System.IO;
using Xunit;

namespace PerfMonitorLive.Tests
{
    public class PathsTests
    {
        static string FindRepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null) { if (File.Exists(Path.Combine(d.FullName, "report.ps1"))) return d.FullName; d = d.Parent; }
            return null;
        }

        [Fact]
        public void BaseDir_est_le_dossier_temporaire_des_tests()
        {
            Assert.Equal(Path.GetFullPath(TestEnvironment.BaseDir), Path.GetFullPath(Paths.BaseDir));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Paths.BaseDir), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BaseDir_ne_pointe_pas_vers_le_depot()
        {
            var repo = FindRepoRoot();
            Assert.False(File.Exists(Path.Combine(Paths.BaseDir, "report.ps1")), "BaseDir contient report.ps1 : c'est le dépôt réel");
            if (repo == null) return;   // exécution hors du dépôt : rien d'autre à comparer
            var b = Path.GetFullPath(Paths.BaseDir).TrimEnd(Path.DirectorySeparatorChar);
            var r = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar);
            Assert.NotEqual(r, b, StringComparer.OrdinalIgnoreCase);
            Assert.False(b.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase), "BaseDir est sous le dépôt : " + b);
            Assert.False(Path.GetFullPath(Paths.DataDir).StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
            Assert.False(Path.GetFullPath(Paths.SettingsFile).StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Instance_de_test_non_bench()
        {
            Assert.False(Paths.Bench);
            Assert.Equal("", Paths.InstanceSuffix);
        }
    }
}
