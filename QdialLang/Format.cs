using System.Collections.Generic;

namespace QuestSystem
{
    public enum Opcode
    {
        TextChunk, Cmd, SetSpeed, SetPos, Icon, Await, PushConst,
        GetVar, SetVar, GetGlobalVar, SetGlobalVar,
        InterpolateLocal, InterpolateGlobal,
        Add, Sub, Mul, Div, Eq, Neq, Lt, Gt, Lte, Gte, Neg,
        Jump, JumpIfFalse,
        ChoiceBegin, ChoiceOption, ChoiceEnd,
        EndLine,
        Halt,
        WindowBegin,
        Wait,
        Input,
    }

    public enum WindowStyle { Default = 0, Right = 1, Left = 2 }

    public readonly struct Instruction
    {
        public readonly Opcode Op;
        public readonly int A;
        public readonly int B;

        public Instruction(Opcode op, int a = 0, int b = 0)
        {
            Op = op; A = a; B = b;
        }

        public override string ToString() => $"{Op} A={A} B={B}";
    }

    public enum DialogueFormat { Dialogue = 0, Monologue = 1 }

    public enum ValueTag : byte { Number = 0, String = 1, Bool = 2, Text = 3 }

    public static class FormatSignatures
    {
        public const uint BytecodeMagic = 0x31434251; // "QBC1"
        public const uint StringsMagic = 0x31545351;  // "QST1"
    }

    public class LoadedProgram
    {
        public int ManifestVersion;
        public DialogueFormat Format;
        public int LocalSlotCount;
        public double[] Numbers;
        public Instruction[] Program;
    }

    public readonly struct ArgSignature
    {
        public readonly string Name;
        public readonly ValueTag Tag;
        public readonly string EnumSection;
        public readonly bool HasDefault;
        public readonly double Default;

        public ArgSignature(string name, ValueTag tag, string enumSection, bool hasDefault, double defaultValue)
        {
            Name = name; Tag = tag; EnumSection = enumSection; HasDefault = hasDefault; Default = defaultValue;
        }
    }

    public readonly struct CommandInfo
    {
        public readonly string Name;
        public readonly string Namespace;
        public readonly string Doc;
        public readonly ArgSignature[] Args;
        public readonly bool Deprecated;

        public CommandInfo(string name, string ns, string doc, ArgSignature[] args, bool deprecated)
        {
            Name = name; Namespace = ns; Doc = doc; Args = args; Deprecated = deprecated;
        }
    }

    public readonly struct GlobalInfo
    {
        public readonly string Name;
        public readonly ValueTag Type;
        public readonly bool Stored;
        public readonly string Doc;

        public GlobalInfo(string name, ValueTag type, bool stored, string doc)
        {
            Name = name; Type = type; Stored = stored; Doc = doc;
        }
    }

    public interface IManifestCatalog
    {
        IEnumerable<CommandInfo> EnumerateCommands();
        IEnumerable<GlobalInfo> EnumerateGlobals();
        IEnumerable<string> EnumerateEnumSections();

        IEnumerable<string> EnumerateEnumValues(string section);
    }

    public interface IManifest
    {

        int Version { get; }

        bool TryGetFunction(string name, out int id, out ArgSignature[] args);

        bool IsFunctionRetired(string name);

        bool IsFunctionDeprecated(string name);

        bool TryGetIcon(int iconId, out int frameCount);

        bool TryGetGlobalVar(string name, out int id, out ValueTag type);

        bool IsGlobalComputed(string name);

        bool HasEnumSection(string section);

        bool TryGetEnumValue(string section, string name, out int id);

        bool IsEnumValueRetired(string section, string name);

        bool IsEnumIdLive(string section, int id);
    }
}