using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    public class StartupListModelTests
    {
        private static StartupListModel Model(params string[] names)
        {
            var model = new StartupListModel();
            model.Load(List(names));
            return model;
        }

        private static StartupProgram Get(StartupListModel m, string name) => m.Programs.Single(p => p.Name == name);

        private static string Names(StartupListModel m) => string.Join(",", m.Programs.Select(p => p.Name));

        [Fact]
        public void MoveUp_SwapsWithPrevious()
        {
            var m = Model("A", "B", "C");

            Assert.True(m.MoveUp(Get(m, "C")));
            Assert.Equal("A,C,B", Names(m));
        }

        [Fact]
        public void MoveDown_SwapsWithNext()
        {
            var m = Model("A", "B", "C");

            Assert.True(m.MoveDown(Get(m, "A")));
            Assert.Equal("B,A,C", Names(m));
        }

        [Theory]
        [InlineData("A", -1)]
        [InlineData("C", 1)]
        public void Move_PastTheEnds_ReturnsFalse_AndKeepsOrder(string name, int direction)
        {
            var m = Model("A", "B", "C");
            var prog = Get(m, name);

            Assert.False(direction < 0 ? m.MoveUp(prog) : m.MoveDown(prog));
            Assert.Equal("A,B,C", Names(m));
        }

        [Fact]
        public void MoveTop_And_MoveBottom()
        {
            var m = Model("A", "B", "C");

            Assert.True(m.MoveTop(Get(m, "C")));
            Assert.Equal("C,A,B", Names(m));
            Assert.False(m.MoveTop(Get(m, "C")));

            Assert.True(m.MoveBottom(Get(m, "C")));
            Assert.Equal("A,B,C", Names(m));
            Assert.False(m.MoveBottom(Get(m, "C")));
        }

        [Fact]
        public void Toggle_Enable_Disable_ChangeFlag()
        {
            var m = Model("A", "B");
            var a = Get(m, "A");
            var b = Get(m, "B");

            Assert.True(m.Toggle(a));
            Assert.True(a.Enabled);
            Assert.True(m.Toggle(a));
            Assert.False(a.Enabled);

            Assert.True(m.Enable(b));
            Assert.True(b.Enabled);
            Assert.True(m.Enable(b)); // reports a change even when already enabled (matches old handler)
            Assert.True(m.Disable(b));
            Assert.False(b.Enabled);
        }

        [Fact]
        public void ProgramNotInList_IsIgnored_EvenWithTheSameName()
        {
            var m = Model("A", "B");
            var stranger = P("A"); // same name, different instance: the model matches instances, not positions or names

            Assert.False(m.MoveUp(stranger));
            Assert.False(m.MoveDown(stranger));
            Assert.False(m.MoveTop(stranger));
            Assert.False(m.MoveBottom(stranger));
            Assert.False(m.Toggle(stranger));
            Assert.False(m.Enable(stranger));
            Assert.False(m.Disable(stranger));
            Assert.False(stranger.Enabled);
            Assert.Equal("A,B", Names(m));
            Assert.Equal(-1, m.IndexOf(stranger));
        }

        [Fact]
        public void Mutators_DoNotChangeIsDirty_OnlyMarkMethodsDo()
        {
            var m = Model("A", "B");

            m.MoveDown(Get(m, "A"));
            m.Toggle(Get(m, "A"));
            Assert.False(m.IsDirty);

            m.MarkDirty();
            m.MoveUp(Get(m, "A"));
            Assert.True(m.IsDirty);

            m.MarkClean();
            Assert.False(m.IsDirty);
        }

        [Fact]
        public void Snapshot_CapturesOrderAndEnabled_AndIsImmutable()
        {
            var m = Model("A", "B", "C");
            m.Enable(Get(m, "A"));
            m.Enable(Get(m, "C"));

            var snapshot = m.Snapshot();
            m.MoveTop(Get(m, "C"));
            m.Disable(Get(m, "A"));

            Assert.Equal(new[] { "A", "B", "C" }, snapshot.Order);
            Assert.Equal(new[] { "A", "C" }, snapshot.EnabledInOrder());
            Assert.Contains("a", snapshot.Enabled); // case-insensitive
        }

        [Fact]
        public void Snapshot_CollectionsCannotBeCastBackToMutable()
        {
            var m = Model("A", "B");
            m.Enable(Get(m, "A"));

            var snapshot = m.Snapshot();

            Assert.False(snapshot.Enabled is ISet<string> set && !set.IsReadOnly);
            Assert.False(snapshot.Enabled is HashSet<string>);
            Assert.False(snapshot.Order is List<string>);
            Assert.False(snapshot.Order is string[]);
            var asList = Assert.IsAssignableFrom<IList<string>>(snapshot.Order);
            Assert.True(asList.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => asList[0] = "X");
        }

        [Fact]
        public void EnabledPrograms_InDisplayOrder()
        {
            var m = Model("A", "B", "C");
            m.Enable(Get(m, "C"));
            m.Enable(Get(m, "A"));
            m.MoveTop(Get(m, "C"));

            Assert.Equal(new[] { "C", "A" }, m.EnabledPrograms().Select(p => p.Name));
        }
    }
}
