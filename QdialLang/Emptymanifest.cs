using System.Collections.Generic;

namespace QuestSystem
{
    // Для случаев с не указанным манифестом
    public sealed class EmptyManifest : IManifest
    {
        public static readonly EmptyManifest Instance = new();

        public int Version => 0;

        public bool TryGetFunction(string name, out int id, out ArgSignature[] args)
        {
            id = default; args = System.Array.Empty<ArgSignature>(); return false;
        }

        public bool IsFunctionRetired(string name) => false;
        public bool IsFunctionDeprecated(string name) => false;
        public bool TryGetIcon(int iconId, out int frameCount) { frameCount = 0; return false; }
        public bool IsGlobalComputed(string name) => false;

        public bool TryGetGlobalVar(string name, out int id, out ValueTag type)
        {
            id = default; type = default; return false;
        }

        public bool HasEnumSection(string section) => false;
        public bool TryGetEnumValue(string section, string name, out int id) { id = default; return false; }
        public bool IsEnumValueRetired(string section, string name) => false;
        public bool IsEnumIdLive(string section, int id) => false;
    }
}