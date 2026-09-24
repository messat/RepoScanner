using NUnit.Framework;
using RepoScanner.Core.Entities;
using RepoScanner.Core.Enums;

namespace RepoScanner.Core.Tests.Entities
{
    [TestFixture]
    public class FindingTests
    {
        // A problem the scanner has just found is open until it is resolved or suppressed
        [Test]
        public void Status_OnANewFinding_IsOpen()
        {
            Finding finding = new Finding();

            Assert.That(finding.Status, Is.EqualTo(FindingStatus.Open));
        }

        // The Detected event is added to a finding the scanner has just created
        [Test]
        public void Events_OnANewFinding_AcceptsAnEvent()
        {
            Finding finding = new Finding();

            finding.Events.Add(new FindingEvent());

            Assert.That(finding.Events, Has.Count.EqualTo(1));
        }
    }
}
