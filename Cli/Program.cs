using System;
using System.IO;
using System.Text;
using System.Text.Json;
using QuestSystem;


// qdialc — консольный компилятор QDialLang
// Пишет<имя>.qbc и<имя>_<локаль>.qstr в папку -o (по умолчанию — рядом со сценарием).
// Коды выхода: 0 — собрано, 1 — ошибки в сценарии, 2 — неверный вызов или сбой записи.
static class Program
{
    private const string Usage =
        "Использование:\n" +
        "  qdialc [manifest.json] <*.qdscr|*.txt> [-o папка] [--locale ru] [--icons секция] [--dump]\n" +
        "\n" +
        "  manifest.json   манифест игры; без него компилируются только средства языка\n" +
        "  -o, --out       куда писать .qbc и .qstr (по умолчанию — рядом со сценарием)\n" +
        "  --locale        локаль в имени таблицы строк: <имя>_<локаль>.qstr (по умолчанию ru)\n" +
        "  --icons         enum-секция манифеста с именами иконок, для [ico имя кадр]\n" +
        "  --dump          показать байткод\n" +
        "  -h, --help      эта справка\n";

    private sealed class Options
    {
        public string Manifest;
        public string Script;
        public string OutDir;
        public string Locale = "ru";
        public string IconSection;
        public bool Dump;
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.InputEncoding = Encoding.UTF8; } catch (IOException) { }
        if (args.Length == 0) return Interactive();
        var options = new Options();
        var error = Parse(args, options, out bool help);
        if (help) { Console.WriteLine(Usage); return 0; }
        if (error != null)
        {
            Console.Error.WriteLine($"qdialc: {error}\n");
            Console.Error.WriteLine(Usage);
            return 2;
        }
        return Compile(options);
    }

    private static string Parse(string[] args, Options o, out bool help)
    {
        help = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next(string flag)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"после {flag} ожидалось значение");
                return args[++i];
            }
            try
            {
                switch (a)
                {
                    case "-h": case "--help": case "/?": help = true; return null;
                    case "-o": case "--out": o.OutDir = Next(a); continue;
                    case "--locale": o.Locale = Next(a); continue;
                    case "--icons": o.IconSection = Next(a); continue;
                    case "--dump": o.Dump = true; continue;
                }
            }
            catch (ArgumentException e) { return e.Message; }
            if (a.StartsWith("-")) return $"неизвестный ключ {a}";

            if (Path.GetExtension(a).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (o.Manifest != null) return "манифест указан дважды";
                o.Manifest = a;
            }
            else
            {
                if (o.Script != null) return "сценарий указан дважды: за один запуск компилируется один файл";
                o.Script = a;
            }
        }
        if (o.Script == null) return "не указан сценарий";
        if (!IsLocale(o.Locale)) return $"код локали — буквы, цифры и дефис, например en или pt-BR; получено «{o.Locale}»";
        return null;
    }

    private static int Interactive()
    {
        Console.WriteLine("qdialc — компилятор QDialLang. Пути можно перетаскивать в окно.\n");
        var o = new Options();

        o.Script = AskPath("Сценарий (*.qdscr, *.txt)", mustExist: true, optional: false);
        if (o.Script == null) return Finish(2);

        o.Manifest = AskPath("Манифест (*.json; Enter — без манифеста)", mustExist: true, optional: true);
        o.OutDir = AskPath("Папка для .qbc и .qstr (Enter — рядом со сценарием)", mustExist: false, optional: true);

        while (true)
        {
            var locale = Ask("Локаль (Enter — ru)");
            if (locale.Length == 0) break;
            if (IsLocale(locale)) { o.Locale = locale; break; }
            Console.WriteLine("  Код локали — буквы, цифры и дефис, например en или pt-BR.");
        }

        if (o.Manifest != null)
        {
            var section = Ask("Секция иконок для [ico имя кадр] (Enter — пропустить)");
            if (section.Length > 0) o.IconSection = section;
        }

        Console.WriteLine();
        return Finish(Compile(o));
    }

    private static int Finish(int code)
    {
        Console.WriteLine("\nНажмите Enter, чтобы закрыть окно.");
        Console.ReadLine();
        return code;
    }

    private static string Ask(string prompt)
    {
        Console.Write(prompt + ": ");
        return (Console.ReadLine() ?? "").Trim();
    }

    private static string AskPath(string prompt, bool mustExist, bool optional)
    {
        while (true)
        {
            var path = CleanPath(Ask(prompt));
            if (path.Length == 0)
            {
                if (optional) return null;
                if (Console.In.Peek() == -1) return null;
                Console.WriteLine("  Нужен путь к файлу.");
                continue;
            }
            if (!mustExist || File.Exists(path)) return path;
            Console.WriteLine($"  Файл не найден: {path}");
        }
    }

    private static string CleanPath(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("& ")) s = s.Substring(2).Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) s = s.Substring(1, s.Length - 2);
        return s;
    }

    private static int Compile(Options o)
    {
        if (!File.Exists(o.Script))
        {
            Console.Error.WriteLine($"qdialc: сценарий не найден: {o.Script}");
            return 2;
        }
        IManifest manifest = EmptyManifest.Instance;
        if (o.Manifest != null)
        {
            try { manifest = ManifestReader.Load(o.Manifest); }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Предупреждение: манифест не прочитан ({e.Message}). Компилирую без манифеста.");
            }
        }
        else Console.WriteLine("Манифест не задан: компилирую только средства языка.");

        string source;
        try { source = File.ReadAllText(o.Script); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"qdialc: сценарий не прочитан: {e.Message}");
            return 2;
        }

        var result = DialogueCompiler.Compile(source, manifest, new CompilerOptions { IconSection = o.IconSection });

        var scriptName = Path.GetFileName(o.Script);
        foreach (var d in result.Diagnostics)
        Console.WriteLine($"{scriptName}: {d}");
        if (!result.Success)
        {
            Console.WriteLine($"\nНе скомпилировано: {scriptName}");
            return 1;
        }
        if (o.Dump) Console.WriteLine(result.Output);

        var name = Path.GetFileNameWithoutExtension(o.Script);
        var dir = string.IsNullOrWhiteSpace(o.OutDir)
            ? Path.GetDirectoryName(Path.GetFullPath(o.Script))
            : Path.GetFullPath(o.OutDir);
        var qbc = Path.Combine(dir, name + ".qbc");
        var qstr = Path.Combine(dir, $"{name}_{o.Locale}.qstr");

        try
        {
            Directory.CreateDirectory(dir);
            using (var f = File.Create(qbc)) BytecodeWriter.WriteBytecode(result.Output, f);
            using (var f = File.Create(qstr)) BytecodeWriter.WriteStrings(result.Output, f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"qdialc: не записан результат: {e.Message}");
            return 2;
        }

        Console.WriteLine($"Готово:\n  {qbc}\n  {qstr}");
        return 0;
    }

    private static bool IsLocale(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        return true;
    }
}
