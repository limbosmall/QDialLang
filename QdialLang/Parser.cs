using System.Collections.Generic;

namespace QuestSystem
{

    public class Parser
    {
        private readonly List<Token> _tokens;
        private readonly DiagnosticBag _diag;
        private int _pos;

        public Parser(List<Token> tokens, DiagnosticBag diagnostics = null)
        {
            _tokens = tokens;
            _diag = diagnostics ?? new DiagnosticBag();
        }

        public DiagnosticBag Diagnostics => _diag;

        private Token Cur => _tokens[_pos];
        private Token Peek(int offset = 1) => _tokens[System.Math.Min(_pos + offset, _tokens.Count - 1)];
        private bool Check(TokenKind k) => Cur.Kind == k;

        private Token Advance()
        {
            var t = _tokens[_pos];
            if (t.Kind != TokenKind.Eof) _pos++;
            return t;
        }

        private static CompileException Error(Token at, string message) => new(at.Span, message);

        private Token Expect(TokenKind k, string what)
        {
            if (!Check(k)) throw Error(Cur, $"Ожидалось: {what}; получено {Describe(Cur)}");
            return Advance();
        }

        private static string Describe(Token t) => t.Kind switch
        {
            TokenKind.Newline => "конец строки",
            TokenKind.Eof => "конец файла",
            TokenKind.Ident => $"'{t.Text}'",
            TokenKind.Number => $"число {t.Text}",
            TokenKind.String => $"строка \"{t.Text}\"",
            TokenKind.Text => "текст",
            TokenKind.Directive => $"директива {t.Text}",
            _ => $"'{t.Text}'",
        };

        private void ExpectEndOfLine()
        {
            if (Check(TokenKind.Newline)) { Advance(); return; }
            if (Check(TokenKind.Eof)) return;

            var first = Cur;
            var last = first;
            while (!Check(TokenKind.Newline) && !Check(TokenKind.Eof)) last = Advance();
            _diag.Error(SourceSpan.Cover(first.Span, last.Span), "Лишнее в конце строки: одна конструкция — одна строка");
            if (Check(TokenKind.Newline)) Advance();
        }

        private void SkipRestOfLine()
        {
            while (!Check(TokenKind.Newline) && !Check(TokenKind.Eof)) Advance();
        }

        private void SkipToNextLine()
        {
            SkipRestOfLine();
            if (Check(TokenKind.Newline)) Advance();
        }

        private void SkipBlock()
        {
            int depth = 0;
            do
            {
                if (Check(TokenKind.LBrace)) depth++;
                else if (Check(TokenKind.RBrace)) depth--;
                Advance();
            } while (depth > 0 && !Check(TokenKind.Eof));
            if (Check(TokenKind.Newline)) Advance();
        }

        private void RecoverStatement()
        {
            SkipToNextLine();
            if (Check(TokenKind.LBrace)) SkipBlock();
        }

        public (DialogueFormat Format, List<Node> Lines) ParseProgram()
        {
            var format = DialogueFormat.Dialogue;

            if (Check(TokenKind.Directive))
            {
                var tok = Advance();
                if (tok.Text == LanguageDefs.DirectiveMonologue) format = DialogueFormat.Monologue;
                else if (tok.Text == LanguageDefs.DirectiveDialogue) format = DialogueFormat.Dialogue;
                else _diag.Error(tok.Span, $"Неизвестная директива '{tok.Text}'");
                ExpectEndOfLine();
            }
            else
            {
                _diag.Error(Cur.Span, $"Первой строкой файла должна быть директива {LanguageDefs.DirectiveMonologue} или {LanguageDefs.DirectiveDialogue}");
            }

            return (format, ParseStatements(topLevel: true));
        }

        private List<Node> ParseStatements(bool topLevel)
        {
            var nodes = new List<Node>();
            while (!Check(TokenKind.Eof))
            {
                if (Check(TokenKind.RBrace))
                {
                    if (!topLevel) break;
                    _diag.Error(Cur.Span, "Лишняя '}': нет открытого блока");
                    Advance();
                    ExpectEndOfLine();
                    continue;
                }

                try
                {
                    var n = ParseLine();
                    if (n != null) nodes.Add(n);
                }
                catch (CompileException e)
                {
                    _diag.Error(e.Span, e.Message);
                    RecoverStatement();
                }
            }
            return nodes;
        }

        private List<Node> ParseBlock(bool textBody, Token owner, out int closeLine)
        {
            closeLine = -1;
            if (!Check(TokenKind.LBrace))
            {
                _diag.Error(owner.Span, "Ожидался блок: '{' на следующей строке");
                return new List<Node>();
            }
            var open = Advance();
            ExpectEndOfLine();

            var body = textBody ? ParseTextLines() : ParseStatements(topLevel: false);

            if (Check(TokenKind.RBrace))
            {
                closeLine = Advance().Line;
                ExpectEndOfLine();
            }
            else
            {
                _diag.Error(open.Span, "Блок не закрыт: нет парной '}'");
                closeLine = Cur.Line;
            }
            return body;
        }

        private Node ParseLine()
        {
            switch (Cur.Kind)
            {
                case TokenKind.Colon: return ParseColonConstruct();
                case TokenKind.DoubleColon: return ParseLabelPoint();
                case TokenKind.KwGoto: return ParseGoto();
                case TokenKind.KwIf: return ParseIf();
                case TokenKind.KwChoice: return ParseChoice();
                case TokenKind.KwHalt: return ParseHalt();
                case TokenKind.LBracket: return ParseTagStatement();
                case TokenKind.Ident: return ParseDeclOrAssign();

                case TokenKind.LBrace:
                    _diag.Error(Cur.Span, "Блок '{' без заголовка");
                    SkipBlock();
                    return null;
                case TokenKind.KwElse:
                    throw Error(Cur, "'else' без 'if': else должен идти сразу после закрывающей '}' ветки if");
                case TokenKind.Arrow:
                    throw Error(Cur, "Вариант '->' допустим только внутри choice");
                case TokenKind.Directive:
                    throw Error(Cur, "Директива допустима только первой строкой файла");
                default:
                    throw Error(Cur, $"Неожиданная конструкция: {Describe(Cur)}");
            }
        }

        private Node ParseColonConstruct()
        {
            Advance(); // ':'
            var nameTok = Expect(TokenKind.Ident, "имя после ':'");
            var name = nameTok.Text;
            ExpectEndOfLine();

            if (Check(TokenKind.LBrace))
            {
                bool textBody = LanguageDefs.TextWindowStyles.ContainsKey(name);
                var body = ParseBlock(textBody, nameTok, out _);
                return new InvocationNode { Span = nameTok.Span, Name = name, Body = body };
            }

            if (LanguageDefs.IsComponent(name))
            {
                _diag.Error(nameTok.Span, $"У компонента ':{name}' нет тела: ожидалась '{{' на следующей строке");
                return null;
            }

            if (name.EndsWith("_start"))
            {
                if (!CheckRegionName(name.Substring(0, name.Length - "_start".Length), nameTok.Span)) return null;
                return new RegionStartNode { Span = nameTok.Span, Name = name };
            }
            if (name.EndsWith("_end"))
            {
                if (!CheckRegionName(name.Substring(0, name.Length - "_end".Length), nameTok.Span)) return null;
                return new RegionEndNode { Span = nameTok.Span, Name = name };
            }
            return new GotoNode { Span = nameTok.Span, Target = name + "_start", ToRegion = true };
        }

        private bool CheckRegionName(string region, SourceSpan span)
        {
            if (region.Length == 0)
            {
                _diag.Error(span, "У региона нет имени: ожидалось ':ИМЯ_start' / ':ИМЯ_end'");
                return false;   // безымянный маркер не нужен даже для разбора дальше
            }
            if (LanguageDefs.IsComponent(region))
            {
                _diag.Error(span, $"Регион не может называться '{region}': ':{region}' — это вызов компонента, перейти на такой регион нельзя");
                return true;
            }
            foreach (var suffix in new[] { "_start", "_end" })
                if (region.EndsWith(suffix))
                {
                    _diag.Error(span,
                        $"Имя региона '{region}' кончается на {suffix}: строка ':{region}' читается как маркер региона " +
                        $"'{region.Substring(0, region.Length - suffix.Length)}', а не как переход, — перейти на такой регион нельзя");
                    return true;
                }
            return true;
        }

        private Node ParseLabelPoint()
        {
            Advance(); // '::'
            var nameTok = Expect(TokenKind.Ident, "имя точки после '::'");
            ExpectEndOfLine();
            return new LabelNode { Span = nameTok.Span, Name = nameTok.Text };
        }

        private Node ParseGoto()
        {
            Advance(); // 'goto'
            var target = Expect(TokenKind.Ident, "имя точки после goto");
            ExpectEndOfLine();
            return new GotoNode { Span = target.Span, Target = target.Text, ToRegion = false };
        }

        private Node ParseHalt()
        {
            var kw = Advance(); // 'halt'
            ExpectEndOfLine();
            return new HaltNode { Span = kw.Span };
        }


        private Node ParseIf()
        {
            var kw = Advance(); // 'if'
            var node = new IfNode { Span = kw.Span };

            if (Check(TokenKind.Newline) || Check(TokenKind.Eof))
                _diag.Error(kw.Span, "После 'if' ожидалось условие");
            else
                node.Condition = ParseExprRecovering();
            ExpectEndOfLine();

            node.Then = ParseBlock(false, kw, out node.ThenEndLine);

            if (Check(TokenKind.KwElse))
            {
                var elseKw = Advance();
                ExpectEndOfLine();
                node.HasElse = true;
                node.Else = ParseBlock(false, elseKw, out node.ElseEndLine);
            }
            return node;
        }

        private Node ParseChoice()
        {
            var kw = Advance(); // 'choice'
            ExpectEndOfLine();
            var node = new ChoiceNode { Span = kw.Span };

            if (!Check(TokenKind.LBrace))
            {
                _diag.Error(kw.Span, "Ожидался блок вариантов: '{' на следующей строке");
                return null;
            }
            var open = Advance();
            ExpectEndOfLine();

            while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            {
                try { node.Options.Add(ParseChoiceOption()); }
                catch (CompileException e)
                {
                    _diag.Error(e.Span, e.Message);
                    SkipToNextLine();
                }
            }

            if (Check(TokenKind.RBrace)) { Advance(); ExpectEndOfLine(); }
            else _diag.Error(open.Span, "Блок не закрыт: нет парной '}'");

            return node;
        }

        private ChoiceOptionNode ParseChoiceOption()
        {
            if (!Check(TokenKind.Arrow))
                throw Error(Cur, "Внутри choice допустимы только варианты: -> \"текст\" :РЕГИОН или -> \"текст\" goto ТОЧКА");
            Advance();
            var text = Expect(TokenKind.String, "текст варианта в кавычках");

            var opt = new ChoiceOptionNode { Text = text.Text, TextSpan = text.Span };

            if (Check(TokenKind.Colon))
            {
                Advance();
                var n = Expect(TokenKind.Ident, "имя региона после ':'");
                opt.Target = n.Text + "_start"; opt.ToRegion = true; opt.TargetSpan = n.Span;
            }
            else if (Check(TokenKind.KwGoto))
            {
                Advance();
                var n = Expect(TokenKind.Ident, "имя точки после goto");
                opt.Target = n.Text; opt.ToRegion = false; opt.TargetSpan = n.Span;
            }
            else
            {
                throw Error(Cur, "Ожидалась цель варианта: ':РЕГИОН' или 'goto ТОЧКА'");
            }

            ExpectEndOfLine();
            return opt;
        }

        private Node ParseDeclOrAssign()
        {
            var first = Advance(); // Ident

            if (Check(TokenKind.Ident))
            {
                if (!LanguageDefs.KnownTypes.Contains(first.Text))
                    throw Error(first, $"Неизвестный тип '{first.Text}'. Если это текст реплики — он должен стоять внутри :text_Default/:text_Right/:text_Left");

                var nameTok = Advance();
                if (nameTok.Text.StartsWith("$"))
                    throw Error(nameTok, "Глобальные переменные не объявляются в скрипте — они приходят из манифеста");

                var decl = new LocalDeclNode { Span = nameTok.Span, TypeName = first.Text, VarName = nameTok.Text };
                if (!Check(TokenKind.Equals))
                {
                    _diag.Error(Cur.Span, $"Ожидалось: '=' и начальное значение; получено {Describe(Cur)}");
                    SkipRestOfLine();
                }
                else
                {
                    Advance();
                    decl.Value = ParseExprRecovering();
                }
                ExpectEndOfLine();
                return decl;
            }

            if (!Check(TokenKind.Equals) && LanguageDefs.IsComponent(first.Text))
                throw Error(first, $"Компонент '{first.Text}' вызывается как ':{first.Text}' — пропущено двоеточие");

            Expect(TokenKind.Equals, "'='");
            var value = ParseExprRecovering();
            ExpectEndOfLine();
            var name = LanguageDefs.StripGlobal(first.Text, out bool isGlobal);
            return new AssignNode { Span = first.Span, VarName = name, IsGlobal = isGlobal, Value = value };
        }

        private List<Node> ParseTextLines()
        {
            var nodes = new List<Node>();
            while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            {
                try { nodes.Add(ParseTextLine()); }
                catch (CompileException e)
                {
                    _diag.Error(e.Span, e.Message);
                    SkipToNextLine();
                }
            }
            return nodes;
        }

        private Node ParseTextLine()
        {
            var node = new LineNode { Span = new SourceSpan(Cur.Line, Cur.Column, 0) };

            while (!Check(TokenKind.Newline) && !Check(TokenKind.Eof))
            {
                if (Check(TokenKind.Text))
                {
                    var t = Advance();
                    node.Parts.Add(new TextPart { Span = t.Span, Text = t.Text });
                }
                else if (Check(TokenKind.LBracket))
                {
                    // Сломанный тег не должен уносить с собой всю реплику:
                    // лексер гарантирует, что в тексте у '[' есть парная ']' на той же строке
                    try { node.Parts.Add(ParseTag()); }
                    catch (CompileException e)
                    {
                        _diag.Error(e.Span, e.Message);
                        while (!Check(TokenKind.RBracket) && !Check(TokenKind.Newline) && !Check(TokenKind.Eof)) Advance();
                        if (Check(TokenKind.RBracket)) Advance();
                    }
                }
                else
                {
                    throw Error(Cur, $"Неожиданный токен {Describe(Cur)}");
                }
            }
            if (Check(TokenKind.Newline)) Advance();
            return node;
        }

        private TagPart ParseTag()
        {
            var open = Advance(); // '['
            if (!Check(TokenKind.Ident))
                throw Error(Cur, Check(TokenKind.RBracket) ? "Пустой тег '[]'" : $"Ожидалось имя тега; получено {Describe(Cur)}");
            var nameTok = Advance();

            var tag = new TagPart { Name = nameTok.Text, NameSpan = nameTok.Span };
            while (!Check(TokenKind.RBracket))
            {
                if (Check(TokenKind.Newline) || Check(TokenKind.Eof))
                    throw Error(open, "Не закрыт тег: нет парной ']'");
                tag.Args.Add(ParseTagArg());
            }
            var close = Advance(); // ']'
            tag.Span = SourceSpan.Cover(open.Span, close.Span);
            return tag;
        }

        private Node ParseTagStatement()
        {
            var tag = ParseTag();
            ExpectEndOfLine();
            return new CommandStatementNode { Span = tag.Span, Tag = tag };
        }

        private TagArg ParseTagArg()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Number:
                    Advance();
                    return new TagArg { Kind = TagArgKind.Number, Number = t.Number, Text = t.Text, Span = t.Span };
                case TokenKind.Minus when Peek().Kind == TokenKind.Number:
                    {
                        Advance();
                        var n = Advance();
                        return new TagArg { Kind = TagArgKind.Number, Number = -n.Number, Text = "-" + n.Text, Span = SourceSpan.Cover(t.Span, n.Span) };
                    }
                case TokenKind.Bool:
                    Advance();
                    return new TagArg { Kind = TagArgKind.Bool, Bool = t.Bool, Text = t.Text, Span = t.Span };
                case TokenKind.Ident:
                    Advance();
                    return new TagArg { Kind = TagArgKind.Name, Text = t.Text, Span = t.Span };
                case TokenKind.String:
                    Advance();
                    return new TagArg { Kind = TagArgKind.String, Text = t.Text, Span = t.Span };
                default:
                    throw Error(t, $"Аргумент тега должен быть литералом (число, true/false, имя); получено {Describe(t)}");
            }
        }

        private ExprNode ParseExprRecovering()
        {
            try { return ParseExpr(); }
            catch (CompileException e)
            {
                _diag.Error(e.Span, e.Message);
                SkipRestOfLine();
                return null;
            }
        }

        private ExprNode ParseExpr() => ParseComparison();

        private ExprNode ParseComparison()
        {
            var left = ParseArith();
            if (IsComparisonOp(Cur.Kind))
            {
                var opTok = Advance();
                var right = ParseArith();
                if (IsComparisonOp(Cur.Kind))
                    throw Error(Cur, "Сравнения не сцепляются: 'a < b < c' недопустимо, используйте вложенный if");
                return new BinaryExprNode { Span = opTok.Span, Op = ToBinOp(opTok.Kind), Left = left, Right = right };
            }
            return left;
        }

        private ExprNode ParseArith()
        {
            var left = ParseTerm();
            while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
            {
                var opTok = Advance();
                var right = ParseTerm();
                left = new BinaryExprNode { Span = opTok.Span, Op = ToBinOp(opTok.Kind), Left = left, Right = right };
            }
            return left;
        }

        private ExprNode ParseTerm()
        {
            var left = ParseFactor();
            while (Check(TokenKind.Star) || Check(TokenKind.Slash))
            {
                var opTok = Advance();
                var right = ParseFactor();
                left = new BinaryExprNode { Span = opTok.Span, Op = ToBinOp(opTok.Kind), Left = left, Right = right };
            }
            return left;
        }

        private ExprNode ParseFactor()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Minus:
                    Advance();
                    return new UnaryExprNode { Span = t.Span, Op = UnaryOp.Neg, Operand = ParseFactor() };
                case TokenKind.Number:
                    Advance();
                    return new NumberLitNode { Span = t.Span, Value = t.Number };
                case TokenKind.Bool:
                    Advance();
                    return new BoolLitNode { Span = t.Span, Value = t.Bool };
                case TokenKind.String:
                    Advance();
                    return new StringLitNode { Span = t.Span, Value = t.Text };
                case TokenKind.Ident:
                    {
                        Advance();
                        var name = LanguageDefs.StripGlobal(t.Text, out bool isGlobal);
                        return new VarRefNode { Span = t.Span, Name = name, IsGlobal = isGlobal };
                    }
                case TokenKind.LParen:
                    {
                        Advance();
                        var e = ParseExpr();
                        Expect(TokenKind.RParen, "')'");
                        return e;
                    }
                default:
                    throw Error(t, $"Ожидалось выражение; получено {Describe(t)}");
            }
        }

        private static bool IsComparisonOp(TokenKind k) =>
            k is TokenKind.EqEq or TokenKind.NotEq or TokenKind.Lt or TokenKind.Gt or TokenKind.Lte or TokenKind.Gte;

        private static BinOp ToBinOp(TokenKind k) => k switch
        {
            TokenKind.Plus => BinOp.Add,
            TokenKind.Minus => BinOp.Sub,
            TokenKind.Star => BinOp.Mul,
            TokenKind.Slash => BinOp.Div,
            TokenKind.EqEq => BinOp.Eq,
            TokenKind.NotEq => BinOp.Neq,
            TokenKind.Lt => BinOp.Lt,
            TokenKind.Gt => BinOp.Gt,
            TokenKind.Lte => BinOp.Lte,
            TokenKind.Gte => BinOp.Gte,
            _ => throw new System.InvalidOperationException($"Не бинарный оператор: {k}"),
        };
    }
}
