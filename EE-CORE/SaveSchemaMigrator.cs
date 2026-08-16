using System;
using System.Collections.Generic;

namespace EditableEncyclopedia
{
    // Save schema versioning + migration registry.
    //
    // Core owns the canonical save format version. Peer modules + extensions register
    // migrations from older versions to newer ones. When a save loads, Core runs every
    // applicable migration in order, giving each module a chance to upgrade its slice
    // of the schema.
    //
    // This is the load-bearing piece for the extension future: each EE-Ext-* module can
    // own its own save fields with its own format version, and Core arbitrates upgrades
    // across them. Without this, every new field risks breaking every old save.
    //
    // Current canonical save format: v11 (matches existing JSON import/export schema).
    //
    // Usage from a peer:
    //     SaveSchemaMigrator.RegisterMigration("EE-Ext-Chronicle", fromV: 1, toV: 2, (ctx) => {
    //         // transform ctx.GetSlice("EE-Ext-Chronicle") from v1 -> v2 shape
    //     });
    //
    // Phase 2.6 (EncyclopediaEditBehavior move) wires actual save-load hooks into this.
    // For now this is API-only; concrete data access lives in Phase 2.6.
    public static class SaveSchemaMigrator
    {
        // Bump when Core's own save format changes. Peer modules track their own versions
        // independently via RegisterMigration(moduleId, ...).
        public const int CoreSchemaVersion = 11;

        public delegate void MigrationFn(SaveSchemaContext context);

        private static readonly Dictionary<string, List<Registration>> _migrations =
            new Dictionary<string, List<Registration>>(StringComparer.OrdinalIgnoreCase);

        public static void RegisterMigration(string moduleId, int fromVersion, int toVersion, MigrationFn fn)
        {
            if (string.IsNullOrEmpty(moduleId) || fn == null) return;
            if (toVersion <= fromVersion)
                throw new ArgumentException($"Migration toVersion ({toVersion}) must be > fromVersion ({fromVersion})");

            if (!_migrations.TryGetValue(moduleId, out var list))
            {
                list = new List<Registration>();
                _migrations[moduleId] = list;
            }
            list.Add(new Registration(fromVersion, toVersion, fn));
        }

        public static void RunMigrations(string moduleId, int savedVersion, SaveSchemaContext context)
        {
            if (string.IsNullOrEmpty(moduleId) || context == null) return;
            if (!_migrations.TryGetValue(moduleId, out var list)) return;

            int currentVersion = savedVersion;
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var reg in list)
                {
                    if (reg.FromVersion == currentVersion)
                    {
                        try
                        {
                            reg.Fn(context);
                            currentVersion = reg.ToVersion;
                            progress = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            EECoreLogger.For("EE-Core").Error(
                                $"Migration failed for {moduleId} v{reg.FromVersion}->v{reg.ToVersion}", ex);
                            return;
                        }
                    }
                }
            }
        }

        public static int LatestRegisteredVersion(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId)) return 0;
            if (!_migrations.TryGetValue(moduleId, out var list) || list.Count == 0) return 0;
            int max = 0;
            foreach (var r in list) if (r.ToVersion > max) max = r.ToVersion;
            return max;
        }

        private readonly struct Registration
        {
            public int FromVersion { get; }
            public int ToVersion { get; }
            public MigrationFn Fn { get; }
            public Registration(int from, int to, MigrationFn fn) { FromVersion = from; ToVersion = to; Fn = fn; }
        }
    }

    // Opaque context handed to migration functions. Phase 2.6 fills in the actual slice
    // accessors (Get/SetSlice) once EncyclopediaEditBehavior lives in Core and exposes
    // its underlying dictionaries.
    public sealed class SaveSchemaContext
    {
        private readonly Dictionary<string, object> _slices;

        public SaveSchemaContext()
        {
            _slices = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public object GetSlice(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId)) return null;
            return _slices.TryGetValue(moduleId, out var v) ? v : null;
        }

        public void SetSlice(string moduleId, object value)
        {
            if (string.IsNullOrEmpty(moduleId)) return;
            _slices[moduleId] = value;
        }
    }
}
