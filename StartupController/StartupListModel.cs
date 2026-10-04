namespace StartupController
{
    // UI-independent state of the startup list: ordering, app-level enable/disable and the dirty flag.
    // Mutators take the program itself (not a list position), so sorting or reselection in the view
    // can't make them act on the wrong entry. They return true when the caller should treat the list
    // as changed (refresh and mark dirty), and false when the program isn't in the list or can't move.
    public sealed class StartupListModel
    {
        private List<StartupProgram> _programs = new List<StartupProgram>();

        public IReadOnlyList<StartupProgram> Programs => _programs;

        public int Count => _programs.Count;

        // Changed only through MarkDirty/MarkClean; mutators leave it alone (AutoSave decides
        // whether a change leaves the list dirty)
        public bool IsDirty { get; private set; }

        public void MarkDirty() => IsDirty = true;

        public void MarkClean() => IsDirty = false;

        // Bumped by Load and by every mutator that reports a change. A save marks the list clean only if the
        // revision it snapshotted is still current, so a change made while a save is running stays dirty.
        public long Revision { get; private set; }

        public void Load(IEnumerable<StartupProgram> programs)
        {
            _programs = programs.ToList();
            Revision++;
        }

        // Position of the program instance in display order, or -1
        public int IndexOf(StartupProgram program)
        {
            for (int i = 0; i < _programs.Count; i++)
            {
                if (ReferenceEquals(_programs[i], program))
                    return i;
            }
            return -1;
        }

        // Where the selection goes after the view is rebuilt: the same instance, else the first program with exactly
        // the same name (e.g. after a reload), else the first with the same name ignoring case, else -1
        public int IndexToReselect(StartupProgram? selected)
        {
            if (selected == null) return -1;
            int index = IndexOf(selected);
            if (index >= 0) return index;
            index = IndexOfName(selected.Name, StringComparison.Ordinal);
            return index >= 0 ? index : IndexOfName(selected.Name, StringComparison.OrdinalIgnoreCase);
        }

        private int IndexOfName(string name, StringComparison comparison)
        {
            for (int i = 0; i < _programs.Count; i++)
            {
                if (string.Equals(_programs[i].Name, name, comparison))
                    return i;
            }
            return -1;
        }

        public bool MoveUp(StartupProgram program) => Move(program, -1);

        public bool MoveDown(StartupProgram program) => Move(program, 1);

        public bool MoveTop(StartupProgram program)
        {
            int index = IndexOf(program);
            if (index <= 0) return false; // missing or already at top
            _programs.RemoveAt(index);
            _programs.Insert(0, program);
            return Revised();
        }

        public bool MoveBottom(StartupProgram program)
        {
            int index = IndexOf(program);
            if (index < 0 || index >= _programs.Count - 1) return false; // missing or already at bottom
            _programs.RemoveAt(index);
            _programs.Add(program);
            return Revised();
        }

        // Enable/Disable report a change even when the flag already had that value (matches the old handlers).
        // All three clear the D7 Changed status: enabling approves the current Run data, disabling drops it.
        public bool Toggle(StartupProgram program)
        {
            if (IndexOf(program) < 0) return false;
            program.Enabled = !program.Enabled;
            program.Changed = false;
            return Revised();
        }

        public bool Enable(StartupProgram program)
        {
            if (IndexOf(program) < 0) return false;
            program.Enabled = true;
            program.Changed = false;
            return Revised();
        }

        public bool Disable(StartupProgram program)
        {
            if (IndexOf(program) < 0) return false;
            program.Enabled = false;
            program.Changed = false;
            return Revised();
        }

        public List<StartupProgram> EnabledPrograms()
        {
            return _programs.Where(p => p.Enabled).ToList();
        }

        // Take on the UI thread; the result is safe to hand to a background save.
        // Enabled programs carry the fingerprint of the Run data shown when the list was loaded, so saving
        // records exactly what the user approved. Changed entries are saved as disabled, without a fingerprint.
        public StoredOrder Snapshot()
        {
            var enabled = _programs.Where(p => p.Enabled).ToList();
            return StoredOrder.Create(
                _programs.Select(p => p.Name),
                enabled.Select(p => p.Name),
                enabled.Where(p => RunFingerprint.IsWellFormed(p.Fingerprint))
                    .Select(p => new KeyValuePair<string, string>(p.Name, p.Fingerprint)),
                fingerprintsKnown: true);
        }

        private bool Move(StartupProgram program, int direction)
        {
            int index = IndexOf(program);
            if (index < 0) return false;
            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= _programs.Count) return false;
            _programs.RemoveAt(index);
            _programs.Insert(newIndex, program);
            return Revised();
        }

        private bool Revised()
        {
            Revision++;
            return true;
        }
    }
}
