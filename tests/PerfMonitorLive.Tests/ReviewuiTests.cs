using System.Collections.Generic;
using PerfMonitorLive.UI;
using Xunit;

namespace PerfMonitorLive.Tests
{
    /// <summary>Groupe « ui » de la revue : réveil d'une carte sans notification globale.</summary>
    public class ReviewuiTests
    {
        static List<string> Wake(MetricVm vm, int pushes)
        {
            vm.SetLive(false);
            for (int i = 0; i < pushes; i++) vm.Push(i * 10);
            var names = new List<string>();
            vm.PropertyChanged += (s, e) => names.Add(e.PropertyName);
            vm.SetLive(true);
            return names;
        }

        [Fact]
        public void SetLive_true_never_sends_global_notification()
        {
            var names = Wake(new MetricVm("x", "X", "", null, () => { }), 5);
            Assert.NotEmpty(names);   // le rendu recalculé est bien notifié
            Assert.DoesNotContain(null, names);
            Assert.DoesNotContain(string.Empty, names);
            Assert.DoesNotContain("Threshold", names);
            Assert.DoesNotContain("Sustain", names);
            Assert.DoesNotContain("Enabled", names);
            Assert.Contains("AreaGeom", names);
        }

        [Fact]
        public void SetLive_true_notifies_each_name_once_and_nothing_when_clean()
        {
            var vm = new MetricVm("x", "X", "", null, () => { });
            var names = Wake(vm, 3);
            Assert.Equal(names.Count, new HashSet<string>(names).Count);
            var second = Wake(vm, 0);   // rien modifié pendant ce sommeil
            Assert.Empty(second);
        }

        [Fact]
        public void Live_card_still_notifies_directly()
        {
            var vm = new MetricVm("x", "X", "", null, () => { });
            vm.SetLive(true);
            var names = new List<string>();
            vm.PropertyChanged += (s, e) => names.Add(e.PropertyName);
            vm.ValueText = "42";
            Assert.Contains("ValueText", names);
        }
    }
}
