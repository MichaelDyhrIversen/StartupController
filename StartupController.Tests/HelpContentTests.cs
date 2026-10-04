using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // In-app help: the Help.md parser, the embedded content and the Help window's source guards
    public class HelpContentTests
    {
        private static string HelpMarkdown()
        {
            return File.ReadAllText(Path.Combine(SourceScan.ProductionSourceDirectory(), "Help", "Help.md"), System.Text.Encoding.UTF8);
        }

        private static string AllText(IEnumerable<HelpSection> sections) =>
            string.Join("\n", sections.Select(s => s.Title + "\n" + s.Body));

        // ---------- Parse ----------

        [Fact]
        public void Parse_SplitsSections_AndIgnoresTextBeforeTheFirstHeading()
        {
            var sections = HelpContent.Parse("intro text\n<!-- note -->\n## One\nfirst\n## Two\nsecond\n");

            Assert.Equal(new[] { "One", "Two" }, sections.Select(s => s.Title));
            Assert.Equal("first", sections[0].Body);
            Assert.Equal("second", sections[1].Body);
        }

        [Fact]
        public void Parse_SkipsCommentLines_ConvertsBullets_AndKeepsLevel3HeadingsAsText()
        {
            var section = Assert.Single(HelpContent.Parse("## T\n<!-- hidden -->\n- item\n### not a section\n"));

            Assert.Equal("\u2022 item\n### not a section", section.Body);
        }

        [Fact]
        public void Parse_CollapsesBlankLines_AndTrimsLeadingAndTrailingOnes()
        {
            var section = Assert.Single(HelpContent.Parse("## T\n\n\na\n\n\n\nb\n\n\n"));

            Assert.Equal("a\n\nb", section.Body);
        }

        [Fact]
        public void Parse_CrLfAndLf_GiveTheSameResult()
        {
            const string text = "## A\nline 1\n- bullet\n\nline 2\n## B\n";

            Assert.Equal(HelpContent.Parse(text), HelpContent.Parse(text.Replace("\n", "\r\n", StringComparison.Ordinal)));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("no heading at all\n- bullet")]
        public void Parse_NoSections_GivesAnEmptyList(string? text)
        {
            Assert.Empty(HelpContent.Parse(text));
        }

        [Fact]
        public void Parse_HeadingWithoutBody_GivesAnEmptyBody()
        {
            var sections = HelpContent.Parse("## Empty\n## Next\ntext");

            Assert.Equal("", sections[0].Body);
            Assert.Equal("text", sections[1].Body);
        }

        // ---------- embedded content ----------

        [Fact]
        public void LoadEmbedded_HasAtLeastEightUniqueSections()
        {
            var sections = HelpContent.LoadEmbedded();

            Assert.True(sections.Count >= 8, $"only {sections.Count} sections");
            Assert.Equal(sections.Count, sections.Select(s => s.Title).Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(sections, s => s.Body.Length == 0);
            Assert.DoesNotContain(HelpContent.MissingText, AllText(sections), StringComparison.Ordinal);
        }

        [Fact]
        public void LoadEmbedded_KeepsTheArrowsAndDashes_AfterDecoding()
        {
            var text = AllText(HelpContent.LoadEmbedded());

            foreach (var code in new[] { 0x2191, 0x2193, 0x21C8, 0x21CA, 0x2013 })
                Assert.Contains(((char)code).ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain("\uFFFD", text, StringComparison.Ordinal);
        }

        [Fact]
        public void HelpMd_IsSavedWithAUtf8Bom()
        {
            var bytes = File.ReadAllBytes(Path.Combine(SourceScan.ProductionSourceDirectory(), "Help", "Help.md"));

            Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }

        [Fact]
        public void HelpMd_UsesOnlyTheTinyFormat_AndHasNoLinks()
        {
            var text = HelpMarkdown();

            foreach (var forbidden in new[] { "**", "`", "](", "http://", "https://" })
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        [Fact]
        public void HelpMd_NoLongerSaysEveryProgramInTheRunKeyIsListed()
        {
            Assert.DoesNotContain("Every program in your Run key", HelpMarkdown(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Load_MissingResource_GivesAPlaceholderSection()
        {
            var section = Assert.Single(HelpContent.Load(typeof(HelpContent).Assembly, "StartupController.NoSuchHelp.md"));

            Assert.Equal(HelpContent.MissingText, section.Body);
        }

        // ---------- About and Search ----------

        [Fact]
        public void About_ContainsTheVersionAndTheLogFolder()
        {
            var about = HelpContent.About("9.8.7", @"C:\Temp\logs");

            Assert.Equal("About", about.Title);
            Assert.Contains("9.8.7", about.Body, StringComparison.Ordinal);
            Assert.Contains(@"C:\Temp\logs", about.Body, StringComparison.Ordinal);
        }

        [Fact]
        public void CurrentVersion_IsTheInformationalVersion()
        {
            Assert.Matches(@"^\d+\.\d+\.\d+", HelpContent.CurrentVersion());
        }

        [Fact]
        public void Search_MatchesTitleAndBody_IgnoringCase()
        {
            var sections = new[] { new HelpSection("Order", "arrows"), new HelpSection("Logs", "View Logs"), new HelpSection("Other", "x") };

            Assert.Equal(new[] { 0 }, HelpContent.Search(sections, "ORDER"));
            Assert.Equal(new[] { 1 }, HelpContent.Search(sections, "view logs"));
            Assert.Equal(new[] { 0, 1, 2 }, HelpContent.Search(sections, ""));
            Assert.Equal(new[] { 0, 1, 2 }, HelpContent.Search(sections, "  "));
            Assert.Empty(HelpContent.Search(sections, "nothing like this"));
        }

        // ---------- source guards ----------

        [Theory]
        [InlineData("HelpForm.cs")]
        [InlineData("LogViewerForm.cs")]
        public void ChildWindows_NeverUseRtf_LinksOrProcessStart(string fileName)
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), fileName));

            Assert.DoesNotMatch(@"\.\s*Rtf\s*=", code);
            Assert.DoesNotMatch(@"\bProcess\s*\.\s*Start\b", code);
            Assert.DoesNotContain("LinkClicked", code, StringComparison.Ordinal);
            if (code.Contains("RichTextBox", StringComparison.Ordinal))
                Assert.Contains("DetectUrls = false", code, StringComparison.Ordinal);
        }

        [Fact]
        public void Form1_NoLongerShowsHelpInAMessageBox()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Form1.cs"));

            Assert.DoesNotContain("HelpText", code, StringComparison.Ordinal);
            Assert.Contains("new HelpForm(", code, StringComparison.Ordinal);
            Assert.Contains("new LogViewerForm(", code, StringComparison.Ordinal);
        }
    }
}
