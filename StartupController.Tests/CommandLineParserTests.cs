namespace StartupController.Tests
{
    // CommandLineParser (3.1, revised for security M1: shortest executable token first, no prefix probing).
    // Every case passes an explicit fileExists, so no test depends on the real file system.
    public class CommandLineParserTests
    {
        private static Func<string, bool> Existing(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        private static readonly Func<string, bool> Nothing = _ => false;

        [Theory]
        [InlineData("\"C:\\a b\\x.exe\" -y", "C:\\a b\\x.exe", "-y")]
        [InlineData("  \"C:\\x.exe\"  ", "C:\\x.exe", "")]
        [InlineData("rundll32.exe shell32.dll,Foo", "rundll32.exe", "shell32.dll,Foo")]
        [InlineData("notepad", "notepad", "")]
        [InlineData("notepad file.txt", "notepad", "file.txt")]
        [InlineData("C:\\Tools\\run.CMD /q", "C:\\Tools\\run.CMD", "/q")]
        [InlineData("C:\\x.exe.d\\app.exe", "C:\\x.exe.d\\app.exe", "")]
        [InlineData("C:\\a b\\x.exe -y", "C:\\a b\\x.exe", "-y")]
        [InlineData("C:\\Links\\app.lnk --x", "C:\\Links\\app.lnk", "--x")]
        public void Split_WhenNothingExists(string command, string expectedExe, string expectedArgs)
        {
            var (exe, args) = CommandLineParser.Split(command, Nothing);

            Assert.Equal(expectedExe, exe);
            Assert.Equal(expectedArgs, args);
        }

        [Fact]
        public void Unquoted_ProgramFiles_ResolvesToTheFirstExecutableToken()
        {
            var (exe, args) = CommandLineParser.Split(@"C:\Program Files\x.exe -y", Existing(@"C:\Program Files\x.exe"));

            Assert.Equal(@"C:\Program Files\x.exe", exe);
            Assert.Equal("-y", args);
        }

        [Fact]
        public void Unquoted_FirstExecutableToken_NotTheProgramExeHijack()
        {
            // Both exist: C:\Program.exe must not win over the intended file
            var exists = Existing(@"C:\Program.exe", @"C:\Program Files\x.exe");

            var parsed = CommandLineParser.Parse(@"C:\Program Files\x.exe -y", exists, expandEnvironment: false);

            Assert.Equal(@"C:\Program Files\x.exe", parsed.ExePath);
            Assert.Equal("-y", parsed.Arguments);
            Assert.True(parsed.UnquotedWithSpaces);
        }

        [Fact]
        public void Unquoted_OnlyProgramExeExists_IsNeverChosen() // was Unquoted_OnlyShortPrefixExists_ResolvesToIt (M1)
        {
            var (exe, args) = CommandLineParser.Split(@"C:\Program Files\x.exe -y", Existing(@"C:\Program.exe"));

            Assert.Equal(@"C:\Program Files\x.exe", exe); // missing: the launcher reports NotFound
            Assert.Equal("-y", args);
        }

        [Fact]
        public void Unquoted_PlantedFileNamedLikeTheWholeCommand_IsNotChosen() // M1
        {
            var exists = Existing(@"C:\ProgramData\Vendor\app.exe --load x.js", @"C:\ProgramData\Vendor\app.exe");

            var (exe, args) = CommandLineParser.Split(@"C:\ProgramData\Vendor\app.exe --load x.js", exists);

            Assert.Equal(@"C:\ProgramData\Vendor\app.exe", exe);
            Assert.Equal("--load x.js", args);
        }

        [Fact]
        public void Unquoted_PlantedShortExe_TargetMissing_IsNotChosen() // M1: no ".exe" probe on intermediate prefixes
        {
            var probed = new List<string>();

            var (exe, _) = CommandLineParser.Split(@"C:\ProgramData\My App\a.exe -x", p => { probed.Add(p); return p == @"C:\ProgramData\My.exe"; });

            Assert.Equal(@"C:\ProgramData\My App\a.exe", exe);
            Assert.Empty(probed);
        }

        [Fact]
        public void Unquoted_ProgramFilesWithTwoSpaces_PlantedProgramExe_IsNotChosen() // M1
        {
            var (exe, args) = CommandLineParser.Split(@"C:\Program Files\X Y\a.exe arg", Existing(@"C:\Program.exe"));

            Assert.Equal(@"C:\Program Files\X Y\a.exe", exe);
            Assert.Equal("arg", args);
        }

        [Fact]
        public void ExeInDirectoryName_ShortestExecutableTokenWins() // was LazyRegex_IsGone_LongestExistingPathWins; M1 by design
        {
            // Quote the path to run C:\My.exe Tools\app.exe; unquoted, the first executable token is the exe
            var (exe, args) = CommandLineParser.Split(@"C:\My.exe Tools\app.exe -y", Existing(@"C:\My.exe Tools\app.exe"));

            Assert.Equal(@"C:\My.exe", exe);
            Assert.Equal(@"Tools\app.exe -y", args);
        }

        [Fact]
        public void DotExeInsideADirectoryName_IsNotCut()
        {
            var (exe, args) = CommandLineParser.Split(@"C:\x.exe.d\app.exe", Existing(@"C:\x.exe.d\app.exe"));

            Assert.Equal(@"C:\x.exe.d\app.exe", exe);
            Assert.Equal("", args);
        }

        [Fact]
        public void UnmatchedQuote_DropsTheQuote() // was UnmatchedQuote_KeepsQuoteInExe_Current (#7)
        {
            var (exe, args) = CommandLineParser.Split("\"C:\\a b.exe", Nothing);

            Assert.Equal(@"C:\a b.exe", exe);
            Assert.Equal("", args);
        }

        [Fact]
        public void UnmatchedQuote_WithArgs_UsesTheFirstExecutableToken()
        {
            var (exe, args) = CommandLineParser.Split("\"C:\\a b.exe -y", Existing(@"C:\a b.exe"));

            Assert.Equal(@"C:\a b.exe", exe);
            Assert.Equal("-y", args);
        }

        [Fact]
        public void UnmatchedQuote_NoExecutableToken_KeepsTheWholeRest()
        {
            var (exe, args) = CommandLineParser.Split("\"C:\\a b\\tool", Nothing);

            Assert.Equal(@"C:\a b\tool", exe);
            Assert.Equal("", args);
        }

        [Fact]
        public void EnvironmentVariables_AreExpanded() // was EnvironmentVariables_AreNotExpanded_Current
        {
            var expected = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%") + @"\x.exe";
            Assert.DoesNotContain("%", expected);

            var (exe, _) = CommandLineParser.Split(@"%LOCALAPPDATA%\x.exe", Nothing);

            Assert.Equal(expected, exe);
        }

        [Fact]
        public void EnvironmentVariables_WithoutSeparator_AreExpanded()
        {
            var (exe, _) = CommandLineParser.Split("%LOCALAPPDATA%x.exe", Nothing);

            Assert.Equal(Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%") + "x.exe", exe);
        }

        [Fact]
        public void AlreadyExpandedPath_IsNotExpandedAgain()
        {
            var parsed = CommandLineParser.Parse(@"C:\Apps\%LOCALAPPDATA%\x.exe", Nothing, expandEnvironment: false);

            Assert.Equal(@"C:\Apps\%LOCALAPPDATA%\x.exe", parsed.ExePath);
        }

        [Fact]
        public void QuotedPathWithoutExtension_GetsExeWhenOnlyThatExists()
        {
            var (exe, args) = CommandLineParser.Split("\"C:\\a b\\tool\" -q", Existing(@"C:\a b\tool.exe"));

            Assert.Equal(@"C:\a b\tool.exe", exe);
            Assert.Equal("-q", args);
        }

        [Theory]
        [InlineData("\"sub\\tool\" -q")]
        [InlineData("\"..\\tool\" -q")]
        [InlineData("\"C:tool\" -q")]
        [InlineData("\"\\tool\" -q")]
        [InlineData("\"tool\" -q")]
        public void QuotedRelativePath_IsNeverProbed(string command) // L1: no current-directory resolution
        {
            var probed = new List<string>();

            CommandLineParser.Split(command, p => { probed.Add(p); return true; });

            Assert.Empty(probed);
        }

        [Fact]
        public void QuotedPathEndingInADot_IsNeverProbedWithExeAppended()
        {
            var probed = new List<string>();

            var (exe, _) = CommandLineParser.Split("\"C:\\x\\tool.\"", p => { probed.Add(p); return true; });

            Assert.Equal(@"C:\x\tool.", exe);
            Assert.Empty(probed);
        }

        [Fact]
        public void UnquotedWithoutExecutableToken_ExeProbeOnlyOnTheWholeString()
        {
            var probed = new List<string>();

            var (exe, args) = CommandLineParser.Split(@"C:\Program Files\App\tool -q", p => { probed.Add(p); return p == @"C:\Program Files\App\tool.exe"; });

            // Intermediate prefixes are never probed, so the extensionless tool with arguments isn't resolved.
            // ".exe" is probed first (CreateProcess order), and the path is kept whole so it ends up NotFound (L-C).
            Assert.Equal(new[] { @"C:\Program Files\App\tool -q.exe", @"C:\Program Files\App\tool -q" }, probed);
            Assert.Equal(@"C:\Program Files\App\tool -q", exe);
            Assert.Equal("", args);
        }

        [Fact]
        public void UnquotedWholeStringWithoutExtension_GetsExe() // was UnquotedPrefixWithoutExtension_UsesTheExeProbe
        {
            var (exe, args) = CommandLineParser.Split(@"C:\Program Files\App\tool", Existing(@"C:\Program Files\App\tool.exe"));

            Assert.Equal(@"C:\Program Files\App\tool.exe", exe);
            Assert.Equal("", args);
        }

        [Fact]
        public void RelativeExecutableToken_IsChosenWithoutProbing()
        {
            var probed = new List<string>();

            var (exe, args) = CommandLineParser.Split("my tool.exe -y", p => { probed.Add(p); return true; });

            Assert.Empty(probed);
            Assert.Equal("my tool.exe", exe); // not a full path: the launcher reports NotFound
            Assert.Equal("-y", args);
        }

        [Fact]
        public void UnquotedExecutableToken_IsChosenWithoutAnyProbe() // was FileExists_IsConsulted_ForUnquotedPathsWithSpaces
        {
            var probed = new List<string>();

            CommandLineParser.Split(@"C:\a b\x.exe -y", p => { probed.Add(p); return false; });

            Assert.Empty(probed);
        }

        [Fact]
        public void TrailingDotAfterAnExecutableExtension_StillEndsTheExe()
        {
            var (exe, args) = CommandLineParser.Split(@"C:\x\app.exe. -y", Nothing);

            Assert.Equal(@"C:\x\app.exe.", exe); // the launcher rejects the trailing dot
            Assert.Equal("-y", args);
        }

        [Fact]
        public void Probes_AreCapped()
        {
            int probes = 0;

            CommandLineParser.Split(@"\\srv\share\a " + string.Join(" ", Enumerable.Repeat("b", 5000)), _ => { probes++; return false; });

            Assert.InRange(probes, 0, CommandLineParser.MaxProbes);
        }

        [Fact]
        public void FileExistsOverload_RejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => CommandLineParser.Split("x.exe", null!));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        [InlineData("\"")]
        public void EmptyOrWhitespace_ReturnsEmpty(string command)
        {
            var (exe, args) = CommandLineParser.Split(command, Nothing);

            Assert.Equal("", exe);
            Assert.Equal("", args);
        }
    }
}
