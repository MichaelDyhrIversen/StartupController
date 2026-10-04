using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // 2.4 AutoSave (serialized, snapshot-based, failure-visible) and the 2.5 model-level SaveNow behaviour.
    public class OrderSaveCoordinatorTests
    {
        private readonly StartupListModel _model = new StartupListModel();
        private readonly FakeOrderStore _store = new FakeOrderStore();
        private readonly FakeNotifier _notifier = new FakeNotifier();

        public OrderSaveCoordinatorTests()
        {
            _model.Load(List("A", "B", "C"));
        }

        private StartupProgram Get(string name) => _model.Programs.Single(p => p.Name == name);

        private OrderSaveCoordinator Coordinator(ManualRunner? runner = null) =>
            new OrderSaveCoordinator(_model, _store, _notifier, runner == null ? null : runner.Run);

        [Fact]
        public async Task LaterRequest_Wins_EvenWhenTheEarlierTaskRunsLast()
        {
            var runner = new ManualRunner();
            var saver = Coordinator(runner);
            var first = saver.SaveAsync(manual: false);    // snapshot A,B,C
            _model.MoveTop(Get("C"));
            var second = saver.SaveAsync(manual: false);   // snapshot C,A,B

            runner.Complete(1);
            runner.Complete(0);
            Assert.True(await second);
            Assert.True(await first);

            var written = Assert.Single(_store.Saved); // the older snapshot was skipped
            Assert.Equal(new[] { "C", "A", "B" }, written.Order);
            Assert.False(_model.IsDirty);
        }

        [Fact]
        public async Task SlowFirstWrite_IsSerialized_AndTheLastWriteIsTheLatestSnapshot()
        {
            using var gate = new ManualResetEventSlim(false);
            _store.BlockNextSave = gate;
            var saver = Coordinator();

            var first = saver.SaveAsync(manual: false);
            Assert.True(_store.SaveStarted.Wait(TimeSpan.FromSeconds(10)));
            _model.MoveTop(Get("C"));
            var second = saver.SaveAsync(manual: false);
            gate.Set();

            Assert.True(await first);
            Assert.True(await second);
            Assert.Equal(new[] { "C", "A", "B" }, _store.Saved[^1].Order);
            Assert.False(_model.IsDirty);
        }

        [Fact]
        public async Task StoreThrows_MarksDirty_NotifiesOnce_AndReportsFailure()
        {
            _store.ThrowOnSave = new UnauthorizedAccessException("denied");
            var saver = Coordinator();

            var ok = await saver.SaveAsync(manual: false);

            Assert.False(ok);
            Assert.True(_model.IsDirty);
            var message = Assert.Single(_notifier.Messages);
            Assert.Contains("denied", message, StringComparison.Ordinal);
            Assert.False(saver.HasPendingSaves);
        }

        [Fact]
        public async Task MutatingAfterRequest_DoesNotAffectWrittenSnapshot_AndKeepsTheListDirty()
        {
            var runner = new ManualRunner();
            var saver = Coordinator(runner);
            _model.Enable(Get("A"));
            _model.MarkDirty();

            var save = saver.SaveAsync(manual: true);
            _model.MoveBottom(Get("A"));
            _model.Disable(Get("A"));
            runner.Complete(0);
            Assert.True(await save);

            var written = Assert.Single(_store.Saved);
            Assert.Equal(new[] { "A", "B", "C" }, written.Order);
            Assert.Equal(new[] { "A" }, written.EnabledInOrder());
            Assert.True(_model.IsDirty); // the change after the snapshot isn't saved yet
        }

        [Fact]
        public async Task DirtyList_FlushedOnce_WhenAutoSaveIsSwitchedOn()
        {
            // Form1: AutoSave off -> MarkDirty; switching AutoSave on with a dirty list -> one SaveAsync
            var saver = Coordinator();
            _model.MoveTop(Get("C"));
            _model.MarkDirty();

            Assert.True(await saver.SaveAsync(manual: false));

            Assert.Single(_store.Saved);
            Assert.False(_model.IsDirty);
        }

        [Fact]
        public async Task AutoSaveSuccess_DoesNotNotify_ManualSaveDoes()
        {
            var saver = Coordinator();

            await saver.SaveAsync(manual: false);
            Assert.Empty(_notifier.Messages);

            await saver.SaveAsync(manual: true);
            Assert.Equal("Order saved!", Assert.Single(_notifier.Messages));
        }

        [Fact]
        public async Task OlderWriteFails_NewerSucceeds_OlderContinuationRunsLast_ListStaysClean()
        {
            // Review item 4: A's write fails, B's write succeeds, and A's continuation runs after B's
            var runner = new ManualRunner();
            var saver = Coordinator(runner);
            var a = saver.SaveAsync(manual: false);        // snapshot A,B,C
            _model.MoveTop(Get("C"));
            var b = saver.SaveAsync(manual: false);        // snapshot C,A,B

            _store.ThrowOnSave = new IOException("A failed");
            runner.Execute(0);                             // A's write throws inside the lock
            _store.ThrowOnSave = null;
            runner.Execute(1);                             // B's write succeeds
            runner.Finish(1);
            Assert.True(await b);
            Assert.False(_model.IsDirty);

            runner.Finish(0);                              // A's failure is delivered last
            Assert.False(await a);

            Assert.Equal(new[] { "C", "A", "B" }, Assert.Single(_store.Saved).Order);
            Assert.False(_model.IsDirty);                  // the latest list is stored: not dirty
            Assert.Empty(_notifier.Messages);              // a superseded failure doesn't warn
        }

        [Fact]
        public async Task HasPendingSaves_WhileAWriteIsQueued()
        {
            var runner = new ManualRunner();
            var saver = Coordinator(runner);

            var save = saver.SaveAsync(manual: false);
            Assert.True(saver.HasPendingSaves);
            runner.Complete(0);
            await save;

            Assert.False(saver.HasPendingSaves);
        }

        // --- 2.5 SaveNow ---

        [Fact]
        public async Task SaveNow_AfterPendingAsyncSave_WritesLatestSnapshot_AndTheOldOneIsSkipped()
        {
            var runner = new ManualRunner();
            var saver = Coordinator(runner);
            var pending = saver.SaveAsync(manual: false);  // A,B,C, not yet run
            _model.MoveTop(Get("C"));
            _model.MarkDirty();

            Assert.True(saver.SaveNow(out var error));
            Assert.Null(error);
            runner.Complete(0);
            await pending;

            var written = Assert.Single(_store.Saved);
            Assert.Equal(new[] { "C", "A", "B" }, written.Order);
            Assert.False(_model.IsDirty);
        }

        [Fact]
        public async Task SaveNow_WaitsForARunningWrite()
        {
            using var gate = new ManualResetEventSlim(false);
            _store.BlockNextSave = gate;
            var saver = Coordinator();
            var running = saver.SaveAsync(manual: false);
            Assert.True(_store.SaveStarted.Wait(TimeSpan.FromSeconds(10)));
            _model.MoveTop(Get("C"));

            var saveNow = Task.Run(() => saver.SaveNow(out _));
            await Task.WhenAny(saveNow, Task.Delay(200));
            Assert.False(saveNow.IsCompleted); // blocked on the lock held by the running write
            gate.Set();

            Assert.True(await saveNow.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await running);
            Assert.Equal(new[] { "C", "A", "B" }, _store.Saved[^1].Order);
            Assert.Equal(2, _store.Saved.Count);
        }

        [Fact]
        public void SaveNow_StoreThrows_ReportsFailure_MarksDirty_WithoutNotification()
        {
            _store.ThrowOnSave = new IOException("disk");
            var saver = Coordinator();

            Assert.False(saver.SaveNow(out var error));

            Assert.Equal("disk", error);
            Assert.True(_model.IsDirty);
            Assert.Empty(_notifier.Messages); // the closing handler shows a MessageBox instead
        }

        [Fact]
        public void Revision_ChangesOnlyWhenAMutatorReportsAChange()
        {
            var before = _model.Revision;

            Assert.False(_model.MoveUp(Get("A")));
            Assert.Equal(before, _model.Revision);

            Assert.True(_model.MoveDown(Get("A")));
            Assert.Equal(before + 1, _model.Revision);
        }
    }
}
