using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    public class LoggingServiceTests
    {
        [Fact]
        public void Logs_GoToTheTestDirectory()
        {
            var marker = "marker-" + Guid.NewGuid().ToString("N");

            LoggingService.LogInfo(marker);

            Assert.StartsWith(TestLogSetup.LogDirectory, LoggingService.LogFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(marker, File.ReadAllText(LoggingService.LogFilePath), StringComparison.Ordinal);
        }
    }
}
