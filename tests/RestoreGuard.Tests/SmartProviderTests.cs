using RestoreGuard.Providers.Smart;

namespace RestoreGuard.Tests;

public class SmartProviderTests
{
    [Fact]
    public void GoldenFile_AllLab99DisksParseAndPass()
    {
        var disks = SmartProvider.Parse(Fixtures.Read("smartctl-lab99.txt"), "lab99");

        // Live 2026-07-04: 5 SCSI passthrough disks + the PBS-datastore NVMe.
        Assert.Equal(6, disks.Count);
        Assert.All(disks, d => Assert.Equal("lab99", d.Host));
        Assert.All(disks, d => Assert.Equal("PASSED", d.Health));
        Assert.Contains(disks, d => d.Name == "smart /dev/nvme0");
    }

    [Fact]
    public void FailedAndUnparsableDisksAreVisible()
    {
        const string output = """
            ===DEV /dev/sda
            {"smart_status": {"passed": false}}
            ===DEV /dev/sdb
            not json at all
            """;

        var disks = SmartProvider.Parse(output, "lab99");

        Assert.Equal(2, disks.Count);
        Assert.Equal("FAILED", Assert.Single(disks, d => d.Name == "smart /dev/sda").Health);
        Assert.Equal("UNKNOWN", Assert.Single(disks, d => d.Name == "smart /dev/sdb").Health);
    }
}
