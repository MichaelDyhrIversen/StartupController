using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // StartupApproved semantics (2.1): a missing value and an even first byte mean Windows runs the entry.
    public class StartupApprovedStateTests
    {
        [Fact]
        public void Null_IsEnabled() // was Null_IsDisabled_Current: Windows runs entries without a value
        {
            Assert.True(StartupApprovedState.IsEnabled(null));
        }

        [Fact]
        public void Empty_IsEnabled() // was Empty_IsDisabled_Current
        {
            Assert.True(StartupApprovedState.IsEnabled(Array.Empty<byte>()));
        }

        [Fact]
        public void AllZero_IsEnabled()
        {
            Assert.True(StartupApprovedState.IsEnabled(new byte[12]));
        }

        [Fact]
        public void First0x02_IsEnabled()
        {
            Assert.True(StartupApprovedState.IsEnabled(Approved(0x02)));
        }

        [Fact]
        public void First0x03_IsDisabled()
        {
            Assert.False(StartupApprovedState.IsEnabled(Approved(0x03)));
        }

        [Fact]
        public void First0x06_IsEnabled() // was First0x06_IsDisabled_Current: even first byte = enabled
        {
            Assert.True(StartupApprovedState.IsEnabled(Approved(0x06)));
        }

        [Theory]
        [InlineData(0x01)]
        [InlineData(0x07)]
        public void OddFirstByte_IsDisabled(byte first)
        {
            Assert.False(StartupApprovedState.IsEnabled(Approved(first)));
        }

        [Fact]
        public void SingleByte0x03_IsDisabled()
        {
            Assert.False(StartupApprovedState.IsEnabled(new byte[] { 0x03 }));
        }

        [Fact]
        public void SingleByte0x02_IsEnabled()
        {
            Assert.True(StartupApprovedState.IsEnabled(new byte[] { 0x02 }));
        }
    }
}
