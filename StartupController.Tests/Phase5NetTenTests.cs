using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // Phase 5: the .NET 10 move. Guards the settings that must not silently regress and re-checks the
    // platform-sensitive pieces on the new runtime. No registry, no processes.
    public class Phase5NetTenTests
    {
        [Fact]
        public void TestHost_RunsOnDotNet10()
        {
            Assert.Equal(10, Environment.Version.Major);
            Assert.StartsWith(".NET 10.", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, StringComparison.Ordinal);
        }

        [Fact]
        public void Form1_StartupAction_IsHiddenFromDesignerSerialization()
        {
            var property = typeof(Form1).GetProperty("StartupAction", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(property);
            var attribute = property!.GetCustomAttribute<DesignerSerializationVisibilityAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(DesignerSerializationVisibility.Hidden, attribute!.Visibility);
        }

        [Theory]
        [InlineData("StartupController", "StartupController.csproj")]
        [InlineData("StartupController.Tests", "StartupController.Tests.csproj")]
        public void Csproj_TargetsNet10Windows(string directory, string file)
        {
            string text = File.ReadAllText(Path.Combine(SourceScan.RepoRoot(), directory, file));
            var match = Regex.Match(text, @"<TargetFramework>\s*([^<]+?)\s*</TargetFramework>");
            Assert.True(match.Success, "no single <TargetFramework> in " + file);
            Assert.Equal("net10.0-windows", match.Groups[1].Value);
            Assert.DoesNotContain("<TargetFrameworks>", text, StringComparison.Ordinal);
        }

        [Fact]
        public void AssemblyInfo_DeclaresWindows10_14393()
        {
            string text = SourceScan.ReadCode(Path.Combine(SourceScan.RepoRoot(), "StartupController", "Properties", "AssemblyInfo.cs"));
            Assert.Contains("SupportedOSPlatform(\"windows10.0.14393\")", text, StringComparison.Ordinal);

            var attribute = typeof(Form1).Assembly.GetCustomAttribute<SupportedOSPlatformAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal("windows10.0.14393", attribute!.PlatformName);
        }

        // C# 14 may bind string[].Contains(x, comparer) to MemoryExtensions (span); the result must be unchanged
        [Theory]
        [InlineData(@"C:\a\b.exe", true)]
        [InlineData(@"C:\a\B.EXE", true)]
        [InlineData(@"C:\a\b.Lnk", true)]
        [InlineData(@"C:\a\b.cmd.", true)]       // trailing dot trimmed
        [InlineData(@"C:\a\b.exe  ", true)]      // trailing spaces trimmed
        [InlineData(@"C:\a\b.txt", false)]
        [InlineData(@"C:\a\b", false)]
        [InlineData(@"C:\a\b.exex", false)]
        [InlineData("", false)]
        public void HasExecutableExtension_IsCaseInsensitiveAndMatchesTheLinqResult(string path, bool expected)
        {
            Assert.Equal(expected, CommandLineParser.HasExecutableExtension(path));
            string extension = Path.GetExtension(path.TrimEnd('.', ' '));
            Assert.Equal(expected, System.Linq.Enumerable.Contains(CommandLineParser.ExecutableExtensions, extension, StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void HasExecutableExtension_TurkishCultureDoesNotChangeTheAnswer()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                Assert.True(CommandLineParser.HasExecutableExtension(@"C:\a\B.EXE"));
                Assert.True(CommandLineParser.HasExecutableExtension(@"C:\a\b.Lnk"));
                Assert.False(CommandLineParser.HasExecutableExtension(@"C:\a\b.txt"));
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        // The span overload of string[].Contains(x) (Program, LoggingService) is still an exact, case-sensitive match
        [Fact]
        public void ArrayContains_LaunchSwitch_IsOrdinalExact()
        {
            string[] args = { "--launch" };
            Assert.True(args.Contains("--launch"));
            Assert.False(args.Contains("--LAUNCH"));
            Assert.False(new[] { "--launcher" }.Contains("--launch"));
        }

        [Fact]
        public void NormalizePath_TempFile_UsesGetLongPathNameAndStaysSameFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "Some Long File Name.txt");
                File.WriteAllText(file, "x");
                string normalized = PathHelper.NormalizePath(file);
                Assert.Equal(Path.GetFullPath(file), normalized, ignoreCase: true);
                Assert.True(PathHelper.IsSameFile(file, normalized));   // GetFileInformationByHandle
                Assert.False(PathHelper.IsSameFile(file, Path.Combine(dir, "missing.txt")));

                string other = Path.Combine(dir, "other.txt");
                File.WriteAllText(other, "y");
                Assert.False(PathHelper.IsSameFile(file, other));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void NormalizePath_MissingFile_DoesNotThrow()
        {
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.exe");
            Assert.Equal(Path.GetFullPath(missing), PathHelper.NormalizePath(missing), ignoreCase: true);
        }
    }
}
