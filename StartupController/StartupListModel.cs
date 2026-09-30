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

        public void Load(IEnumerable<StartupProgram> programs)
        {
            _programs = programs.ToList();
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

        public bool MoveUp(StartupProgram program) => Move(program, -1);

        public bool MoveDown(StartupProgram program) => Move(program, 1);

        public bool MoveTop(StartupProgram program)
        {
            int index = IndexOf(program);
            if (index <= 0) return false; // missing or already at top
            _programs.RemoveAt(index);
            _programs.Insert(0, program);
            return true;
        }

        public bool MoveBottom(StartupProgram program)
        {
            int index = IndexOf(program);
            if (index < 0 || index >= _programs.Count - 1) return false; // missing or already at bottom
            _programs.RemoveAt(index);
            _programs.Add(program);
            return true;
        }

        // Enable/Disable report a change even when the flag already had that value (matches the old handlers)
        public bool Toggle(StartupProgram program)
        {
            if (IndexOf(program) < 0) return false;
            program.Enabled = !program.Enabled;
            return true;
        }

        public bool Enable(StartupProgram program)
        {
            if (IndexOf(program) < 0) return false;
            program.Enabled = true;
            return true;
        }

        public bool Disable(StartupProgram program)
        {
            if (IndexOf(program) < 0) return false;
            program.Enabled = false;
            return true;
        }

        public List<StartupProgram> EnabledPrograms()
        {
            return _programs.Where(p => p.Enabled).ToList();
        }

        // Take on the UI thread; the result is safe to hand to a background save
        public StoredOrder Snapshot()
        {
            return StoredOrder.Create(
                _programs.Select(p => p.Name),
                _programs.Where(p => p.Enabled).Select(p => p.Name));
        }

        private bool Move(StartupProgram program, int direction)
        {
            int index = IndexOf(program);
            if (index < 0) return false;
            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= _programs.Count) return false;
            _programs.RemoveAt(index);
            _programs.Insert(newIndex, program);
            return true;
        }
    }
}
