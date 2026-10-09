using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace QuestSystem
{

    public class Emitter
    {
        private readonly IManifest _manifest;
        private readonly CompilerOptions _options;
        private readonly DiagnosticBag _diag;

        private readonly List<Instruction> _program = new();
        private readonly List<double> _numberPool = new();
        private readonly List<string> _stringPool = new();
        private readonly Dictionary<string, int> _stringCache = new();
        private readonly Dictionary<double, int> _numberCache = new();

        private sealed class Scope
        {
            public readonly bool IsRoot;
            public readonly int SlotBase;
            public readonly Dictionary<string, (int Slot, Symbol Symbol)> Vars = new();
            public Scope(bool isRoot, int slotBase) { IsRoot = isRoot; SlotBase = slotBase; }
        }

        private readonly List<Scope> _scopes = new();
        private int _nextSlot;
        private int _slotCount;

        private readonly struct PendingJump
        {
            public readonly int Index;
            public readonly bool PatchB;
            public readonly SourceSpan Span;
            public readonly bool ToRegion;
            public PendingJump(int index, bool patchB, SourceSpan span, bool toRegion)
            {
                Index = index; PatchB = patchB; Span = span; ToRegion = toRegion;
            }
        }

        private readonly Dictionary<string, int> _labelAddresses = new();
        private readonly Dictionary<string, Symbol> _labelSymbols = new();
        private readonly Dictionary<string, List<SourceSpan>> _labelRefs = new();
        private readonly List<(string Name, SourceSpan Span)> _regionEnds = new();

        // Все адреса, на которые что-либо прыгает: и именованные метки, и
        // анонимные цели if/else. Нужны оптимизации мёртвого Jump в EmitIf
        private readonly HashSet<int> _jumpTargets = new();
        private readonly Dictionary<string, List<PendingJump>> _pendingJumps = new();
        private readonly List<(int Origin, string Label, SourceSpan Span)> _namedJumps = new();
        private readonly List<(int Start, int End, int Slot, Symbol Symbol)> _rootDecls = new();
        private readonly Dictionary<int, List<int>> _slotReads = new();

        private DialogueFormat _format;
        private readonly bool _noManifest;

        public SymbolTable Symbols { get; } = new();

        public Emitter(IManifest manifest, CompilerOptions options = null, DiagnosticBag diagnostics = null)
        {
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _noManifest = manifest is EmptyManifest;
            _options = options ?? new CompilerOptions();
            _diag = diagnostics ?? new DiagnosticBag();
        }

        public DiagnosticBag Diagnostics => _diag;

        public EmitResult Emit(DialogueFormat format, List<Node> nodes)
        {
            _format = format;

            PushScope(isRoot: true);
            EmitBlock(nodes);
            PopScope(-1);
            ReportUnresolvedJumps();
            CheckJumpsOverDeclarations();
            AttachLabelReferences();
            CheckRegionPairs();

            Add(Opcode.Halt);
            return new EmitResult
            {
                Format = format,
                Program = _program.ToArray(),
                NumberPool = _numberPool,
                StringPool = _stringPool,
                LocalSlotCount = _slotCount,
                ManifestVersion = _manifest.Version,
            };
        }

        private void EmitBlock(List<Node> nodes)
        {
            foreach (var n in nodes)
            {
                try { EmitNode(n); }
                catch (CompileException e) { _diag.Error(e); }
            }
        }

        private void EmitScopedBlock(List<Node> nodes, int closeLine)
        {
            PushScope(isRoot: false);
            try { EmitBlock(nodes); }
            finally { PopScope(closeLine); }
        }

        private void EmitNode(Node node)
        {
            switch (node)
            {
                case RegionStartNode r: DefineLabel(r.Name, r.Span, isRegion: true); break;
                case LabelNode l: DefineLabel(l.Name, l.Span, isRegion: false); break;
                case RegionEndNode e: _regionEnds.Add((e.Name, e.Span)); break; // в байткод ничего не идёт
                case GotoNode g: EmitGoto(g); break;
                case HaltNode: Add(Opcode.Halt); break;
                case CommandStatementNode c: EmitTagStatement(c); break;
                case InvocationNode inv: EmitInvocation(inv); break;
                case IfNode i: EmitIf(i); break;
                case ChoiceNode c: EmitChoice(c); break;
                case LocalDeclNode d: EmitLocalDecl(d); break;
                case AssignNode a: EmitAssign(a); break;
            }
        }

        private void DefineLabel(string name, SourceSpan span, bool isRegion)
        {
            if (_labelAddresses.ContainsKey(name))
                throw new CompileException(span, $"Метка '{name}' уже объявлена (регионы и точки делят одно пространство имён)");

            CheckLabelScope(span, isRegion);

            int addr = _program.Count;
            _labelAddresses[name] = addr;
            _jumpTargets.Add(addr);

            var sym = new Symbol
            {
                Kind = isRegion ? SymbolKind.Region : SymbolKind.Point,
                Name = isRegion ? name.Substring(0, name.Length - "_start".Length) : name,
                Declaration = span,
            };
            (isRegion ? Symbols.Regions : Symbols.Points).Add(sym);
            _labelSymbols[name] = sym;

            if (_pendingJumps.TryGetValue(name, out var fixups))
            {
                foreach (var f in fixups) Patch(f.Index, addr, f.PatchB);
                _pendingJumps.Remove(name);
            }
        }
        private void CheckLabelScope(SourceSpan span, bool isRegion)
        {
            for (int s = _scopes.Count - 1; s >= 0 && !_scopes[s].IsRoot; s--)
            {
                foreach (var kv in _scopes[s].Vars)
                {
                    var what = isRegion ? "Начало региона" : "Точка";
                    _diag.Error(span,
                        $"{what} стоит внутри блока после объявления локальной '{kv.Key}' (строка {kv.Value.Symbol.Declaration.Line + 1}): " +
                        "переход сюда пропустит её инициализацию. Поставьте метку до объявления или объявите локальную вне блока");
                    return;
                }
            }
        }

        private void EmitGoto(GotoNode g)
        {
            int idx = Add(Opcode.Jump, 0);
            LinkOrDefer(g.Target, idx, patchB: false, g.Span, g.ToRegion);
        }

        private void LinkOrDefer(string label, int instrIndex, bool patchB, SourceSpan span, bool toRegion)
        {
            _namedJumps.Add((instrIndex, label, span));
            if (!_labelRefs.TryGetValue(label, out var refs))
                _labelRefs[label] = refs = new List<SourceSpan>();
            refs.Add(span);

            if (_labelAddresses.TryGetValue(label, out int addr))
            {
                Patch(instrIndex, addr, patchB);
                return;
            }
            if (!_pendingJumps.TryGetValue(label, out var list))
                _pendingJumps[label] = list = new List<PendingJump>();
            list.Add(new PendingJump(instrIndex, patchB, span, toRegion));
        }

        private void CheckJumpsOverDeclarations()
        {
            foreach (var (origin, label, span) in _namedJumps)
            {
                if (!_labelAddresses.TryGetValue(label, out int target) || target <= origin) continue;

                foreach (var (start, end, slot, symbol) in _rootDecls)
                {
                    if (origin >= start || target <= end) continue;
                    if (!_slotReads.TryGetValue(slot, out var reads) || !reads.Exists(r => r >= target)) continue;

                    var name = label.EndsWith("_start") && _labelSymbols.TryGetValue(label, out var ls) && ls.Kind == SymbolKind.Region
                        ? ":" + ls.Name : label;
                    _diag.Error(span,
                        $"Переход на '{name}' перепрыгивает объявление локальной '{symbol.Name}' (строка {symbol.Declaration.Line + 1}), " +
                        "а после цели она используется: значение не будет инициализировано (на повторном проходе — останется старым). " +
                        "Объявите локальную выше перехода");
                    break;
                }
            }
        }

        private void ReportUnresolvedJumps()
        {
            foreach (var kv in _pendingJumps)
            {
                var label = kv.Key;
                foreach (var j in kv.Value)
                {
                    string msg;
                    if (j.ToRegion)
                    {
                        var region = label.Substring(0, label.Length - "_start".Length);
                        msg = _labelAddresses.ContainsKey(region)
                            ? $"Регион '{region}' не найден. Есть точка '::{region}' — на неё переходят через 'goto {region}'"
                            : $"Регион '{region}' не найден: нет ':{region}_start'";
                    }
                    else
                    {
                        msg = _labelAddresses.ContainsKey(label + "_start")
                            ? $"Точка '{label}' не найдена. Есть регион '{label}' — на него переходят через ':{label}'"
                            : $"Точка '{label}' не найдена: нет '::{label}'";
                    }
                    _diag.Error(j.Span, msg);
                }
            }
        }

        private void AttachLabelReferences()
        {
            foreach (var kv in _labelRefs)
                if (_labelSymbols.TryGetValue(kv.Key, out var sym))
                    sym.References.AddRange(kv.Value);
        }

        private void CheckRegionPairs()
        {
            foreach (var (name, span) in _regionEnds)
            {
                var region = name.Substring(0, name.Length - "_end".Length);
                if (!_labelSymbols.TryGetValue(region + "_start", out var sym) || sym.Kind != SymbolKind.Region)
                    _diag.Warning(span, $"':{name}' без парного ':{region}_start'");
                else if (sym.RegionEnd != null)
                    _diag.Warning(span, $"Повторный конец региона '{region}'");
                else
                    sym.RegionEnd = span;
            }
            foreach (var sym in Symbols.Regions)
                if (sym.RegionEnd == null)
                    _diag.Warning(sym.Declaration, $"Регион '{sym.Name}' не закрыт: нет ':{sym.Name}_end'");
        }

        private void EmitTagStatement(CommandStatementNode node)
        {
            var tag = node.Tag;
            switch (tag.Name)
            {
                case "cmd":
                    EmitCmdTag(tag);
                    break;
                case "wait":
                    EmitWaitTag(tag);
                    break;
                case "await":
                    RequireFormat(DialogueFormat.Monologue, "[await]", tag.NameSpan);
                    EmitAwaitTag(tag);
                    break;
                case "input":
                    EmitInputTag(tag);
                    break;
                default:
                    if (LanguageDefs.BuiltinTags.Contains(tag.Name))
                        throw new CompileException(tag.NameSpan, $"Тег '[{tag.Name}]' допустим только внутри текстового блока");
                    throw new CompileException(tag.NameSpan, $"Вне текстового блока допустимы только [cmd], [wait], [await] и [input]{CommandHint(tag.Name)}");
            }
        }

        private void EmitInvocation(InvocationNode inv)
        {
            if (LanguageDefs.TextWindowStyles.TryGetValue(inv.Name, out var style))
            {
                if (inv.Body.Count == 0)
                    throw new CompileException(inv.Span, $"Текстовый блок ':{inv.Name}' пуст: окно без строк никогда не получит флаг последней строки");

                Add(Opcode.WindowBegin, (int)style);
                bool allowIcon = style != WindowStyle.Default;
                for (int i = 0; i < inv.Body.Count; i++)
                    EmitLine((LineNode)inv.Body[i], allowIcon, isLastInWindow: i == inv.Body.Count - 1);
                return;
            }

            if (LanguageDefs.CodeComponents.Contains(inv.Name))
            {
                EmitBlock(inv.Body); // группировка без своей области видимости
                return;
            }

            throw new CompileException(inv.Span, $"Неизвестная компонентная функция ':{inv.Name}'");
        }

        private void EmitLine(LineNode line, bool allowIcon, bool isLastInWindow)
        {
            bool autoAdvance = false;
            foreach (var part in line.Parts)
            {
                try
                {
                    switch (part)
                    {
                        case TextPart t:
                            Add(Opcode.TextChunk, AddString(t.Text));
                            break;
                        case TagPart tag:
                            EmitLineTag(tag, allowIcon, ref autoAdvance);
                            break;
                    }
                }
                catch (CompileException e) { _diag.Error(e); }
            }
            Add(Opcode.EndLine, autoAdvance ? 1 : 0, isLastInWindow ? 1 : 0);
        }

        private void EmitLineTag(TagPart tag, bool allowIcon, ref bool autoAdvance)
        {
            switch (tag.Name)
            {
                case "\\n":
                    RequireArgs(tag, 0);
                    Add(Opcode.TextChunk, AddString("\n"));
                    break;

                case "pos":
                    RequireFormat(DialogueFormat.Monologue, "[pos]", tag.NameSpan);
                    RequireArgs(tag, 2);
                    int x = IntArg(tag.Args[0], "[pos] x");
                    int y = IntArg(tag.Args[1], "[pos] y");
                    Add(Opcode.SetPos, x, y);
                    break;

                case "spd":
                    {
                        RequireArgs(tag, 1);
                        var op = ResolveStackArg(tag.Args[0], ValueTag.Number, "[spd]");
                        EmitPush(op);
                        Add(Opcode.SetSpeed);
                        break;
                    }

                case "cmd":
                    EmitCmdTag(tag);
                    break;

                case "ico":
                    if (!allowIcon)
                        throw new CompileException(tag.NameSpan, "[ico] допустим только внутри :text_Right/:text_Left");
                    EmitIcoTag(tag);
                    break;

                case "await":
                    RequireFormat(DialogueFormat.Monologue, "[await]", tag.NameSpan);
                    EmitAwaitTag(tag);
                    break;

                case "wait":
                    EmitWaitTag(tag);
                    break;

                case "input":
                    EmitInputTag(tag);
                    break;

                case "auto":
                    RequireArgs(tag, 0);
                    autoAdvance = true;
                    break;

                default:
                    EmitInterpolation(tag);
                    break;
            }
        }

        private void EmitWaitTag(TagPart tag)
        {
            RequireArgs(tag, 1);
            double seconds = NumberArg(tag.Args[0], "[wait]");
            if (seconds < 0)
                throw new CompileException(tag.Args[0].Span, "[wait]: пауза не может быть отрицательной");
            Add(Opcode.Wait, AddNumber(seconds));
        }

        private void EmitAwaitTag(TagPart tag)
        {
            RequireArgs(tag, 1);
            var a = tag.Args[0];
            if (a.Kind != TagArgKind.Name || !a.Text.StartsWith("$"))
                throw new CompileException(a.Span, "[await] ожидает глобальную переменную: [await $имя]");
            var name = a.Text.Substring(1);
            if (!_manifest.TryGetGlobalVar(name, out int globalId, out var type))
                throw _noManifest ? NeedManifest(a.Span, "[await]") : new CompileException(a.Span, $"Неизвестная глобальная переменная '${name}'");
            if (type == ValueTag.Text)
                throw new CompileException(a.Span, TextOnlyMessage(name));
            if (type != ValueTag.Bool)
                throw new CompileException(a.Span,
                    $"[await] ждёт bool-глобальную (выполнение продолжится, когда она станет true); '${name}' имеет тип {TypeName(type)}");
            Add(Opcode.Await, globalId);
        }

        private void EmitInputTag(TagPart tag)
        {
            RequireFormat(DialogueFormat.Dialogue, "[input]", tag.NameSpan);
            RequireArgs(tag, 1);
            var a = tag.Args[0];
            if (a.Kind != TagArgKind.Name || !a.Text.StartsWith("$"))
                throw new CompileException(a.Span, "[input] ожидает глобальную переменную: [input $имя] — введённое значение должно пережить диалог");
            var name = a.Text.Substring(1);
            if (!_manifest.TryGetGlobalVar(name, out int id, out var type))
                throw _noManifest ? NeedManifest(a.Span, "[input]") : new CompileException(a.Span, $"Неизвестная глобальная переменная '${name}'");
            if (type == ValueTag.Bool)
                throw new CompileException(a.Span, $"[input]: '${name}' имеет тип bool — для выбора да/нет есть choice");
            if (type != ValueTag.Number && type != ValueTag.Text)
                throw new CompileException(a.Span, $"[input]: '${name}' имеет тип {TypeName(type)}, ввод возможен только в number или text");
            if (_manifest.IsGlobalComputed(name))
                throw new CompileException(a.Span, $"[input]: '${name}' производная (stored: false), записывать в неё нельзя");
            Add(Opcode.Input, id, (int)type);
        }

        private void EmitCmdTag(TagPart tag)
        {
            if (tag.Args.Count == 0 || tag.Args[0].Kind != TagArgKind.Name)
                throw new CompileException(tag.Args.Count == 0 ? tag.Span : tag.Args[0].Span, "[cmd] ожидает имя команды: [cmd имя аргументы]");

            var nameArg = tag.Args[0];
            var fnName = nameArg.Text;
            if (!_manifest.TryGetFunction(fnName, out int fnId, out var sig))
            {
                if (_noManifest) throw NeedManifest(nameArg.Span, $"Команда '{fnName}'");
                if (_manifest.IsFunctionRetired(fnName))
                    throw new CompileException(nameArg.Span, $"Команда '{fnName}' удалена из манифеста");
                throw new CompileException(nameArg.Span, $"Неизвестная команда '{fnName}'");
            }

            if (_manifest.IsFunctionDeprecated(fnName))
                _diag.Warning(nameArg.Span, $"Команда '{fnName}' устарела");

            int total = sig.Length;
            int required = 0;
            for (int i = 0; i < total; i++)
                if (!sig[i].HasDefault) required = i + 1;

            int given = tag.Args.Count - 1;
            if (given < required || given > total)
            {
                var expected = required == total ? $"{total}" : $"от {required} до {total}";
                throw new CompileException(tag.Span, $"Команда '{fnName}' ожидает {expected} аргумент(ов), получено {given}");
            }

            var ops = new PushOp[total];
            bool ok = true;
            for (int i = 0; i < total; i++)
            {
                if (i >= given) { ops[i] = new PushOp(Opcode.PushConst, AddNumber(sig[i].Default)); continue; }
                try { ops[i] = ResolveCmdArg(fnName, sig[i], tag.Args[i + 1]); }
                catch (CompileException e) { _diag.Error(e); ok = false; }
            }
            if (!ok) return;

            for (int i = total - 1; i >= 0; i--)
                EmitPush(ops[i]);
            Add(Opcode.Cmd, fnId, total);
        }

        private PushOp ResolveCmdArg(string fnName, ArgSignature sig, TagArg arg)
        {
            var what = $"'{fnName}', аргумент '{sig.Name}'";
            switch (sig.Tag)
            {
                case ValueTag.Number:
                case ValueTag.Bool:
                    return ResolveStackArg(arg, sig.Tag, what);
                case ValueTag.String:
                    return new PushOp(Opcode.PushConst, AddNumber(ResolveEnumArg(sig.EnumSection, arg, what)));
                default:
                    throw new CompileException(arg.Span, $"{what}: тип {TypeName(sig.Tag)} не поддерживается у аргументов команд");
            }
        }

        private readonly struct PushOp
        {
            public readonly Opcode Op;
            public readonly int Operand;
            public PushOp(Opcode op, int operand) { Op = op; Operand = operand; }
        }


        private PushOp ResolveStackArg(TagArg arg, ValueTag expected, string what)
        {
            if (arg.Kind == TagArgKind.Name) return ResolveTagVariable(arg, expected, what);

            double v;
            if (expected == ValueTag.Bool)
            {
                if (arg.Kind != TagArgKind.Bool)
                    throw new CompileException(arg.Span, $"{what}: ожидалось true, false или bool-глобальная, получено {arg.Text}");
                v = arg.Bool ? 1 : 0;
            }
            else v = NumberArg(arg, what);
            return new PushOp(Opcode.PushConst, AddNumber(v));
        }

        private PushOp ResolveTagVariable(TagArg arg, ValueTag expected, string what)
        {
            var name = LanguageDefs.StripGlobal(arg.Text, out bool isGlobal);
            if (isGlobal)
            {
                if (!_manifest.TryGetGlobalVar(name, out int id, out var type))
                    throw _noManifest ? NeedManifest(arg.Span, $"Глобальная '${name}'") : new CompileException(arg.Span, $"{what}: неизвестная глобальная переменная '${name}'");
                if (type != expected)
                    throw new CompileException(arg.Span, $"{what}: '${name}' имеет тип {TypeName(type)}, а ожидается {TypeName(expected)}");
                return new PushOp(Opcode.GetGlobalVar, id);
            }

            if (expected != ValueTag.Number)
                throw new CompileException(arg.Span, $"{what}: локальные переменные имеют тип int, а ожидается {TypeName(expected)}");
            if (!TryLookupLocal(name, out int slot, out var sym, out _))
                throw new CompileException(arg.Span, $"{what}: локальная '{name}' не объявлена");
            sym.References.Add(arg.Span);
            return new PushOp(Opcode.GetVar, slot);
        }

        private static string TypeName(ValueTag t) => t switch
        {
            ValueTag.Number => "number",
            ValueTag.Bool => "bool",
            ValueTag.String => "string",
            ValueTag.Text => "text",
            _ => t.ToString(),
        };

        private static string TextOnlyMessage(string name) =>
            $"'${name}' имеет тип text: такая глобальная допустима только в интерполяции [${name}] и в [input] — на стек VM текст не попадает";

        // Имя из enum-секции или его числовой id, в байткод идёт только id
        private int ResolveEnumArg(string section, TagArg arg, string what)
        {
            if (!_manifest.HasEnumSection(section))
                throw _noManifest ? NeedManifest(arg.Span, what) : new CompileException(arg.Span, $"{what}: enum-секция '{section}' отсутствует в манифесте");

            switch (arg.Kind)
            {
                case TagArgKind.Number:
                    {
                        int id = IntArg(arg, what);
                        if (!_manifest.IsEnumIdLive(section, id))
                            throw new CompileException(arg.Span, $"{what}: в секции '{section}' нет значения с id={id}");
                        return id;
                    }
                case TagArgKind.Name:
                case TagArgKind.String:
                    {
                        if (arg.Kind == TagArgKind.Name && arg.Text.StartsWith("$"))
                            throw new CompileException(arg.Span, $"{what}: переменные в enum-аргументах не поддерживаются — нужно имя из секции '{section}' или его id");
                        if (_manifest.TryGetEnumValue(section, arg.Text, out int id)) return id;
                        if (_manifest.IsEnumValueRetired(section, arg.Text))
                            throw new CompileException(arg.Span, $"{what}: значение '{arg.Text}' удалено из секции '{section}'");
                        throw new CompileException(arg.Span, $"{what}: значения '{arg.Text}' нет в секции '{section}'");
                    }
                default:
                    throw new CompileException(arg.Span, $"{what}: ожидалось имя из секции '{section}' или его id, получено {arg.Text}");
            }
        }

        private void EmitIcoTag(TagPart tag)
        {
            RequireArgs(tag, 2);
            int iconId = ResolveIconId(tag.Args[0]);
            int frame = IntArg(tag.Args[1], "[ico] кадр");

            if (!_manifest.TryGetIcon(iconId, out int frameCount))
                throw _noManifest ? NeedManifest(tag.Args[0].Span, "[ico]") : new CompileException(tag.Args[0].Span, $"Иконки id={iconId} нет в таблице icons манифеста");
            if (frame < 0 || frame >= frameCount)
                throw new CompileException(tag.Args[1].Span, $"Кадр {frame} вне диапазона [0,{frameCount}) для иконки id={iconId}");
            Add(Opcode.Icon, iconId, frame);
        }

        private int ResolveIconId(TagArg arg)
        {
            if (arg.Kind == TagArgKind.Number)
            {
                int id = IntArg(arg, "[ico] id");
                if (id < 0) throw new CompileException(arg.Span, "[ico] id: id не может быть отрицательным");
                return id;
            }
            if (arg.Kind == TagArgKind.Bool)
                throw new CompileException(arg.Span, "[ico]: ожидалось имя или id иконки");
            if (_noManifest) throw NeedManifest(arg.Span, "[ico] по имени");

            var section = _options.IconSection;
            if (string.IsNullOrEmpty(section))
                throw new CompileException(arg.Span, "[ico]: иконки по имени недоступны — в настройках компилятора не задана секция иконок (CompilerOptions.IconSection)");
            if (!_manifest.HasEnumSection(section))
                throw new CompileException(arg.Span, $"[ico]: секция иконок '{section}' из настроек компилятора отсутствует в манифесте");
            return ResolveEnumArg(section, arg, "[ico]");
        }

        private void EmitInterpolation(TagPart tag)
        {
            if (tag.Args.Count > 0)
                throw new CompileException(tag.NameSpan, $"Неизвестный тег '[{tag.Name}]'{CommandHint(tag.Name)}");

            var name = LanguageDefs.StripGlobal(tag.Name, out bool isGlobal);
            if (isGlobal)
            {
                if (!_manifest.TryGetGlobalVar(name, out int id, out _))
                    throw _noManifest ? NeedManifest(tag.NameSpan, $"Глобальная '${name}'") : new CompileException(tag.NameSpan, $"Неизвестная глобальная переменная '${name}'");
                Add(Opcode.InterpolateGlobal, id);
                return;
            }

            if (!TryLookupLocal(name, out int slot, out var sym, out _))
                throw new CompileException(tag.NameSpan, $"'[{name}]' — не встроенный тег и не объявленная локальная{CommandHint(name)}");
            sym.References.Add(tag.NameSpan);
            ReadLocal(Opcode.InterpolateLocal, slot);
        }

        private CompileException NeedManifest(SourceSpan span, string what) =>
            new(span, $"{what} требует манифест, а он не задан") { NeedsManifest = true };

        private string CommandHint(string name) =>
            _manifest.TryGetFunction(name, out _, out _) ? $". Команда вызывается так: [cmd {name} ...]" : "";

        private static void RequireArgs(TagPart tag, int n)
        {
            if (tag.Args.Count == n) return;
            throw new CompileException(tag.Span, n == 0
                ? $"Тег '[{tag.Name}]' не принимает аргументов"
                : $"Тег '[{tag.Name}]' ожидает {n} аргумент(ов), получено {tag.Args.Count}");
        }

        private static double NumberArg(TagArg a, string what) => a.Kind switch
        {
            TagArgKind.Number => a.Number,
            TagArgKind.Name => throw new CompileException(a.Span, $"{what}: ожидалось число, получено имя '{a.Text}' (переменные допустимы только в аргументах [cmd] и [spd])"),
            TagArgKind.Bool => throw new CompileException(a.Span, $"{what}: ожидалось число, получено {a.Text}"),
            _ => throw new CompileException(a.Span, $"{what}: ожидалось число, получена строка \"{a.Text}\""),
        };

        private static int IntArg(TagArg a, string what)
        {
            double v = NumberArg(a, what);
            if (v != Math.Floor(v) || v < int.MinValue || v > int.MaxValue)
                throw new CompileException(a.Span, $"{what}: ожидалось целое число, получено {a.Text}");
            return (int)v;
        }

        private void EmitIf(IfNode node)
        {
            if (node.Condition != null) EmitExpr(node.Condition);
            int jf = Add(Opcode.JumpIfFalse, 0);
            EmitScopedBlock(node.Then, node.ThenEndLine);

            if (node.Else.Count > 0)
            {
                bool thenAlreadyExits = _program.Count > 0
                    && IsUnconditionalExit(_program[_program.Count - 1].Op)
                    && !_jumpTargets.Contains(_program.Count);
                int skip = -1;
                if (!thenAlreadyExits) skip = Add(Opcode.Jump, 0);

                Patch(jf, _program.Count, patchB: false);
                EmitScopedBlock(node.Else, node.ElseEndLine);

                if (!thenAlreadyExits) Patch(skip, _program.Count, patchB: false);
            }
            else
            {
                Patch(jf, _program.Count, patchB: false);
            }
        }

        private static bool IsUnconditionalExit(Opcode op) => op == Opcode.Jump || op == Opcode.Halt;

        private void EmitChoice(ChoiceNode node)
        {
            RequireFormat(DialogueFormat.Dialogue, "choice", node.Span);
            if (node.Options.Count == 0)
                throw new CompileException(node.Span, "choice без вариантов: игрок застрянет на выборе");

            Add(Opcode.ChoiceBegin);
            foreach (var opt in node.Options)
            {
                int idx = Add(Opcode.ChoiceOption, AddString(opt.Text), 0);
                LinkOrDefer(opt.Target, idx, patchB: true, opt.TargetSpan, opt.ToRegion);
            }
            Add(Opcode.ChoiceEnd);
        }

        private void PushScope(bool isRoot) => _scopes.Add(new Scope(isRoot, _nextSlot));

        private void PopScope(int closeLine)
        {
            var scope = _scopes[_scopes.Count - 1];
            foreach (var kv in scope.Vars) kv.Value.Symbol.ScopeEndLine = closeLine;
            _nextSlot = scope.SlotBase; // соседние области переиспользуют слоты
            _scopes.RemoveAt(_scopes.Count - 1);
        }

        private bool TryLookupLocal(string name, out int slot, out Symbol symbol, out bool inCurrentScope)
        {
            for (int s = _scopes.Count - 1; s >= 0; s--)
            {
                if (_scopes[s].Vars.TryGetValue(name, out var v))
                {
                    slot = v.Slot; symbol = v.Symbol; inCurrentScope = s == _scopes.Count - 1;
                    return true;
                }
            }
            slot = -1; symbol = null; inCurrentScope = false;
            return false;
        }

        private void EmitLocalDecl(LocalDeclNode d)
        {
            int start = _program.Count;
            if (d.Value != null) EmitExpr(d.Value);
            if (LanguageDefs.BuiltinTags.Contains(d.VarName))
                throw new CompileException(d.Span,
                    $"Имя '{d.VarName}' занято встроенным тегом: [{d.VarName}] в реплике — это тег, а не значение переменной");
            if (LanguageDefs.KnownTypes.Contains(d.VarName))
                throw new CompileException(d.Span, $"Имя '{d.VarName}' — это имя типа, назвать так переменную нельзя");

            if (TryLookupLocal(d.VarName, out _, out var existing, out bool sameScope))
            {
                var where = $"строка {existing.Declaration.Line + 1}";
                throw new CompileException(d.Span, sameScope
                    ? $"Локальная '{d.VarName}' уже объявлена ({where})"
                    : $"Локальная '{d.VarName}' уже объявлена во внешней области ({where}): затенение запрещено");
            }

            var scope = _scopes[_scopes.Count - 1];
            if (scope.IsRoot) _nextSlot = _slotCount;
            int slot = _nextSlot++;
            _slotCount = Math.Max(_slotCount, _nextSlot);

            var sym = new Symbol { Kind = SymbolKind.Local, Name = d.VarName, Declaration = d.Span, TypeName = d.TypeName };
            Symbols.Locals.Add(sym);
            scope.Vars[d.VarName] = (slot, sym);
            int end = Add(Opcode.SetVar, slot);
            if (scope.IsRoot) _rootDecls.Add((start, end, slot, sym));
        }

        private void EmitPush(PushOp op)
        {
            if (op.Op == Opcode.GetVar) ReadLocal(Opcode.GetVar, op.Operand);
            else Add(op.Op, op.Operand);
        }

        private int ReadLocal(Opcode op, int slot)
        {
            int at = Add(op, slot);
            if (!_slotReads.TryGetValue(slot, out var list)) _slotReads[slot] = list = new List<int>();
            list.Add(at);
            return at;
         }

        private void EmitAssign(AssignNode a)
        {
            if (a.Value != null) EmitExpr(a.Value);

            if (a.IsGlobal)
            {
                if (!_manifest.TryGetGlobalVar(a.VarName, out int id, out var type))
                    throw _noManifest ? NeedManifest(a.Span, $"Глобальная '${a.VarName}'") : new CompileException(a.Span, $"Неизвестная глобальная переменная '${a.VarName}'");
                if (_manifest.IsGlobalComputed(a.VarName))
                    throw new CompileException(a.Span, $"Глобальная '${a.VarName}' производная (stored: false): её значение вычисляет игра, запись из скрипта запрещена");
                if (type == ValueTag.Text)
                    throw new CompileException(a.Span, TextOnlyMessage(a.VarName));
                Add(Opcode.SetGlobalVar, id);
                return;
            }

            if (!TryLookupLocal(a.VarName, out int slot, out var sym, out _))
                throw new CompileException(a.Span, $"Локальная '{a.VarName}' не объявлена");
            sym.References.Add(a.Span);
            Add(Opcode.SetVar, slot);
        }

        private void EmitExpr(ExprNode expr)
        {
            switch (expr)
            {
                case null: break;
                case NumberLitNode n: Add(Opcode.PushConst, AddNumber(n.Value)); break;
                case BoolLitNode b: Add(Opcode.PushConst, AddNumber(b.Value ? 1.0 : 0.0)); break;
                case StringLitNode s:
                    _diag.Error(s.Span, "Строки в выражениях не поддерживаются");
                    Add(Opcode.PushConst, AddNumber(0));
                    break;
                case VarRefNode v: EmitVarRef(v); break;
                case UnaryExprNode u: EmitExpr(u.Operand); Add(Opcode.Neg); break;
                case BinaryExprNode bin:
                    EmitExpr(bin.Left);
                    EmitExpr(bin.Right);
                    Add(ToOpcode(bin.Op));
                    break;
            }
        }

        private void EmitVarRef(VarRefNode v)
        {
            if (v.IsGlobal)
            {
                if (!_manifest.TryGetGlobalVar(v.Name, out int id, out var type))
                {
                    if (_noManifest) _diag.Error(NeedManifest(v.Span, $"Глобальная '${v.Name}'"));
                    else _diag.Error(v.Span, $"Неизвестная глобальная переменная '${v.Name}'");
                }
                else if (type == ValueTag.Text)
                    _diag.Error(v.Span, TextOnlyMessage(v.Name));
                else { Add(Opcode.GetGlobalVar, id); return; }
            }
            else
            {
                if (TryLookupLocal(v.Name, out int slot, out var sym, out _))
                {
                    sym.References.Add(v.Span);
                    ReadLocal(Opcode.GetVar, slot);
                    return;
                }
                _diag.Error(v.Span, $"Локальная '{v.Name}' не объявлена");
            }
            Add(Opcode.PushConst, AddNumber(0));
        }

        private static Opcode ToOpcode(BinOp op) => op switch
        {
            BinOp.Add => Opcode.Add,
            BinOp.Sub => Opcode.Sub,
            BinOp.Mul => Opcode.Mul,
            BinOp.Div => Opcode.Div,
            BinOp.Eq => Opcode.Eq,
            BinOp.Neq => Opcode.Neq,
            BinOp.Lt => Opcode.Lt,
            BinOp.Gt => Opcode.Gt,
            BinOp.Lte => Opcode.Lte,
            BinOp.Gte => Opcode.Gte,
            _ => throw new InvalidOperationException($"Неизвестный оператор {op}"),
        };

        private void RequireFormat(DialogueFormat required, string what, SourceSpan span)
        {
            if (_format == required) return;
            var mode = required == DialogueFormat.Monologue
                ? $"монолога ({LanguageDefs.DirectiveMonologue})"
                : $"диалога ({LanguageDefs.DirectiveDialogue})";
            throw new CompileException(span, $"{what} допустим только в режиме {mode}");
        }

        private int Add(Opcode op, int a = 0, int b = 0)
        {
            _program.Add(new Instruction(op, a, b));
            return _program.Count - 1;
        }

        private void Patch(int instrIndex, int addr, bool patchB)
        {
            _jumpTargets.Add(addr);
            var old = _program[instrIndex];
            _program[instrIndex] = patchB
                ? new Instruction(old.Op, old.A, addr)
                : new Instruction(old.Op, addr, old.B);
        }

        private int AddNumber(double v)
        {
            if (_numberCache.TryGetValue(v, out int existing)) return existing;
            _numberPool.Add(v);
            int idx = _numberPool.Count - 1;
            _numberCache[v] = idx;
            return idx;
        }

        private int AddString(string s)
        {
            if (_stringCache.TryGetValue(s, out int existing)) return existing;
            _stringPool.Add(s);
            int idx = _stringPool.Count - 1;
            _stringCache[s] = idx;
            return idx;
        }
    }
}