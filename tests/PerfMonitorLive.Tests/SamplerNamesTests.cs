using PerfMonitorLive.Metrics;
using Xunit;

namespace PerfMonitorLive.Tests
{
    public class SamplerNamesTests
    {
        [Theory]
        [InlineData("0 C:", "0 C:")]
        [InlineData("_Total", "_Total")]
        [InlineData("Intel(R) Ethernet Connection #2", "Intel[R] Ethernet Connection _2")]
        [InlineData("Realtek PCIe GbE Family Controller", "Realtek PCIe GbE Family Controller")]
        [InlineData("a/b\\c", "a_b_c")]
        [InlineData("Microsoft Hyper-V Network Adapter (#3) \\ x/y", "Microsoft Hyper-V Network Adapter [_3] _ x_y")]
        [InlineData("((()))", "[[[]]]")]
        [InlineData("", "")]
        public void ToWmiName_reproduit_les_remplacements_de_WMI(string pdh, string wmi)
        {
            Assert.Equal(wmi, Sampler.ToWmiName(pdh));
        }
    }
}
