namespace StartupController
{
    // Shows a user-visible message (the tray balloon in the app)
    public interface INotifier
    {
        void Notify(string message);
    }

    // Serializes saves of the launch list.
    // - The snapshot is taken on the calling (UI) thread; only the registry write runs in the background.
    // - Writes run under _saveLock, and a write is skipped when a newer snapshot has already been written,
    //   so the latest request wins whatever order the background tasks run in.
    // - A failed save marks the list dirty, logs and notifies once, unless a newer snapshot has been written
    //   successfully by the time its continuation runs. Only manual saves notify on success.
    // Call SaveAsync/SaveNow from the UI thread: the model is updated after the write, on the caller's context.
    public sealed class OrderSaveCoordinator
    {
        private readonly StartupListModel _model;
        private readonly IOrderStore _store;
        private readonly INotifier _notifier;
        private readonly Func<Action, Task> _runInBackground;

        // Only registry work happens under this lock (no UI marshalling), so the UI thread can wait on it
        private readonly object _saveLock = new object();
        private long _version;            // last version handed out
        private long _lastWrittenVersion; // written under _saveLock, read with Interlocked.Read
        private int _pending;             // SaveAsync calls whose write hasn't finished

        public OrderSaveCoordinator(StartupListModel model, IOrderStore store, INotifier notifier, Func<Action, Task>? runInBackground = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            _runInBackground = runInBackground ?? (action => Task.Run(action));
        }

        public bool HasPendingSaves => Volatile.Read(ref _pending) > 0;

        // Returns false when the write failed; the failure is already logged, notified and the list marked dirty.
        public async Task<bool> SaveAsync(bool manual)
        {
            var request = NewRequest();
            Interlocked.Increment(ref _pending);
            try
            {
                await _runInBackground(() => Write(request));
            }
            catch (Exception ex)
            {
                OnFailure(request, ex, notify: true);
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }

            OnSuccess(request, manual ? "Order saved" : "Order autosaved");
            if (manual)
                _notifier.Notify("Order saved!");
            return true;
        }

        // Synchronous save of the current list (used while closing). Waits for a running write first.
        // Doesn't notify; the caller shows the error.
        public bool SaveNow(out string? error)
        {
            var request = NewRequest();
            try
            {
                Write(request);
            }
            catch (Exception ex)
            {
                OnFailure(request, ex, notify: false);
                error = ex.Message;
                return false;
            }

            OnSuccess(request, "Order saved");
            error = null;
            return true;
        }

        // Blocks until a write that is running right now has finished; starts no new write
        public void WaitForRunningSave()
        {
            lock (_saveLock)
            {
            }
        }

        private SaveRequest NewRequest()
        {
            return new SaveRequest(Interlocked.Increment(ref _version), _model.Snapshot(), _model.Revision);
        }

        private void Write(SaveRequest request)
        {
            lock (_saveLock)
            {
                if (request.Version <= _lastWrittenVersion) return; // a newer snapshot is already stored
                _store.SaveStartupOrder(request.Snapshot);
                Interlocked.Exchange(ref _lastWrittenVersion, request.Version);
            }
        }

        private void OnSuccess(SaveRequest request, string logMessage)
        {
            // A change made after the snapshot keeps the list dirty
            if (_model.Revision == request.Revision)
                _model.MarkClean();
            LoggingService.LogInfo(logMessage);
        }

        private void OnFailure(SaveRequest request, Exception ex, bool notify)
        {
            // A newer snapshot was written after this one failed (its continuation ran late):
            // the failure no longer matters, so don't mark dirty or warn
            if (request.Version <= Interlocked.Read(ref _lastWrittenVersion))
            {
                LoggingService.LogWarning($"Order save failed but a newer save succeeded: {ex.Message}");
                return;
            }

            _model.MarkDirty();
            LoggingService.LogError("Failed to save order", ex);
            if (notify)
                _notifier.Notify($"Failed to save order: {ex.Message}");
        }

        private sealed record SaveRequest(long Version, StoredOrder Snapshot, long Revision);
    }
}
