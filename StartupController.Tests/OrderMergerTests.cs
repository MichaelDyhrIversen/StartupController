using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // OrderMerger replaces StartupRegistryService.ApplyCustomOrder (2.2): names are matched case-insensitively,
    // never by position. The first five tests are the former ApplyCustomOrder characterization tests, flipped.
    public class OrderMergerTests
    {
        private static string Names(IEnumerable<StartupProgram> programs) => string.Join(",", programs.Select(p => p.Name));

        private static string EnabledNames(IEnumerable<StartupProgram> programs) =>
            string.Join(",", programs.Where(p => p.Enabled).Select(p => p.Name));

        private static StoredOrder Stored(string[] order, params string[] enabled) => StoredOrder.Create(order, enabled);

        private static string Describe(IEnumerable<StartupProgram> programs) =>
            string.Join(",", programs.Select(p => p.Name + (p.Enabled ? "(on)" : "(off)")));

        [Fact]
        public void EmptyStorage_KeepsRegistryOrder_AllDisabled() // was EmptyOrder_KeepsInputOrder_AndFlags
        {
            var result = OrderMerger.Merge(List("B", "A", "C"), StoredOrder.Empty);

            Assert.Equal("B,A,C", Names(result));
            Assert.Equal("", EnabledNames(result));
        }

        [Fact]
        public void StoredOrder_SortsListedNames_AndEnablesStoredEnabled() // was Order_SortsListedNames_AndEnablesThem
        {
            var result = OrderMerger.Merge(List("B", "A", "C"), Stored(new[] { "A", "B" }, "A", "B"));

            Assert.Equal("A,B,C", Names(result));
            Assert.Equal("A,B", EnabledNames(result));
        }

        [Fact]
        public void StoredLongerThanListed_DoesNotThrow() // was OrderLongerThanList_Throws_Current (#2)
        {
            var result = OrderMerger.Merge(List("A", "B"), Stored(new[] { "A", "B", "C" }, "A", "B", "C"));

            Assert.Equal("A(on),B(on)", Describe(result));
        }

        [Fact]
        public void StaleName_DoesNotEnableAnotherProgram() // was StaleName_EnablesWrongProgram_Current (#2)
        {
            var result = OrderMerger.Merge(List("A", "B"), Stored(new[] { "A", "X" }, "A", "X"));

            Assert.Equal("A(on),B(off)", Describe(result));
        }

        [Fact]
        public void CaseMismatch_IsMatched_AndUsesRegistryCasing() // was CaseMismatch_IsNotMatched_ButFirstPositionIsEnabled_Current
        {
            var result = OrderMerger.Merge(List("B", "Spotify"), Stored(new[] { "spotify" }, "SPOTIFY"));

            Assert.Equal("Spotify(on),B(off)", Describe(result));
        }

        [Fact]
        public void MixedOrder_EnabledFlagsFollowStoredSet()
        {
            var result = OrderMerger.Merge(List("C", "B", "A"), Stored(new[] { "A", "B", "C" }, "A", "C"));

            Assert.Equal("A(on),B(off),C(on)", Describe(result));
        }

        [Fact]
        public void EnabledNameNotInOrder_IsNotEnabled()
        {
            var result = OrderMerger.Merge(List("A", "B"), Stored(new[] { "A" }, "B"));

            Assert.Equal("A(off),B(off)", Describe(result));
        }

        [Fact]
        public void KeepsPathAndDescription()
        {
            var listed = new List<StartupProgram> { P("A", path: "\"C:\\a b\\a.exe\" -x") };
            listed[0].Description = "desc";

            var result = Assert.Single(OrderMerger.Merge(listed, Stored(new[] { "a" }, "a")));

            Assert.Equal("\"C:\\a b\\a.exe\" -x", result.Path);
            Assert.Equal("desc", result.Description);
        }

        // --- MergeForSave (D4 placement of stored names that aren't displayed) ---

        [Fact]
        public void Save_HiddenName_StaysAfterItsStoredPredecessor()
        {
            var previous = Stored(new[] { "A", "X", "B" }, "X");
            var displayed = Stored(new[] { "B", "A" }, "B");

            var result = OrderMerger.MergeForSave(displayed, previous);

            Assert.Equal(new[] { "B", "A", "X" }, result.Order);
            Assert.Equal(new[] { "B", "X" }, result.EnabledInOrder());
        }

        [Fact]
        public void Save_HiddenNamesAtTheFront_StayAtTheFrontInOrder()
        {
            var previous = Stored(new[] { "X", "Y", "A" }, "Y");
            var displayed = Stored(new[] { "A", "B" });

            var result = OrderMerger.MergeForSave(displayed, previous);

            Assert.Equal(new[] { "X", "Y", "A", "B" }, result.Order);
            Assert.Equal(new[] { "Y" }, result.EnabledInOrder());
        }

        [Fact]
        public void Save_DisplayedFlagsWin_OverPreviousFlags()
        {
            var previous = Stored(new[] { "A", "B" }, "A");
            var displayed = Stored(new[] { "A", "B" }, "B");

            var result = OrderMerger.MergeForSave(displayed, previous);

            Assert.Equal(new[] { "B" }, result.EnabledInOrder());
        }

        [Fact]
        public void Save_DropsBlankNames_AndCaseInsensitiveDuplicates()
        {
            var previous = Stored(new[] { " ", "a", "X", "x" }, "X");
            var displayed = Stored(new[] { "A", "", "A", "B" }, "A");

            var result = OrderMerger.MergeForSave(displayed, previous);

            Assert.Equal(new[] { "A", "X", "B" }, result.Order);
            Assert.Equal(new[] { "A", "X" }, result.EnabledInOrder());
        }
    }
}
