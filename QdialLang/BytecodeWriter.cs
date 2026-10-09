using System.IO;
using System.Text;

namespace QuestSystem
{
    // Запись .qbc (байткод) и .qstr (таблица строк), little-endian
    public static class BytecodeWriter
    {
        private const uint BytecodeMagic = FormatSignatures.BytecodeMagic; // "QBC1" little-endian
        private const uint StringsMagic = FormatSignatures.StringsMagic;   // "QST1" little-endian

        public static void WriteBytecode(EmitResult result, Stream stream)
        {
            using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            w.Write(BytecodeMagic);
            w.Write(result.ManifestVersion);
            w.Write((byte)result.Format);
            w.Write(result.LocalSlotCount);
            w.Write(result.NumberPool.Count);
            w.Write(result.Program.Length);

            foreach (var n in result.NumberPool) w.Write(n);

            foreach (var instr in result.Program)
            {
                w.Write((byte)instr.Op);
                w.Write(instr.A);
                w.Write(instr.B);
            }
        }

        public static void WriteStrings(EmitResult result, Stream stream)
        {
            using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            w.Write(StringsMagic);
            w.Write(result.StringPool.Count);

            for (int id = 0; id < result.StringPool.Count; id++)
            {
                var bytes = Encoding.UTF8.GetBytes(result.StringPool[id]);
                w.Write(id);
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }
    }
}
