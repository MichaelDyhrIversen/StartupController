namespace StartupController.Tests
{
    // 3.2 singleton activation. Every test uses its own test-only event name, never the app's real one.
    public class InstanceActivationTests
    {
        private static string TestEventName() => @"Local\StartupController.Tests.Activate." + Guid.NewGuid().ToString("N");

        [Fact]
        public void Signal_ReachesTheListener()
        {
            var name = TestEventName();
            using var activated = new ManualResetEventSlim(false);
            using var activation = new InstanceActivation(name, activated.Set);

            Assert.True(InstanceActivation.SignalExisting(name));

            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void EverySignal_ActivatesAgain()
        {
            var name = TestEventName();
            using var twice = new CountdownEvent(2);
            using var activation = new InstanceActivation(name, () => twice.Signal(), TimeSpan.Zero);

            Assert.True(InstanceActivation.SignalExisting(name));
            SpinWait.SpinUntil(() => twice.CurrentCount == 1, TimeSpan.FromSeconds(5));
            Assert.True(InstanceActivation.SignalExisting(name));

            Assert.True(twice.Wait(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void Signal_WithoutARunningInstance_ReturnsFalse()
        {
            Assert.False(InstanceActivation.SignalExisting(TestEventName()));
        }

        [Fact]
        public void AfterDispose_NoOneListens()
        {
            var name = TestEventName();
            new InstanceActivation(name, () => { }).Dispose();

            Assert.False(InstanceActivation.SignalExisting(name));
        }

        [Fact]
        public void CallbackThrows_ListenerKeepsRunning()
        {
            var name = TestEventName();
            int calls = 0;
            using var second = new ManualResetEventSlim(false);
            using var activation = new InstanceActivation(name, () =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("form gone");
                second.Set();
            }, TimeSpan.Zero);

            InstanceActivation.SignalExisting(name);
            SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(5));
            InstanceActivation.SignalExisting(name);

            Assert.True(second.Wait(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void DefaultEventName_IsSessionLocal()
        {
            Assert.StartsWith(@"Local\", InstanceActivation.DefaultEventName, StringComparison.Ordinal);
        }
    }
}
