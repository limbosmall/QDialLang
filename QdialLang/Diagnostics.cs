using System;
using System.Collections;
using System.Collections.Generic;

namespace QuestSystem
{
    public readonly record struct SourceSpan(int Line, int Column, int Length)
    {
        public static SourceSpan Cover(SourceSpan a, SourceSpan b) =>
            a.Line == b.Line && b.Column + b.Length >= a.Column
                ? new SourceSpan(a.Line, a.Column, b.Column + b.Length - a.Column)
                : a;
    }

    public enum Severity { Error, Warning }

    public readonly record struct Diagnostic(Severity Severity, int Line, int Column, int Length, string Message)
    {
        public SourceSpan Span => new(Line, Column, Length);
        public bool NeedsManifest { get; init; }

        public override string ToString() =>
            $"{(Severity == Severity.Error ? "Ошибка" : "Предупреждение")} ({Line + 1}:{Column + 1}): {Message}";
    }

    public sealed class DiagnosticBag : IEnumerable<Diagnostic>
    {
        private readonly List<Diagnostic> _items = new();
        private readonly HashSet<(int Line, int Column)> _errorPositions = new();

        public bool HasErrors { get; private set; }
        public int Count => _items.Count;
        public void Error(SourceSpan span, string message, bool needsManifest = false)
        {
            HasErrors = true;
            if (!_errorPositions.Add((span.Line, span.Column))) return;
            _items.Add(new Diagnostic(Severity.Error, span.Line, span.Column, span.Length, message) { NeedsManifest = needsManifest });
        }

        internal void Error(CompileException e) => Error(e.Span, e.Message, e.NeedsManifest);

        public void Warning(SourceSpan span, string message) =>
            _items.Add(new Diagnostic(Severity.Warning, span.Line, span.Column, span.Length, message));

        public List<Diagnostic> ToSortedList()
        {
            var list = new List<Diagnostic>(_items);
            var indexed = new List<(Diagnostic d, int i)>();
            for (int i = 0; i < list.Count; i++) indexed.Add((list[i], i));
            indexed.Sort((x, y) =>
            {
                int c = x.d.Line.CompareTo(y.d.Line);
                if (c == 0) c = x.d.Column.CompareTo(y.d.Column);
                return c != 0 ? c : x.i.CompareTo(y.i);
            });
            return indexed.ConvertAll(x => x.d);
        }

        public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class CompileException : Exception
    {
        public SourceSpan Span { get; }
        public bool NeedsManifest { get; init; }
        public CompileException(SourceSpan span, string message) : base(message) { Span = span; }
    }
}
