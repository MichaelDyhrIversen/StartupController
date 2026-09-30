namespace StartupController
{
    // Pure merge rules between the listed Run entries and the stored order. Names are matched
    // case-insensitively (like registry value names) and never by position.
    internal static class OrderMerger
    {
        // Listed programs in display order:
        //  1. stored names that are listed, in stored order;
        //  2. listed names that aren't stored, in registry order, disabled (new entries are never auto-launched).
        // A stored-enabled name is enabled only if its stored fingerprint matches the listed program's current
        // fingerprint (D7). Without a stored fingerprint it is accepted only while no fingerprints exist yet
        // (migration). Otherwise it comes back Changed: shown, not enabled, never launched.
        // Duplicate stored names: the first one wins. Returns new instances; the inputs are not changed.
        internal static List<StartupProgram> Merge(IReadOnlyList<StartupProgram> listed, StoredOrder stored)
        {
            var byName = new Dictionary<string, StartupProgram>(StringComparer.OrdinalIgnoreCase);
            foreach (var program in listed)
                byName.TryAdd(program.Name, program);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<StartupProgram>(listed.Count);

            foreach (var name in stored.Order)
            {
                if (!byName.TryGetValue(name, out var program) || !used.Add(program.Name))
                    continue;

                var copy = Copy(program);
                if (stored.Enabled.Contains(name))
                {
                    if (stored.Fingerprints.TryGetValue(name, out var fingerprint))
                    {
                        if (RunFingerprint.Matches(fingerprint, program.Fingerprint))
                            copy.Enabled = true;
                        else
                            MarkChanged(copy);
                    }
                    else if (!stored.FingerprintsKnown)
                    {
                        copy.Enabled = true; // first use after migration; the fingerprint is recorded at the next save
                    }
                    else
                    {
                        MarkChanged(copy);
                    }
                }
                result.Add(copy);
            }

            foreach (var program in listed)
            {
                if (used.Add(program.Name))
                    result.Add(Copy(program));
            }

            return result;
        }

        // What to write on save: the displayed order, plus previously stored names that aren't displayed
        // (Run entry missing, or Windows runs it). Each hidden name stays right after the name that came
        // before it in the previous order, or at the front if it was first, and keeps its enabled flag and
        // fingerprint. Displayed names take their flag and fingerprint from the snapshot.
        // Empty/whitespace names are dropped; duplicates keep the first occurrence. Linear in the input size.
        internal static StoredOrder MergeForSave(StoredOrder displayed, StoredOrder previous)
        {
            var displayedOrder = new List<string>();
            var displayedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in displayed.Order)
            {
                if (!string.IsNullOrWhiteSpace(name) && displayedNames.Add(name))
                    displayedOrder.Add(name);
            }

            // Hidden names grouped by the nearest displayed name before them (null key = front)
            var front = new List<string>();
            var followers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var hiddenSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? anchor = null;
            foreach (var name in previous.Order)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (displayedNames.Contains(name))
                {
                    anchor = name;
                }
                else if (hiddenSeen.Add(name))
                {
                    if (anchor == null)
                    {
                        front.Add(name);
                    }
                    else
                    {
                        if (!followers.TryGetValue(anchor, out var list))
                            followers[anchor] = list = new List<string>();
                        list.Add(name);
                    }
                }
            }

            var order = new List<string>(displayedOrder.Count + hiddenSeen.Count);
            order.AddRange(front);
            foreach (var name in displayedOrder)
            {
                order.Add(name);
                if (followers.TryGetValue(name, out var list))
                    order.AddRange(list);
            }

            var enabled = new List<string>();
            var fingerprints = new List<KeyValuePair<string, string>>();
            foreach (var name in displayedOrder)
                Carry(name, displayed, enabled, fingerprints);
            foreach (var name in hiddenSeen)
                Carry(name, previous, enabled, fingerprints);

            return StoredOrder.Create(order, enabled, fingerprints, fingerprintsKnown: true);
        }

        private static void Carry(string name, StoredOrder source, List<string> enabled, List<KeyValuePair<string, string>> fingerprints)
        {
            if (!source.Enabled.Contains(name)) return;
            enabled.Add(name);
            if (source.Fingerprints.TryGetValue(name, out var fingerprint))
                fingerprints.Add(new KeyValuePair<string, string>(name, fingerprint));
        }

        private static void MarkChanged(StartupProgram program)
        {
            program.Enabled = false;
            program.Changed = true;
        }

        private static StartupProgram Copy(StartupProgram program) => new StartupProgram
        {
            Name = program.Name,
            Path = program.Path,
            Enabled = false,
            Description = program.Description,
            Fingerprint = program.Fingerprint
        };
    }
}
