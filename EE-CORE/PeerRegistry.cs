using System;
using System.Collections.Generic;

namespace EditableEncyclopedia
{
    // Peer module registry. Each EE-* module registers itself in OnSubModuleLoad
    // so other peers can discover it without reflection or DLL probing.
    //
    // Usage from peer SubModule:
    //     PeerRegistry.Register("EE-EditableEncyclopedia", "v2.6.0", "encyclopedia-edit", "chronicle", "timeline");
    //
    // Usage from any peer:
    //     if (PeerRegistry.Has("EE-WebAPI")) { /* show "Open in browser" button */ }
    //     PeerInfo chronicle = PeerRegistry.Get("EE-Ext-Chronicle");
    public static class PeerRegistry
    {
        private static readonly Dictionary<string, PeerInfo> _peers =
            new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string moduleId, string version, params string[] capabilities)
        {
            if (string.IsNullOrEmpty(moduleId)) return;
            _peers[moduleId] = new PeerInfo(moduleId, version ?? "unknown", capabilities ?? new string[0]);
        }

        public static bool Has(string moduleId)
        {
            return !string.IsNullOrEmpty(moduleId) && _peers.ContainsKey(moduleId);
        }

        public static PeerInfo Get(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId)) return null;
            return _peers.TryGetValue(moduleId, out var info) ? info : null;
        }

        public static bool HasCapability(string moduleId, string capability)
        {
            var p = Get(moduleId);
            if (p == null || string.IsNullOrEmpty(capability)) return false;
            for (int i = 0; i < p.Capabilities.Count; i++)
            {
                if (string.Equals(p.Capabilities[i], capability, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static IReadOnlyCollection<PeerInfo> All => _peers.Values;
    }

    public sealed class PeerInfo
    {
        public string ModuleId { get; }
        public string Version { get; }
        public IReadOnlyList<string> Capabilities { get; }

        public PeerInfo(string moduleId, string version, IReadOnlyList<string> capabilities)
        {
            ModuleId = moduleId;
            Version = version;
            Capabilities = capabilities;
        }

        public override string ToString() => $"{ModuleId}@{Version} [{string.Join(",", Capabilities)}]";
    }
}
