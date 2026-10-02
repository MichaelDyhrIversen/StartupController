extern alias Uninstall;

using Uninstall::StartupController.Uninstall;

namespace StartupController.Tests.Infrastructure
{
    /// <summary>Uninstall helper environment for tests: an interactive, impersonated, non-elevated user by default.</summary>
    internal sealed class FakeHelperEnvironment : IHelperEnvironment
    {
        public string UserSid { get; set; } = "S-1-5-21-1000-2000-3000-1001";
        public bool IsSystem { get; set; }
        public bool IsElevated { get; set; }
        public int SessionId { get; set; } = 1;
        public bool UserInteractive { get; set; } = true;
    }

    /// <summary>Records the question instead of showing a MessageBox.</summary>
    internal sealed class FakePrompt : IPrompt
    {
        public Func<PromptAnswer> Answer { get; set; } = () => PromptAnswer.Yes;

        public List<string> Texts { get; } = new List<string>();

        public TimeSpan? Timeout { get; private set; }

        public PromptAnswer Ask(string text, string caption, TimeSpan timeout)
        {
            Texts.Add(text);
            Timeout = timeout;
            return Answer();
        }
    }

    /// <summary>Collects helper log lines as "LEVEL text".</summary>
    internal sealed class FakeUninstallLog : IUninstallLog
    {
        public List<string> Lines { get; } = new List<string>();

        public void Info(string text) => Lines.Add("INFO " + text);
        public void Warning(string text) => Lines.Add("WARN " + text);
        public void Error(string text) => Lines.Add("ERROR " + text);
    }
}
