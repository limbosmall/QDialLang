using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuestSystem
{
    public enum TokenKind
    {
        Text, Ident, String, Number, Bool, Directive,
        LBracket, RBracket, LBrace, RBrace, LParen, RParen,
        Colon, DoubleColon, Arrow, Equals, EqEq, NotEq, Lt, Gt, Lte, Gte,
        Plus, Minus, Star, Slash,
        KwIf, KwElse, KwChoice, KwHalt, KwGoto,
        Newline, Eof,
        Bad,
    }

    public readonly struct Token
    {
        public readonly TokenKind Kind;
        public readonly string Text;
        public readonly double Number;
        public readonly bool Bool;
        public readonly int Line;
        public readonly int Column;
        public readonly int Length;

        public Token(TokenKind kind, int line, int column, int length, string text = "", double number = 0, bool boolean = false)
        {
            Kind = kind; Line = line; Column = column; Length = length; Text = text; Number = number; Bool = boolean;
        }

        public SourceSpan Span => new(Line, Column, Length);

        public override string ToString() => $"{Kind}:{Text}@{Line + 1}:{Column + 1}";
    }

    public class Lexer
    {
        private static readonly Dictionary<string, TokenKind> Keywords = new()
        {
            ["if"] = TokenKind.KwIf,
            ["else"] = TokenKind.KwElse,
            ["choice"] = TokenKind.KwChoice,
            ["halt"] = TokenKind.KwHalt,
            ["goto"] = TokenKind.KwGoto,
        };

        private const string Delimiters = "()[]+-*/=!<>:\"";

        private readonly DiagnosticBag _diag;
        private List<Token> _tokens;
        private Stack<bool> _textMode;
        private HashSet<int> _textLines;
        private string _pendingColonName;

        public Lexer(DiagnosticBag diagnostics = null) { _diag = diagnostics ?? new DiagnosticBag(); }

        public DiagnosticBag Diagnostics => _diag;

        public HashSet<int> TextLines => _textLines;

        public List<Token> Tokenize(string source)
        {
            _tokens = new List<Token>();
            _textMode = new Stack<bool>();
            _textLines = new HashSet<int>();
            _pendingColonName = null;
            _textMode.Push(false); // верхний уровень файла — код

            var lines = source.Replace("\r\n", "\n").Split('\n');

            for (int lineNo = 0; lineNo < lines.Length; lineNo++)
            {
                var raw = lines[lineNo];
                int start = 0;
                while (start < raw.Length && char.IsWhiteSpace(raw[start])) start++;
                if (start == raw.Length) continue;

                if (_textMode.Peek())
                    HandleTextModeLine(raw, start, lineNo);
                else
                    HandleCodeModeLine(raw, start, lineNo);
            }

            int last = lines.Length - 1;
            _tokens.Add(new Token(TokenKind.Eof, last, lines[last].Length, 0));
            return _tokens;
        }

        private void Add(TokenKind kind, int line, int col, int len, string text = "") =>
            _tokens.Add(new Token(kind, line, col, len, text));

        private void AddNewline(string raw, int lineNo) => Add(TokenKind.Newline, lineNo, raw.Length, 0);

        private static bool IsLoneChar(string raw, int start, char c)
        {
            if (raw[start] != c) return false;
            for (int i = start + 1; i < raw.Length; i++)
                if (!char.IsWhiteSpace(raw[i])) return false;
            return true;
        }

        private void PopMode()
        {
            if (_textMode.Count > 1) _textMode.Pop();
        }

        private void HandleTextModeLine(string raw, int start, int lineNo)
        {
            if (IsLoneChar(raw, start, '}'))
            {
                PopMode();
                Add(TokenKind.RBrace, lineNo, start, 1, "}");
                AddNewline(raw, lineNo);
                return;
            }
            _textLines.Add(lineNo);
            TokenizeTextLine(raw, start, lineNo);
        }

        private void HandleCodeModeLine(string raw, int start, int lineNo)
        {
            if (raw[start] == '@')
            {
                _pendingColonName = null;
                int end = start;
                while (end < raw.Length && !char.IsWhiteSpace(raw[end])) end++;
                Add(TokenKind.Directive, lineNo, start, end - start, raw.Substring(start, end - start));
                TokenizeWords(raw, end, raw.Length, lineNo); // мусор после директивы увидит парсер
                AddNewline(raw, lineNo);
                return;
            }

            if (IsLoneChar(raw, start, '{'))
            {
                bool textBody = _pendingColonName != null && LanguageDefs.TextWindowStyles.ContainsKey(_pendingColonName);
                _pendingColonName = null;
                _textMode.Push(textBody);
                Add(TokenKind.LBrace, lineNo, start, 1, "{");
                AddNewline(raw, lineNo);
                return;
            }

            if (IsLoneChar(raw, start, '}'))
            {
                _pendingColonName = null;
                PopMode();
                Add(TokenKind.RBrace, lineNo, start, 1, "}");
                AddNewline(raw, lineNo);
                return;
            }
            bool singleColon = raw[start] == ':' && !(start + 1 < raw.Length && raw[start + 1] == ':');
            _pendingColonName = singleColon ? ReadWord(raw, start + 1, raw.Length) : null;

            TokenizeWords(raw, start, raw.Length, lineNo);
            AddNewline(raw, lineNo);
        }

        private static bool IsWordChar(char c) => !char.IsWhiteSpace(c) && Delimiters.IndexOf(c) < 0;
        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static string ReadWord(string s, int from, int to)
        {
            int i = from;
            while (i < to && IsWordChar(s[i])) i++;
            return s.Substring(from, i - from);
        }

        private void TokenizeWords(string s, int from, int to, int lineNo)
        {
            int i = from;
            while (i < to)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                char next = i + 1 < to ? s[i + 1] : '\0';

                switch (c)
                {
                    case '(': Add(TokenKind.LParen, lineNo, i, 1, "("); i++; continue;
                    case ')': Add(TokenKind.RParen, lineNo, i, 1, ")"); i++; continue;
                    case '[': Add(TokenKind.LBracket, lineNo, i, 1, "["); i++; continue;
                    case ']': Add(TokenKind.RBracket, lineNo, i, 1, "]"); i++; continue;
                    case '+': Add(TokenKind.Plus, lineNo, i, 1, "+"); i++; continue;
                    case '*': Add(TokenKind.Star, lineNo, i, 1, "*"); i++; continue;
                    case '/': Add(TokenKind.Slash, lineNo, i, 1, "/"); i++; continue;

                    case '-':
                        if (next == '>') { Add(TokenKind.Arrow, lineNo, i, 2, "->"); i += 2; }
                        else { Add(TokenKind.Minus, lineNo, i, 1, "-"); i++; }
                        continue;
                    case '=':
                        if (next == '=') { Add(TokenKind.EqEq, lineNo, i, 2, "=="); i += 2; }
                        else { Add(TokenKind.Equals, lineNo, i, 1, "="); i++; }
                        continue;
                    case '!':
                        if (next == '=') { Add(TokenKind.NotEq, lineNo, i, 2, "!="); i += 2; }
                        else
                        {
                            _diag.Error(new SourceSpan(lineNo, i, 1), "Неожиданный символ '!' (логических операторов в языке нет)");
                            Add(TokenKind.Bad, lineNo, i, 1, "!");
                            i++;
                        }
                        continue;
                    case '<':
                        if (next == '=') { Add(TokenKind.Lte, lineNo, i, 2, "<="); i += 2; }
                        else { Add(TokenKind.Lt, lineNo, i, 1, "<"); i++; }
                        continue;
                    case '>':
                        if (next == '=') { Add(TokenKind.Gte, lineNo, i, 2, ">="); i += 2; }
                        else { Add(TokenKind.Gt, lineNo, i, 1, ">"); i++; }
                        continue;

                    case ':':
                        {
                            bool dbl = next == ':';
                            Add(dbl ? TokenKind.DoubleColon : TokenKind.Colon, lineNo, i, dbl ? 2 : 1, dbl ? "::" : ":");
                            i += dbl ? 2 : 1;
                            int ns = i;
                            while (i < to && IsWordChar(s[i])) i++;
                            if (i > ns) Add(TokenKind.Ident, lineNo, ns, i - ns, s.Substring(ns, i - ns));
                            continue;
                        }

                    case '"':
                        {
                            int start = i++;
                            while (i < to && s[i] != '"') i++;
                            if (i >= to)
                            {
                                _diag.Error(new SourceSpan(lineNo, start, to - start), "Не закрыта строка: нет парной '\"'");
                                Add(TokenKind.String, lineNo, start, to - start, s.Substring(start + 1, to - start - 1));
                            }
                            else
                            {
                                Add(TokenKind.String, lineNo, start, i - start + 1, s.Substring(start + 1, i - start - 1));
                                i++;
                            }
                            continue;
                        }
                }

                if (IsAsciiDigit(c))
                {
                    int start = i;
                    while (i < to && (IsAsciiDigit(s[i]) || s[i] == '.')) i++;

                    if (i < to && IsWordChar(s[i]))
                    {
                        while (i < to && IsWordChar(s[i])) i++;
                        var bad = s.Substring(start, i - start);
                        _diag.Error(new SourceSpan(lineNo, start, i - start), $"Имя '{bad}' начинается с цифры — так нельзя");
                        Add(TokenKind.Ident, lineNo, start, i - start, bad);
                        continue;
                    }

                    var text = s.Substring(start, i - start);
                    if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var num)
                        || text.EndsWith("."))
                    {
                        _diag.Error(new SourceSpan(lineNo, start, i - start), $"Некорректное число '{text}'");
                        num = 0;
                    }
                    _tokens.Add(new Token(TokenKind.Number, lineNo, start, i - start, text, number: num));
                    continue;
                }

                int wstart = i;
                while (i < to && IsWordChar(s[i])) i++;
                if (i == wstart)
                {
                    _diag.Error(new SourceSpan(lineNo, i, 1), $"Неожиданный символ '{s[i]}'");
                    Add(TokenKind.Bad, lineNo, i, 1, s[i].ToString());
                    i++;
                    continue;
                }
                var word = s.Substring(wstart, i - wstart);
                int len = i - wstart;

                if (word == "true") _tokens.Add(new Token(TokenKind.Bool, lineNo, wstart, len, word, boolean: true));
                else if (word == "false") _tokens.Add(new Token(TokenKind.Bool, lineNo, wstart, len, word, boolean: false));
                else if (Keywords.TryGetValue(word, out var kw)) Add(kw, lineNo, wstart, len, word);
                else Add(TokenKind.Ident, lineNo, wstart, len, word);
            }
        }

        private void TokenizeTextLine(string raw, int start, int lineNo)
        {
            int i = start;
            int textStart = start;
            var sb = new StringBuilder();

            void FlushText()
            {
                if (sb.Length > 0)
                {
                    Add(TokenKind.Text, lineNo, textStart, sb.Length, sb.ToString());
                    sb.Clear();
                }
            }

            while (i < raw.Length)
            {
                if (raw[i] == '[')
                {
                    FlushText();
                    int end = raw.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        _diag.Error(new SourceSpan(lineNo, i, raw.Length - i), "Не закрыт тег: нет парной ']'");
                        Add(TokenKind.LBracket, lineNo, i, 1, "[");
                        TokenizeWords(raw, i + 1, raw.Length, lineNo);
                        break;
                    }
                    Add(TokenKind.LBracket, lineNo, i, 1, "[");
                    TokenizeWords(raw, i + 1, end, lineNo);
                    Add(TokenKind.RBracket, lineNo, end, 1, "]");
                    i = end + 1;
                    continue;
                }
                if (sb.Length == 0) textStart = i;
                sb.Append(raw[i]);
                i++;
            }
            FlushText();
            AddNewline(raw, lineNo);
        }
    }
}