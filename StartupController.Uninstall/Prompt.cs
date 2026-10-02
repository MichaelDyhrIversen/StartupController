using System.Runtime.InteropServices;

namespace StartupController.Uninstall
{
    internal enum PromptAnswer
    {
        Yes,
        No,

        // Nobody answered within the timeout: the silent default applies
        TimedOut,

        // The question couldn't be shown: the silent default applies
        Failed
    }

    // The Yes/No question shown during an interactive uninstall. A seam so tests never show a real MessageBox.
    internal interface IPrompt
    {
        PromptAnswer Ask(string text, string caption, TimeSpan timeout);
    }

    internal sealed class MessageBoxPrompt : IPrompt
    {
        private const uint MB_YESNO = 0x00000004;
        private const uint MB_ICONQUESTION = 0x00000020;
        private const uint MB_SETFOREGROUND = 0x00010000;
        private const uint MB_TOPMOST = 0x00040000;
        private const int IDYES = 6;
        private const int IDNO = 7;
        private const int MB_TIMEDOUT = 32000;

        // Exported by user32 since Windows XP but undocumented; returns MB_TIMEDOUT when the time runs out, 0 on failure
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxTimeoutW", SetLastError = true)]
        private static extern int MessageBoxTimeout(IntPtr hWnd, string text, string caption, uint type, ushort languageId, uint milliseconds);

        public PromptAnswer Ask(string text, string caption, TimeSpan timeout)
        {
            int result;
            try
            {
                result = MessageBoxTimeout(IntPtr.Zero, text, caption, MB_YESNO | MB_ICONQUESTION | MB_TOPMOST | MB_SETFOREGROUND,
                    0, (uint)Math.Max(1, timeout.TotalMilliseconds));
            }
            catch (EntryPointNotFoundException)
            {
                return PromptAnswer.Failed;
            }

            switch (result)
            {
                case IDYES: return PromptAnswer.Yes;
                case IDNO: return PromptAnswer.No;
                case MB_TIMEDOUT: return PromptAnswer.TimedOut;
                default: return PromptAnswer.Failed;
            }
        }
    }
}
