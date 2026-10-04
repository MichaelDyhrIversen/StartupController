using System.Windows.Forms;
using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Phase 2 review gaps: D4 placement edge cases, coordinator races/failures, CloseReason coverage.
    public sealed class Phase2ReviewTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private static StoredOrder S(string[] order, params string[] enabled) => StoredOrder.Create(order, enabled);

        // --- D4 placement ---

        [Fact]
        public void Save_HiddenNameWhosePredecessorIsAlsoHidden_StaysInChain()
        {
            var previous = S(new[] { "A", "X", "Y", "B" }, "X", "Y");
            var displayed = S(new[] { "B", "A" }, "B");

            var result = OrderMerger.MergeForSave(displayed, previous);

            Assert.Equal(new[] { "B", "A", "X", "Y" }, result.Order);
            Assert.Equal(new[] { "B", "X", "Y" }, result.EnabledInOrder());
        }

        [Fact]
        public void Save_ChainOfHiddenNamesAtTheFront_AndPredecessorCaseDiffers()
        {
            var previous = S(new[] { "X", "Y", "a", "Z" });
            var displayed = S(new[] { "B", "A" });

            var result = OrderMerger.MergeForSave(displayed, previous);

            Assert.Equal(new[] { "X", "Y", "B", "A", "Z" }, result.Order);
        }

        [Fact]
        public void Save_BlankNameInPrevious_DoesNotBreakThePredecessorChain()
        {
            var result = OrderMerger.MergeForSave(S(new[] { "A" }), S(new[] { "A", " ", "X" }));

            Assert.Equal(new[] { "A", "X" }, result.Order);
        }

        [Fact]
        public void Save_IsIdempotent_AndHiddenNamesDoNotGrowOrDuplicateAcrossManySaves()
        {
            var previous = S(new[] { "A", "X", "B", "Y", "C" }, "X", "C");
            var displayed = new[] { "C", "B", "A" };
            var rng = new Random(1234);

            for (int i = 0; i < 200; i++)
            {
                var shuffled = displayed.OrderBy(_ => rng.Next()).ToArray();
                previous = OrderMerger.MergeForSave(S(shuffled, shuffled.Take(2).ToArray()), previous);

                Assert.Equal(5, previous.Order.Count);
                Assert.Equal(5, previous.Order.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Contains("X", previous.Enabled); // hidden flag kept
                Assert.DoesNotContain("Y", previous.Enabled);
            }

            var again = OrderMerger.MergeForSave(S(previous.Order.ToArray(), previous.EnabledInOrder().ToArray()), previous);
            Assert.Equal(previous.Order, again.Order);
            Assert.Equal(previous.EnabledInOrder(), again.EnabledInOrder());
        }

        [Fact]
        public void Save_HiddenNamesKeepRelativeOrder_WhenNeighboursAreReordered_ThroughTheService()
        {
            var service = new StartupRegistryService(_sandbox.Root);
            service.SaveStartupOrder(S(new[] { "A", "X", "Y", "B" }, "A", "X", "Y", "B"));

            for (int i = 0; i < 20; i++)
            {
                var stored = service.LoadStoredOrder();
                var shown = stored.Order.Where(n => n is "A" or "B").Reverse().ToArray();
                service.SaveStartupOrder(S(shown, shown));
            }

            var final = service.LoadStoredOrder();
            Assert.Equal(4, final.Order.Count);
            Assert.Equal(new[] { "X", "Y" }, final.Order.Where(n => n is "X" or "Y").ToArray());
            Assert.Contains("X", final.Enabled);
            Assert.Contains("Y", final.Enabled);
        }

        [Fact]
        public void Migration_ThenFirstSave_KeepsLegacyNamesThatAreNotListed()
        {
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, "A;X;B", RegistryValueKind.String);
            var service = new StartupRegistryService(_sandbox.Root);

            service.SaveStartupOrder(S(new[] { "B", "A" }, "B"));

            var loaded = service.LoadStoredOrder();
            Assert.Equal(new[] { "B", "A", "X" }, loaded.Order);
            Assert.Equal(new[] { "B", "X" }, loaded.EnabledInOrder()); // X was enabled in legacy
            Assert.Equal("A;X;B", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        [Fact]
        public void Save_WritesEnabledFirst_ProgramOrderLast()
        {
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, "A", RegistryValueKind.String);
            var writes = new List<string>();
            var service = new RecordingService(_sandbox.Root, writes);

            service.SaveStartupOrder(S(new[] { "A" }, "A"));

            // D7: EnabledPrograms and EnabledFingerprints first, ProgramOrder last
            Assert.Equal(new[] { RegistrySandbox.EnabledProgramsValue, RegistrySandbox.EnabledFingerprintsValue, RegistrySandbox.ProgramOrderValue }, writes);
        }

        private sealed class RecordingService : StartupRegistryService
        {
            private readonly List<string> _writes;
            public RecordingService(RegistryKey root, List<string> writes) : base(root) => _writes = writes;
            internal override void WriteMultiString(RegistryKey key, string name, string[] values)
            {
                _writes.Add(name);
                base.WriteMultiString(key, name, values);
            }
        }

        [Fact]
        public void OwnEntry_IsSkipped_EvenWithDifferentCase_AndEvenWhenDisabled()
        {
            _sandbox.SeedRun("STARTUPCONTROLLER", @"""C:\x.exe"" --launch");
            _sandbox.SeedApproved("STARTUPCONTROLLER", Approved(0x03));
            _sandbox.SeedRun("B", @"C:\b.exe");
            _sandbox.SeedApproved("B", Approved(0x03));

            var listed = new StartupRegistryService(_sandbox.Root).GetStartupPrograms();

            Assert.Equal("B", Assert.Single(listed).Name);
        }

        // --- Coordinator ---

        private readonly StartupListModel _model = new StartupListModel();
        private readonly FakeOrderStore _store = new FakeOrderStore();
        private readonly FakeNotifier _notifier = new FakeNotifier();

        private OrderSaveCoordinator Coord(ManualRunner? r = null)
        {
            _model.Load(List("A", "B", "C"));
            return new OrderSaveCoordinator(_model, _store, _notifier, r == null ? null : r.Run);
        }

        [Fact]
        public async Task EarlierSaveFails_LaterSucceeds_LatestIsStored_AndListIsClean()
        {
            var runner = new ManualRunner();
            var saver = Coord(runner);
            var first = saver.SaveAsync(false);
            _model.MoveTop(_model.Programs.Single(p => p.Name == "C"));
            var second = saver.SaveAsync(false);

            _store.ThrowOnSave = new IOException("boom");
            runner.Complete(0);
            Assert.False(await first);
            _store.ThrowOnSave = null;
            runner.Complete(1);
            Assert.True(await second);

            Assert.Equal(new[] { "C", "A", "B" }, Assert.Single(_store.Saved).Order);
            Assert.False(_model.IsDirty);
            Assert.Single(_notifier.Messages);
        }

        [Fact]
        public async Task LaterSaveFails_AfterEarlierSucceeded_ListStaysDirty_AndOneNotification()
        {
            var runner = new ManualRunner();
            var saver = Coord(runner);
            var first = saver.SaveAsync(false);
            _model.MoveTop(_model.Programs.Single(p => p.Name == "C"));
            var second = saver.SaveAsync(false);

            runner.Complete(0);
            Assert.True(await first);
            _store.ThrowOnSave = new IOException("boom");
            runner.Complete(1);
            Assert.False(await second);

            Assert.True(_model.IsDirty);
            Assert.Equal(new[] { "A", "B", "C" }, Assert.Single(_store.Saved).Order);
            Assert.Single(_notifier.Messages);
            Assert.False(saver.HasPendingSaves);
        }

        [Fact]
        public async Task RetryAfterFailure_Succeeds_AndClearsDirty()
        {
            var saver = Coord();
            _store.ThrowOnSave = new IOException("boom");
            Assert.False(await saver.SaveAsync(true));
            Assert.DoesNotContain("Order saved!", _notifier.Messages);
            _store.ThrowOnSave = null;

            Assert.True(await saver.SaveAsync(true));

            Assert.False(_model.IsDirty);
            Assert.Contains("Order saved!", _notifier.Messages);
        }

        [Fact]
        public async Task RunnerThatThrows_IsReportedAsFailure_AndNotLeftPending()
        {
            _model.Load(List("A"));
            var saver = new OrderSaveCoordinator(_model, _store, _notifier, _ => throw new InvalidOperationException("no thread"));

            Assert.False(await saver.SaveAsync(false));

            Assert.True(_model.IsDirty);
            Assert.False(saver.HasPendingSaves);
        }

        [Fact]
        public async Task SaveNowFails_ThenAsyncSave_Succeeds()
        {
            var saver = Coord();
            _store.ThrowOnSave = new IOException("x");
            Assert.False(saver.SaveNow(out _));
            _store.ThrowOnSave = null;

            Assert.True(await saver.SaveAsync(false));
            Assert.False(_model.IsDirty);
        }

        [Fact]
        public async Task LoadDuringSave_KeepsListDirty()
        {
            var runner = new ManualRunner();
            var saver = Coord(runner);
            _model.MarkDirty();
            var save = saver.SaveAsync(false);
            _model.Load(List("Z"));
            runner.Complete(0);
            await save;

            Assert.True(_model.IsDirty);
        }

        [Fact]
        public async Task Stress_ManyConcurrentSaves_FinalStoreEqualsFinalSnapshot()
        {
            var saver = Coord();
            var tasks = new List<Task<bool>>();
            for (int i = 0; i < 300; i++)
            {
                var p = _model.Programs[i % 3];
                if (i % 2 == 0) _model.MoveBottom(p); else _model.Toggle(p);
                _model.MarkDirty();
                tasks.Add(saver.SaveAsync(false));
            }
            var expected = _model.Snapshot();

            Assert.All(await Task.WhenAll(tasks), ok => Assert.True(ok));

            var last = _store.Saved[^1];
            Assert.Equal(expected.Order, last.Order);
            Assert.Equal(expected.EnabledInOrder(), last.EnabledInOrder());
            Assert.False(_model.IsDirty);
            Assert.False(saver.HasPendingSaves);
            Assert.Empty(_notifier.Messages);
        }

        [Fact]
        public async Task Stress_SaveNowRacingAsyncSaves_EndsWithLatestSnapshot_NoDeadlock()
        {
            var saver = Coord();
            var tasks = new List<Task<bool>>();
            for (int i = 0; i < 100; i++)
            {
                _model.MoveBottom(_model.Programs[0]);
                tasks.Add(saver.SaveAsync(false));
                if (i % 10 == 0) Assert.True(saver.SaveNow(out _));
            }
            var expected = _model.Snapshot();
            Assert.True(saver.SaveNow(out _));
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(expected.Order, _store.Saved[^1].Order);
        }

        [Fact]
        public async Task WaitForRunningSave_ReturnsImmediatelyWhenIdle()
        {
            var saver = Coord();
            await Task.Run(saver.WaitForRunningSave).WaitAsync(TimeSpan.FromSeconds(5));
        }

        // --- CloseReason ---

        [Theory]
        [InlineData(CloseReason.FormOwnerClosing)]
        [InlineData(CloseReason.MdiFormClosing)]
        public void OwnerAndMdiClosing_Dirty_Prompts_Clean_ClosesSilently(CloseReason reason)
        {
            Assert.Equal(CloseDecision.Prompt, ClosePolicy.Decide(reason, true));
            Assert.Equal(CloseDecision.CloseSilently, ClosePolicy.Decide(reason, false));
        }

        [Fact]
        public void EveryCloseReason_OnlyWindowsShutDownSkipsThePromptWhenDirty()
        {
            foreach (CloseReason reason in Enum.GetValues<CloseReason>())
            {
                var expected = reason == CloseReason.WindowsShutDown ? CloseDecision.CloseSilently : CloseDecision.Prompt;
                Assert.Equal(expected, ClosePolicy.Decide(reason, true));
                Assert.Equal(CloseDecision.CloseSilently, ClosePolicy.Decide(reason, false));
            }
        }
    }
}
