using System.Collections.Generic;

namespace QuestSystem
{
    // Span узла — место его ключевого токена (обычно имени), на которое
    // IDE повесит подчёркивание, если с узлом что-то не так.
    public abstract class Node
    {
        public SourceSpan Span;
        public int Line => Span.Line;
    }

    public class RegionStartNode : Node { public string Name; }
    public class RegionEndNode : Node { public string Name; }
    public class GotoNode : Node { public string Target; public bool ToRegion; }
    public class LabelNode : Node { public string Name; }
    public class HaltNode : Node { }

    public class InvocationNode : Node
    {
        public string Name;
        public List<Node> Body = new();
    }

    public class CommandStatementNode : Node { public TagPart Tag; }

    public class IfNode : Node
    {
        public ExprNode Condition; // null, если условие не разобралось (ошибка уже записана)
        public List<Node> Then = new();
        public List<Node> Else = new();
        public bool HasElse;
        public int ThenEndLine = -1;
        public int ElseEndLine = -1;
    }

    public class ChoiceOptionNode
    {
        public string Text;
        public SourceSpan TextSpan;
        public string Target;
        public bool ToRegion;
        public SourceSpan TargetSpan;
    }

    public class ChoiceNode : Node
    {
        public List<ChoiceOptionNode> Options = new();
    }

    public class LocalDeclNode : Node
    {
        public string TypeName;
        public string VarName;
        public ExprNode Value; // null, если выражение не разобралось
    }

    public class AssignNode : Node
    {
        public string VarName;
        public bool IsGlobal;
        public ExprNode Value; // null, если выражение не разобралось
    }

    public class LineNode : Node
    {
        public List<LinePart> Parts = new();
    }

    public abstract class LinePart { public SourceSpan Span; }
    public class TextPart : LinePart { public string Text; }

    public class TagPart : LinePart
    {
        public string Name;
        public SourceSpan NameSpan;
        public List<TagArg> Args = new();
    }

    public enum TagArgKind { Number, Bool, Name, String }

    public class TagArg
    {
        public TagArgKind Kind;
        public double Number;
        public bool Bool;
        public string Text;
        public SourceSpan Span;
    }

    public enum BinOp { Add, Sub, Mul, Div, Eq, Neq, Lt, Gt, Lte, Gte }
    public enum UnaryOp { Neg }

    public abstract class ExprNode : Node { }
    public class BinaryExprNode : ExprNode { public BinOp Op; public ExprNode Left; public ExprNode Right; }
    public class UnaryExprNode : ExprNode { public UnaryOp Op; public ExprNode Operand; }
    public class NumberLitNode : ExprNode { public double Value; }
    public class BoolLitNode : ExprNode { public bool Value; }
    public class StringLitNode : ExprNode { public string Value; }
    public class VarRefNode : ExprNode { public string Name; public bool IsGlobal; }
}
