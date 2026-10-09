using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QuestSystem
{
    public class ManifestReader : IManifest, IManifestCatalog
    {
        private readonly struct CommandEntry
        {
            public readonly int Id;
            public readonly ArgSignature[] Args;
            public readonly bool Absent;
            public readonly bool Deprecated;
            public readonly string Namespace;
            public readonly string Doc;

            public CommandEntry(int id, ArgSignature[] args, bool absent, bool deprecated, string ns, string doc)
            {
                Id = id; Args = args; Absent = absent; Deprecated = deprecated; Namespace = ns; Doc = doc;
            }
        }

        private readonly struct EnumEntry
        {
            public readonly int Id;
            public readonly bool Absent;
            public EnumEntry(int id, bool absent) { Id = id; Absent = absent; }
        }

        private readonly Dictionary<string, CommandEntry> _commands = new();
        private readonly Dictionary<int, int> _iconFrames = new();
        private readonly Dictionary<string, (int Id, ValueTag Type, bool Stored, string Doc)> _globals = new();
        private readonly Dictionary<string, Dictionary<string, EnumEntry>> _enums = new();
        private readonly Dictionary<string, HashSet<int>> _liveEnumIds = new();

        public int Version { get; private set; }

        public IEnumerable<string> LiveFunctionNames
        {
            get
            {
                foreach (var kv in _commands)
                    if (!kv.Value.Absent && !kv.Value.Deprecated)
                        yield return kv.Key;
            }
        }

        public IEnumerable<string> ComputedGlobalNames
        {
            get
            {
                foreach (var kv in _globals)
                    if (!kv.Value.Stored) yield return kv.Key;
            }
        }

        public IEnumerable<string> EnumSectionNames => _enums.Keys;

        // Часть для компилятора и IDE
        public static ManifestReader Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Манифест не найден: {path}");
            return Parse(File.ReadAllText(path));
        }

        public static ManifestReader Parse(string json)
        {
            var reader = new ManifestReader();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            reader.Version = GetInt(root, "version", 0);

            // Перечисления читаются первыми: на них ссылаются аргументы команд (enum_section) и их дефолты
            if (root.TryGetProperty("enums", out var enums) && enums.ValueKind == JsonValueKind.Object)
            {
                foreach (var section in enums.EnumerateObject())
                {
                    var entries = new Dictionary<string, EnumEntry>();
                    var live = new HashSet<int>();
                    if (section.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in section.Value.EnumerateArray())
                        {
                            var name = GetString(e, "name");
                            int id = GetInt(e, "id", -1);
                            if (string.IsNullOrEmpty(name) || id < 0) continue;
                            bool absent = GetBool(e, "absent");
                            entries[name] = new EnumEntry(id, absent);
                            if (!absent) live.Add(id);
                        }
                    }
                    reader._enums[section.Name] = entries;
                    reader._liveEnumIds[section.Name] = live;
                }
            }

            if (root.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in commands.EnumerateArray())
                {
                    var name = GetString(c, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    bool absent = GetBool(c, "absent");
                    // Надгробие не проверяется: его аргументы могут ссылаться на то, чего уже нет.
                    var args = absent ? System.Array.Empty<ArgSignature>() : reader.ParseArgs(c, name);
                    reader._commands[name] = new CommandEntry(GetInt(c, "id", -1), args, absent, GetBool(c, "deprecated"),
                        GetString(c, "namespace"), GetString(c, "doc"));
                }
            }

            if (root.TryGetProperty("globals", out var globals) && globals.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in globals.EnumerateArray())
                {
                    var name = GetString(g, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    bool stored = !g.TryGetProperty("stored", out var s) || s.ValueKind != JsonValueKind.False;
                    reader._globals[name] = (GetInt(g, "id", -1), ParseTag(GetString(g, "type"), $"глобальная '{name}'"), stored, GetString(g, "doc"));
                }
            }

            if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Array)
            {
                foreach (var i in icons.EnumerateArray())
                {
                    int id = GetInt(i, "id", -1);
                    if (id < 0) continue;
                    reader._iconFrames[id] = GetInt(i, "frames", 0);
                }
            }

            return reader;
        }

        public bool TryGetFunction(string name, out int id, out ArgSignature[] args)
        {
            // absent-команда существует в манифесте только как надгробие - использовать её нельзя, но и "неизвестной" она не считается.
            if (_commands.TryGetValue(name, out var e) && !e.Absent)
            {
                id = e.Id; args = e.Args; return true;
            }
            id = default; args = default; return false;
        }

        public bool IsFunctionRetired(string name) =>
            _commands.TryGetValue(name, out var e) && e.Absent;

        public bool IsFunctionDeprecated(string name) =>
            _commands.TryGetValue(name, out var e) && e.Deprecated;

        public bool TryGetIcon(int iconId, out int frameCount) =>
            _iconFrames.TryGetValue(iconId, out frameCount);

        public bool IsGlobalComputed(string name) => _globals.TryGetValue(name, out var e) && !e.Stored;

        public bool TryGetGlobalVar(string name, out int id, out ValueTag type)
        {
            if (_globals.TryGetValue(name, out var e))
            {
                id = e.Id; type = e.Type; return true;
            }
            id = default; type = default; return false;
        }

        public bool HasEnumSection(string section) => section != null && _enums.ContainsKey(section);

        public bool TryGetEnumValue(string section, string name, out int id)
        {
            if (section != null && _enums.TryGetValue(section, out var entries)
                && entries.TryGetValue(name, out var e) && !e.Absent)
            {
                id = e.Id; return true;
            }
            id = default; return false;
        }

        public bool IsEnumValueRetired(string section, string name) =>
            section != null && _enums.TryGetValue(section, out var entries)
            && entries.TryGetValue(name, out var e) && e.Absent;

        public bool IsEnumIdLive(string section, int id) =>
            section != null && _liveEnumIds.TryGetValue(section, out var live) && live.Contains(id);

        private ArgSignature[] ParseArgs(JsonElement command, string commandName)
        {
            if (!command.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
                return System.Array.Empty<ArgSignature>();

            var list = new List<ArgSignature>();
            bool seenDefault = false;
            foreach (var a in args.EnumerateArray())
            {
                var argName = GetString(a, "name") ?? $"#{list.Count}";
                var where = $"аргумент '{argName}' команды '{commandName}'";
                var tag = ParseTag(GetString(a, "type"), where);
                if (tag == ValueTag.Text)
                    throw new InvalidDataException($"Манифест: {where} имеет тип text — он допустим только у глобальных (на стек VM текст не попадает)");

                string section = GetString(a, "enum_section");
                if (tag == ValueTag.String)
                {
                    if (string.IsNullOrEmpty(section))
                        throw new InvalidDataException($"Манифест: {where} имеет тип string, но не указана enum_section");
                    if (!_enums.ContainsKey(section))
                        throw new InvalidDataException($"Манифест: {where} ссылается на несуществующую enum-секцию '{section}'");
                }
                else section = null;

                bool hasDefault = a.TryGetProperty("default", out var d);
                double defaultValue = hasDefault ? ParseDefault(d, tag, section, where) : 0;

                if (seenDefault && !hasDefault)
                    throw new InvalidDataException($"Манифест: {where} без default стоит после аргумента с default — дефолтные аргументы должны идти в конце");
                seenDefault |= hasDefault;

                list.Add(new ArgSignature(argName, tag, section, hasDefault, defaultValue));
            }
            return list.ToArray();
        }

        private double ParseDefault(JsonElement d, ValueTag tag, string section, string where)
        {
            switch (tag)
            {
                case ValueTag.Number when d.ValueKind == JsonValueKind.Number:
                    return d.GetDouble();
                case ValueTag.Bool when d.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    return d.ValueKind == JsonValueKind.True ? 1 : 0;
                case ValueTag.String when d.ValueKind == JsonValueKind.String:
                    if (TryGetEnumValue(section, d.GetString(), out int id)) return id;
                    throw new InvalidDataException($"Манифест: default '{d.GetString()}' ({where}) отсутствует в секции '{section}'");
                case ValueTag.String when d.ValueKind == JsonValueKind.Number:
                    int raw = GetIntValue(d);
                    if (IsEnumIdLive(section, raw)) return raw;
                    throw new InvalidDataException($"Манифест: default id={raw} ({where}) отсутствует в секции '{section}'");
                default:
                    throw new InvalidDataException($"Манифест: default ({where}) не соответствует типу аргумента");
            }
        }

        public IEnumerable<CommandInfo> EnumerateCommands()
        {
            foreach (var kv in _commands)
                if (!kv.Value.Absent)
                    yield return new CommandInfo(kv.Key, kv.Value.Namespace, kv.Value.Doc, kv.Value.Args, kv.Value.Deprecated);
        }

        public IEnumerable<GlobalInfo> EnumerateGlobals()
        {
            foreach (var kv in _globals)
                yield return new GlobalInfo(kv.Key, kv.Value.Type, kv.Value.Stored, kv.Value.Doc);
        }

        public IEnumerable<string> EnumerateEnumSections() => _enums.Keys;

        public IEnumerable<string> EnumerateEnumValues(string section)
        {
            if (section == null || !_enums.TryGetValue(section, out var entries)) yield break;
            foreach (var kv in entries)
                if (!kv.Value.Absent) yield return kv.Key;
        }

        private static ValueTag ParseTag(string raw, string where) => raw switch
        {
            "number" => ValueTag.Number,
            "string" => ValueTag.String,
            "bool" => ValueTag.Bool,
            "text" => ValueTag.Text,
            _ => throw new InvalidDataException($"Манифест: неизвестный тип '{raw}' ({where})")
        };

        private static string GetString(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int GetInt(JsonElement e, string prop, int fallback)
        {
            if (!e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
                return fallback;
            return GetIntValue(v);
        }

        private static int GetIntValue(JsonElement v) => v.TryGetInt32(out int i) ? i : (int)v.GetDouble();

        private static bool GetBool(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
    }
}