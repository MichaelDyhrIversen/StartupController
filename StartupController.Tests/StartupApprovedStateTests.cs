using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Characterization tests: pin the CURRENT rules. Phase 2.1 flips the marked cases.
    public class StartupApprovedStateTests
    {
        [Fact]
        public void Null_IsDisabled_Current() // flipped in 2.1 (Windows treats a missing value as enabled)
        {
            Assert.False(StartupApprovedState.IsEnabled(null));
        }

        [Fact]
        public void Empty_IsDisabled_Current() // flipped in 2.1
        {
            Assert.False(StartupApprovedState.IsEnabled(Array.Empty<byte>()));
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
        public void First0x06_IsDisabled_Current() // flipped in 2.1 (even first byte = enabled)
        {
            Assert.False(StartupApprovedState.IsEnabled(Approved(0x06)));
        }

        [Fact]
        public void SingleByte0x03_IsDisabled()
        {
            Assert.False(StartupApprovedState.IsEnabled(new byte[] { 0x03 }));
        }
    }
}
