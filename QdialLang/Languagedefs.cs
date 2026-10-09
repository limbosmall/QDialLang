using System.Collections.Generic;

namespace QuestSystem
{
    public static class LanguageDefs
    {
        public static readonly Dictionary<string, WindowStyle> TextWindowStyles = new()
        {
            ["text_Default"] = WindowStyle.Default,
            ["text_Right"] = WindowStyle.Right,
            ["text_Left"] = WindowStyle.Left,
        };
        public static readonly HashSet<string> CodeComponents = new() { "global_set", "command_call" };

        // Встроенные теги. Всё прочее в [ ] внутри текста — интерполяция
        public static readonly HashSet<string> BuiltinTags = new()
        {
            "\\n", "pos", "spd", "ico", "wait", "await", "auto", "cmd", "input",
        };

        // Теги, допустимые вне текстового блока, как самостоятельные конструкции
        public static readonly HashSet<string> StatementTags = new() { "cmd", "wait", "await", "input" };

        // Признанные компилятором имена типов для локальных деклараций
        public static readonly HashSet<string> KnownTypes = new() { "int" };

        public const string DirectiveMonologue = "@dialogue_format_monologue";
        public const string DirectiveDialogue = "@dialogue_format_dialogue";

        public static bool IsComponent(string name) =>
            TextWindowStyles.ContainsKey(name) || CodeComponents.Contains(name);

        public static string StripGlobal(string name, out bool isGlobal)
        {
            isGlobal = name.StartsWith("$");
            return isGlobal ? name.Substring(1) : name;
        }
    }
}
