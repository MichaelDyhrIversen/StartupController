namespace StartupController.Tests
{
    // Characterization table for CommandLineParser.Split (moved verbatim from Form1.SplitCommand). 3.1 rewrites it.
    public class CommandLineParserTests
    {
        [Theory]
        [InlineData("\"C:\\a b\\x.exe\" -y", "C:\\a b\\x.exe", "-y")]
        [InlineData("  \"C:\\x.exe\"  ", "C:\\x.exe", "")]
        [InlineData("C:\\a b\\x.exe -y", "C:\\a b\\x.exe", "-y")]
        [InlineData("C:\\x.exe.d\\app.exe", "C:\\x.exe.d\\app.exe", "")]
        [InlineData("rundll32.exe shell32.dll,Foo", "rundll32.exe", "shell32.dll,Foo")]
        [InlineData("notepad", "notepad", "")]
        [InlineData("notepad file.txt", "notepad", "file.txt")]
        [InlineData("C:\\Tools\\run.CMD /q", "C:\\Tools\\run.CMD", "/q")]
        public void Split_CurrentBehaviour(string command, string expectedExe, string expectedArgs)
        {
            var (exe, args) = CommandLineParser.Split(command);

            Assert.Equal(expectedExe, exe);
            Assert.Equal(expectedArgs, args);
        }

        [Fact]
        public void LazyRegex_StopsAtFirstExtensionFollowedBySpace_Current() // pinned bug (#7), flipped in 3.1
        {
            var (exe, args) = CommandLineParser.Split("C:\\My.exe Tools\\app.exe -y");

            Assert.Equal("C:\\My.exe", exe);
            Assert.Equal("Tools\\app.exe -y", args);
        }

        [Fact]
        public void UnmatchedQuote_KeepsQuoteInExe_Current() // pinned bug (#7), flipped in 3.1
        {
            var (exe, args) = CommandLineParser.Split("\"C:\\a b.exe");

            Assert.Equal("\"C:\\a b.exe", exe);
            Assert.Equal("", args);
        }

        [Fact]
        public void EnvironmentVariables_AreNotExpanded_Current() // 3.1 adds expansion
        {
            var (exe, _) = CommandLineParser.Split("%LOCALAPPDATA%\\x.exe");

            Assert.Equal("%LOCALAPPDATA%\\x.exe", exe);
        }

        [Theory]
        [InlineData("\"C:\\a b\\x.exe\" -y")]
        [InlineData("C:\\a b\\x.exe -y")]
        [InlineData("C:\\My.exe Tools\\app.exe -y")]
        [InlineData("notepad file.txt")]
        [InlineData("")]
        public void FileExistsOverload_DoesNotChangeParsing_Current(string command) // 3.1 starts consulting fileExists
        {
            var probed = new List<string>();
            var withTrue = CommandLineParser.Split(command, p => { probed.Add(p); return true; });
            var withFalse = CommandLineParser.Split(command, p => { probed.Add(p); return false; });

            Assert.Equal(CommandLineParser.Split(command), withTrue);
            Assert.Equal(withTrue, withFalse);
            Assert.Empty(probed);
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
        public void EmptyOrWhitespace_ReturnsEmpty(string command)
        {
            var (exe, args) = CommandLineParser.Split(command);

            Assert.Equal("", exe);
            Assert.Equal("", args);
        }
    }
}
