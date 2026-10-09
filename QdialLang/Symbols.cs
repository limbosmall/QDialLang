using System.Collections.Generic;

namespace QuestSystem
{
    public enum SymbolKind { Region, Point, Local }

    public sealed class Symbol
    {
        public SymbolKind Kind;

        public string Name;

        public SourceSpan Declaration;

        public SourceSpan? RegionEnd;

        // Только локальная
        public string TypeName;
        public int ScopeEndLine = -1;

        public readonly List<SourceSpan> References = new();

        public override string ToString() => $"{Kind} {Name} @{Declaration.Line + 1}:{Declaration.Column + 1} refs={References.Count}";
    }

    public sealed class SymbolTable
    {
        public readonly List<Symbol> Regions = new();
        public readonly List<Symbol> Points = new();
        public readonly List<Symbol> Locals = new();
    }
}
