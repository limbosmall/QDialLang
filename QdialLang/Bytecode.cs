using System.Collections.Generic;

namespace QuestSystem
{
    public class EmitResult
    {
        public DialogueFormat Format;
        public Instruction[] Program;
        public List<double> NumberPool;
        public List<string> StringPool;
        public int LocalSlotCount;
        public int ManifestVersion;

        public override string ToString()
        {
            var pool = StringPool.ConvertAll(Escape);
            var program = new List<string>();
            for (int i = 0; i < Program.Length; i++) program.Add($"{i,4}: {Program[i]}");
            return $"\tFormat:\n{Format}\n" +
                   $"\tProgram:\n{string.Join("\n", program)}\n" +
                   $"\tNumberPool:\n{string.Join("\n", NumberPool)}\n" +
                   $"\tStringPool:\n{string.Join("\n", pool)}\n" +
                   $"\tLocalSlotCount:\n{LocalSlotCount}\n" +
                   $"\tManifestVersion:\n{ManifestVersion}";
        }

        private static string Escape(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }
}
