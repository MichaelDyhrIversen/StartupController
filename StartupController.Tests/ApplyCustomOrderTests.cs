using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Characterization tests for StartupRegistryService.ApplyCustomOrder. Bugs are pinned; 2.2 replaces this logic.
    public class ApplyCustomOrderTests
    {
        private static string Names(IEnumerable<StartupProgram> programs) => string.Join(",", programs.Select(p => p.Name));

        private static string EnabledNames(IEnumerable<StartupProgram> programs) =>
            string.Join(",", programs.Where(p => p.Enabled).Select(p => p.Name));

        [Fact]
        public void EmptyOrder_KeepsInputOrder_AndFlags()
        {
            var input = List("B", "A", "C");

            var result = StartupRegistryService.ApplyCustomOrder(input, Array.Empty<string>());

            Assert.Equal("B,A,C", Names(result));
            Assert.Equal("", EnabledNames(result));
        }

        [Fact]
        public void Order_SortsListedNames_AndEnablesThem()
        {
            var input = List("B", "A", "C");

            var result = StartupRegistryService.ApplyCustomOrder(input, new[] { "A", "B" });

            Assert.Equal("A,B,C", Names(result));
            Assert.Equal("A,B", EnabledNames(result));
        }

        [Fact]
        public void OrderLongerThanList_Throws_Current() // pinned bug (#2), flipped in 2.2
        {
            var input = List("A", "B");

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                StartupRegistryService.ApplyCustomOrder(input, new[] { "A", "B", "C" }));
        }

        [Fact]
        public void StaleName_EnablesWrongProgram_Current() // pinned bug (#2): B gets launched, flipped in 2.2
        {
            var input = List("A", "B");

            var result = StartupRegistryService.ApplyCustomOrder(input, new[] { "A", "X" });

            Assert.Equal("A,B", Names(result));
            Assert.Equal("A,B", EnabledNames(result));
        }

        [Fact]
        public void CaseMismatch_IsNotMatched_ButFirstPositionIsEnabled_Current() // pinned bug (#2), flipped in 2.2
        {
            var input = List("B", "A");

            var result = StartupRegistryService.ApplyCustomOrder(input, new[] { "a" });

            Assert.Equal("B,A", Names(result));
            Assert.Equal("B", EnabledNames(result));
        }
    }
}
