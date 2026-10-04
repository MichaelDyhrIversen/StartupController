namespace StartupController
{
    // Singleton activation: the running instance waits on a named event, a second instance sets it and exits.
    // The event lives in the session namespace (Local\), so only processes in the same logon session can signal.
    // Setting it only asks the running instance to show its window. Repeats within MinInterval are ignored.
    internal sealed class InstanceActivation : IDisposable
    {
        internal const string DefaultEventName = @"Local\StartupControllerActivate";
        internal static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(1.5);

        private readonly EventWaitHandle _activate;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly Thread _listener;
        private readonly Action _onActivate;
        private readonly TimeSpan _minInterval;
        private readonly object _lock = new object();
        private bool _disposed;
        private bool _listenerExited;
        private bool _listenerDisposesHandles;
        private bool _handlesDisposed;

        // Creates (or opens) the event and calls onActivate on a background thread each time it is set, at most once
        // per minInterval (default 1.5 s). onActivate must marshal to the UI thread itself.
        // Throws WaitHandleCannotBeOpenedException, UnauthorizedAccessException or IOException if the event can't be created,
        // and ActivationEventExistsException if it already exists: the caller holds the singleton mutex, so an existing
        // event was made by someone else, possibly as a manual-reset event that would keep the listener spinning.
        internal InstanceActivation(string eventName, Action onActivate, TimeSpan? minInterval = null)
        {
            _onActivate = onActivate ?? throw new ArgumentNullException(nameof(onActivate));
            _minInterval = minInterval ?? DefaultMinInterval;
            var activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out bool createdNew);
            if (!createdNew)
            {
                activate.Dispose();
                _stop.Dispose();
                throw new ActivationEventExistsException(eventName);
            }
            _activate = activate;
            _listener = new Thread(Listen) { IsBackground = true, Name = "StartupController activation" };
            _listener.Start();
        }

        // Second instance: set the running instance's event. False if no instance is listening or it can't be opened.
        internal static bool SignalExisting(string eventName)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(eventName);
                return existing.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                return false;
            }
        }

        private void Listen()
        {
            try
            {
                var handles = new WaitHandle[] { _stop, _activate };
                long lastActivation = 0;
                long lastIgnoredLog = 0;
                bool first = true;
                while (WaitHandle.WaitAny(handles) == 1)
                {
                    // Defence in depth: never stay signalled, even if the event somehow is manual-reset
                    _activate.Reset();
                    long now = Environment.TickCount64;
                    if (!first && now - lastActivation < _minInterval.TotalMilliseconds)
                    {
                        // Log at most once per interval
                        if (now - lastIgnoredLog >= _minInterval.TotalMilliseconds)
                        {
                            lastIgnoredLog = now;
                            LoggingService.LogInfo("Activation request ignored (too soon after the previous one)");
                        }
                        continue;
                    }

                    first = false;
                    lastActivation = now;
                    try
                    {
                        _onActivate();
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogError("Failed to activate the running instance", ex);
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _listenerExited = true;
                    if (_listenerDisposesHandles) DisposeHandles();
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Set();
            }

            // Wait for the listener, unless Dispose runs inside the callback (on the listener thread) or the
            // callback is stuck; then the listener disposes the handles when it stops.
            bool joined = Thread.CurrentThread != _listener && _listener.Join(TimeSpan.FromSeconds(2));
            lock (_lock)
            {
                if (joined || _listenerExited)
                    DisposeHandles();
                else
                    _listenerDisposesHandles = true;
            }
        }

        // Called under _lock
        private void DisposeHandles()
        {
            if (_handlesDisposed) return;
            _handlesDisposed = true;
            _activate.Dispose();
            _stop.Dispose();
        }
    }

    // The activation event already existed when this instance (which holds the singleton mutex) tried to create it
    internal sealed class ActivationEventExistsException : IOException
    {
        internal ActivationEventExistsException(string eventName)
            : base($"The activation event '{eventName}' already exists")
        {
        }
    }

    // Delivers activation requests to the form once it exists. A request that arrives earlier is kept and
    // delivered when the target is attached, instead of being dropped.
    internal sealed class ActivationRelay
    {
        private readonly object _lock = new object();
        private Action? _target;
        private bool _pending;

        internal void Request()
        {
            Action? target;
            lock (_lock)
            {
                target = _target;
                if (target == null)
                {
                    _pending = true;
                    return;
                }
            }
            target();
        }

        internal void Attach(Action target)
        {
            ArgumentNullException.ThrowIfNull(target);
            bool pending;
            lock (_lock)
            {
                _target = target;
                pending = _pending;
                _pending = false;
            }
            if (pending) target();
        }
    }
}
