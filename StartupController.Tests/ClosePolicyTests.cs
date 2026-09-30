using System.Windows.Forms;

namespace StartupController.Tests
{
    // 2.5 / D5: no unsaved-changes prompt during Windows shutdown or logoff
    public class ClosePolicyTests
    {
        [Theory]
        [InlineData(CloseReason.UserClosing)]
        [InlineData(CloseReason.ApplicationExitCall)]
        [InlineData(CloseReason.TaskManagerClosing)]
        [InlineData(CloseReason.None)]
        public void Dirty_Prompts(CloseReason reason)
        {
            Assert.Equal(CloseDecision.Prompt, ClosePolicy.Decide(reason, isDirty: true));
        }

        [Fact]
        public void WindowsShutDown_Dirty_ClosesSilently()
        {
            Assert.Equal(CloseDecision.CloseSilently, ClosePolicy.Decide(CloseReason.WindowsShutDown, isDirty: true));
        }

        [Theory]
        [InlineData(CloseReason.UserClosing)]
        [InlineData(CloseReason.ApplicationExitCall)]
        [InlineData(CloseReason.WindowsShutDown)]
        [InlineData(CloseReason.TaskManagerClosing)]
        public void Clean_ClosesSilently(CloseReason reason)
        {
            Assert.Equal(CloseDecision.CloseSilently, ClosePolicy.Decide(reason, isDirty: false));
        }
    }
}
