using System;
using System.Collections.Generic;

namespace QuestSystem
{
    public sealed class CompilerOptions
    {
        public string IconSection;
    }

    public sealed class CompileResult
    {
        public EmitResult Output;

        public List<Diagnostic> Diagnostics = new();

        public List<Token> Tokens = new();
        public SymbolTable Symbols = new();
        public DialogueFormat Format;

        public bool Success => Output != null;
    }

    public static class DialogueCompiler
    {
        public static CompileResult Compile(string source, IManifest manifest, CompilerOptions options = null)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            source ??= "";

            var diag = new DiagnosticBag();
            var result = new CompileResult();

            try
            {
                result.Tokens = new Lexer(diag).Tokenize(source);
                var (format, ast) = new Parser(result.Tokens, diag).ParseProgram();
                result.Format = format;

                var emitter = new Emitter(manifest, options, diag);
                var emitted = emitter.Emit(format, ast);
                result.Symbols = emitter.Symbols;

                if (!diag.HasErrors) result.Output = emitted;
            }
            catch (Exception e)
            {
                diag.Error(new SourceSpan(0, 0, 0), $"Внутренняя ошибка компилятора: {e}");
                result.Output = null;
            }

            result.Diagnostics = diag.ToSortedList();
            return result;
        }
    }
}
