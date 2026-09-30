namespace StartupController.Tests.Infrastructure
{
    /// <summary>In-memory IOrderStore. Records every write; can throw or block inside a write.</summary>
    public sealed class FakeOrderStore : IOrderStore
    {
        private readonly object _lock = new object();
        private readonly List<StoredOrder> _saved = new List<StoredOrder>();

        public List<StoredOrder> Saved
        {
            get { lock (_lock) { return _saved.ToList(); } }
        }

        /// <summary>When set, SaveStartupOrder throws this exception (nothing is recorded).</summary>
        public Exception? ThrowOnSave { get; set; }

        /// <summary>When set, the next write waits for this event before recording (it runs under the coordinator's lock).</summary>
        public ManualResetEventSlim? BlockNextSave { get; set; }

        /// <summary>Set once a blocked write has started waiting.</summary>
        public ManualResetEventSlim SaveStarted { get; } = new ManualResetEventSlim(false);

        public StoredOrder LoadStoredOrder()
        {
            lock (_lock) { return _saved.Count == 0 ? StoredOrder.Empty : _saved[^1]; }
        }

        public void SaveStartupOrder(StoredOrder displayed)
        {
            var gate = BlockNextSave;
            if (gate != null)
            {
                BlockNextSave = null;
                SaveStarted.Set();
                if (!gate.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("FakeOrderStore gate was never released");
            }

            if (ThrowOnSave != null) throw ThrowOnSave;
            lock (_lock) { _saved.Add(displayed); }
        }
    }

    /// <summary>Records notifications instead of showing balloons.</summary>
    public sealed class FakeNotifier : INotifier
    {
        private readonly object _lock = new object();
        private readonly List<string> _messages = new List<string>();

        public List<string> Messages
        {
            get { lock (_lock) { return _messages.ToList(); } }
        }

        public void Notify(string message)
        {
            lock (_lock) { _messages.Add(message); }
        }
    }

    /// <summary>Background runner that holds each action until the test runs it, to control completion order.</summary>
    public sealed class ManualRunner
    {
        private readonly List<(Action Action, TaskCompletionSource Done)> _queued = new List<(Action, TaskCompletionSource)>();

        public int Count => _queued.Count;

        public Task Run(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queued.Add((action, done));
            return done.Task;
        }

        private readonly Dictionary<int, Exception?> _outcomes = new Dictionary<int, Exception?>();

        /// <summary>Runs the queued action at this index and completes (or faults) its task.</summary>
        public void Complete(int index)
        {
            Execute(index);
            Finish(index);
        }

        /// <summary>Runs the queued action but doesn't complete its task yet (the caller's continuation waits).</summary>
        public void Execute(int index)
        {
            try
            {
                _queued[index].Action();
                _outcomes[index] = null;
            }
            catch (Exception ex)
            {
                _outcomes[index] = ex;
            }
        }

        /// <summary>Completes the task of an action already run with Execute, with its result or exception.</summary>
        public void Finish(int index)
        {
            var done = _queued[index].Done;
            if (_outcomes[index] is Exception ex)
                done.SetException(ex);
            else
                done.SetResult();
        }
    }
}
