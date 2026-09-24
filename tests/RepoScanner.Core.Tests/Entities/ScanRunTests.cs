using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Tests.Entities
{
    [TestFixture]
    public class ScanRunTests
    {
        // A scan run is created when a scan starts, so until it is completed it is Running (task #33 relies on this)
        [Test]
        public void Status_OnANewScanRun_IsRunning()
        {
            ScanRun scanRun = new ScanRun();

            Assert.That(scanRun.Status, Is.EqualTo(ScanRunStatus.Running));
        }

        // Each scanner's result is added to the run as it finishes
        [Test]
        public void ScannerResults_OnANewScanRun_AcceptsAResult()
        {
            ScanRun scanRun = new ScanRun();

            scanRun.ScannerResults.Add(new ScannerResult());

            Assert.That(scanRun.ScannerResults, Has.Count.EqualTo(1));
        }
    }
}
