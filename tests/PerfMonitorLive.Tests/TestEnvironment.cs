using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

// Les tests partagent un seul BaseDir temporaire (Paths.BaseDir est figé au premier accès) : pas de parallélisme.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PerfMonitorLive.Tests
{
    /// <summary>SÉCURITÉ DES DONNÉES : redirige Paths.BaseDir vers un dossier temporaire unique AVANT tout accès à
    /// <see cref="Paths"/> (initialiseur de module = exécuté au chargement de l'assembly de tests). Sans cela,
    /// Paths remonterait depuis tests\...\bin jusqu'à report.ps1 et écrirait dans le vrai data\ et settings.json.</summary>
    internal static class TestEnvironment
    {
        public static string BaseDir { get; private set; }

        [ModuleInitializer]
        internal static void Init()
        {
            BaseDir = Path.Combine(Path.GetTempPath(), "PerfMonitorLive.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(BaseDir);
            Environment.SetEnvironmentVariable("PERFMON_BASEDIR", BaseDir);
            Environment.SetEnvironmentVariable("PERFMON_BENCH", null);
            AppDomain.CurrentDomain.ProcessExit += (_, __) => { try { Directory.Delete(BaseDir, true); } catch { } };
        }

        /// <summary>Repart d'un état vide : supprime data\ et settings.json du dossier temporaire (jamais autre chose).</summary>
        public static void Reset()
        {
            if (!string.Equals(Path.GetFullPath(Paths.BaseDir), Path.GetFullPath(BaseDir), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Paths.BaseDir ne pointe pas vers le dossier temporaire de test : " + Paths.BaseDir);
            if (Directory.Exists(Paths.DataDir)) Directory.Delete(Paths.DataDir, true);
            if (File.Exists(Paths.SettingsFile)) File.Delete(Paths.SettingsFile);
        }
    }
}
