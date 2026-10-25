using System.Collections.Generic;

namespace SqlToLinq.Core {

    // The nodes of the intermediate tree only describe the structure of the query.
    // They contain no target language syntax: every node is turned into code by an
    // ICodeGenerator implementation, which the node calls back through Accept().
    // The shape of the tree follows the LINQ operator model (Where, Select, Join ...),
    // because LINQ is the primary target, but the method names are only canonical
    // operation names that a generator may map to its own vocabulary.

    public abstract class LinqNode {
        public abstract T Accept<T>(ILinqNodeVisitor<T> visitor);
    }

    // Operators

    public enum LinqBinaryOperator {
        Equal,
        NotEqual,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
        And,
        Or,
        Add,
        Subtract,
        Multiply,
        Divide
    }

    public enum LinqUnaryOperator {
        Not
    }

    // Target types of CAST

    public enum LinqCastType {
        Int,
        Long,
        Double,
        Decimal,
        String,
        Bool,
        DateTime
    }

    // A LINQ query root node

    public class LinqQueryNode : LinqNode {

        // Name of the source table
        public string SourceTable { get; set; }

        // List of chained LINQ method calls or join nodes
        public List<LinqNode> Methods { get; set; } = new List<LinqNode>();

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitQuery(this);
    }

    // A LINQ method call

    public class LinqMethodCallNode : LinqNode {

        // Canonical name of the operation (e.g., "Where", "Select", "OrderBy")
        public string MethodName { get; set; }

        // List of arguments for the method call (e.g., lambda expressions, constants)
        public List<LinqNode> Arguments { get; set; } = new List<LinqNode>();

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitMethodCall(this);
    }

    // A LINQ lambda expression

    public class LinqLambdaNode : LinqNode {

        // The name of the parameter used in the lambda expression
        public string ParameterName { get; set; }

        // The body of the lambda expression
        public LinqNode Body { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitLambda(this);
    }

    // Binary operations

    public class LinqBinaryExpressionNode : LinqNode {

        public LinqNode Left { get; set; }

        public LinqBinaryOperator Operator { get; set; }

        public LinqNode Right { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitBinaryExpression(this);
    }

    // NOT (unary negation)

    public class LinqUnaryExpressionNode : LinqNode {

        public LinqUnaryOperator Operator { get; set; }

        public LinqNode Operand { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitUnaryExpression(this);
    }

    // IN (literal list)  or  IN (subquery)

    public class LinqInExpressionNode : LinqNode {

        public LinqNode Target { get; set; }

        public List<LinqNode> Values { get; set; } = new List<LinqNode>();

        public LinqSubqueryNode Subquery { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitInExpression(this);
    }

    // Reference to a lambda parameter or to a value introduced by an earlier
    // step of the chain (e.g., "x", "u", "j1")

    public class LinqParameterNode : LinqNode {

        public string Name { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitParameter(this);
    }

    // Member access on an object (e.g., column of a row, entity inside a join wrapper)

    public class LinqMemberAccessNode : LinqNode {

        public LinqNode Target { get; set; }

        public string MemberName { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitMemberAccess(this);
    }

    // The key of a group. MemberName is set when the group has a composite key
    // and only one of its components is referenced.

    public class LinqGroupKeyNode : LinqNode {

        public string GroupParameter { get; set; } = "g";

        public string MemberName { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitGroupKey(this);
    }

    // A representative element of a group, used for non-key columns
    // referenced in a grouped query

    public class LinqGroupElementNode : LinqNode {

        public string GroupParameter { get; set; } = "g";

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitGroupElement(this);
    }

    // Constant values

    public class LinqConstantNode : LinqNode {

        public object Value { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitConstant(this);
    }

    // Brackets

    public class LinqParensNode : LinqNode {

        public LinqNode InnerNode { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitParens(this);
    }

    // New anonymous object for Select()

    public class LinqAnonymousObjectNode : LinqNode {

        // A list of properties in the form of (property name, expression) pairs.
        // The property name can be empty when the name can be inferred.
        public List<(string Name, LinqNode Expression)> Properties { get; set; } = new List<(string, LinqNode)>();

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitAnonymousObject(this);
    }

    // Scalar SQL functions (string, math, date). FunctionName is the SQL name
    // of the function, the generator decides how it is expressed.

    public class LinqStringFunctionNode : LinqNode {

        public string FunctionName { get; set; }

        public List<LinqNode> Arguments { get; set; } = new List<LinqNode>();

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitStringFunction(this);
    }

    // GETDATE() / NOW() / GETUTCDATE() / CURRENT_DATE / CURRENT_TIMESTAMP
    // Kind is "Now", "Date" or "UtcNow".

    public class LinqDateConstantNode : LinqNode {

        public string Kind { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitDateConstant(this);
    }

    // LIKE / NOT LIKE

    public class LinqLikeNode : LinqNode {

        public LinqNode Target { get; set; }

        public string Pattern { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitLike(this);
    }

    // CASE WHEN ... THEN ... ELSE ... END

    public class LinqCaseWhenClause {
        public LinqNode Condition { get; set; }
        public LinqNode Result { get; set; }
    }

    public class LinqCaseNode : LinqNode {

        public LinqNode Operand { get; set; }

        public List<LinqCaseWhenClause> WhenClauses { get; set; } = new List<LinqCaseWhenClause>();

        public LinqNode ElseExpression { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitCase(this);
    }

    // CAST(expr AS type)

    public class LinqCastNode : LinqNode {

        public LinqNode Operand { get; set; }

        public LinqCastType CastType { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitCast(this);
    }

    // Subquery in a condition or in scalar position

    public class LinqSubqueryNode : LinqNode {

        public LinqNode Inner { get; set; }

        public bool AsScalar { get; set; }

        public string OuterParam { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitSubquery(this);
    }

    // EXISTS / NOT EXISTS

    public class LinqExistsNode : LinqNode {

        public LinqSubqueryNode Subquery { get; set; }

        public bool Negated { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitExists(this);
    }

    // UNION / UNION ALL / INTERSECT / EXCEPT

    public class LinqSetOperationNode : LinqNode {

        public LinqNode Left { get; set; }

        public LinqNode Right { get; set; }

        public string MethodName { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitSetOperation(this);
    }

    // DELETE FROM table WHERE ...

    public class LinqDeleteNode : LinqNode {

        public string SourceTable { get; set; }

        public LinqNode WhereCondition { get; set; }

        public string LambdaParam { get; set; } = "x";

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitDelete(this);
    }

    // UPDATE table SET ... WHERE ...

    public class LinqUpdateNode : LinqNode {

        public string SourceTable { get; set; }

        public List<(string ColumnName, LinqNode Value)> Assignments { get; set; }
            = new List<(string, LinqNode)>();

        public LinqNode WhereCondition { get; set; }

        public string LambdaParam { get; set; } = "x";

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitUpdate(this);
    }

    // INSERT INTO table (cols) VALUES (...), (...)

    public class LinqInsertNode : LinqNode {

        public string SourceTable { get; set; }

        public List<List<(string ColumnName, LinqNode Value)>> Rows { get; set; }
            = new List<List<(string, LinqNode)>>();

        public bool HasExplicitColumns { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitInsert(this);
    }

    // INSERT INTO table (cols) SELECT ...

    public class LinqInsertSelectNode : LinqNode {

        public string TargetTable { get; set; }

        public List<string> ColumnNames { get; set; } = new List<string>();

        public LinqNode SelectQuery { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitInsertSelect(this);
    }

    // SELECT ... FROM (SELECT ...) AS alias

    public class LinqInlineViewQueryNode : LinqNode {

        // The query of the derived table
        public LinqNode Inner { get; set; }

        public string Alias { get; set; }

        public List<LinqNode> Methods { get; set; } = new List<LinqNode>();

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitInlineViewQuery(this);
    }

    // Common shape for all join node types

    public abstract class LinqJoinBaseNode : LinqNode {

        public string InnerTable { get; set; }

        public string OuterParam { get; set; }

        public string InnerParam { get; set; }

        public LinqNode ResultSelector { get; set; }
    }

    // INNER JOIN

    public class LinqJoinNode : LinqJoinBaseNode {

        public LinqNode OuterKey { get; set; }

        public LinqNode InnerKey { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitJoin(this);
    }

    // LEFT JOIN. RIGHT JOIN is rewritten as LEFT JOIN with swapped sides
    // by the visitor, so both map to this node.

    public class LinqLeftJoinNode : LinqJoinBaseNode {

        public LinqNode OuterKey { get; set; }

        public LinqNode InnerKey { get; set; }

        public List<string> OuterAliases { get; set; } = new List<string>();

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitLeftJoin(this);
    }

    // CROSS JOIN

    public class LinqCrossJoinNode : LinqJoinBaseNode {

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitCrossJoin(this);
    }

    // Aggregation functions (Count, Sum, Average, Min, Max).
    // Distinct is only used together with Count.

    public class LinqAggregateNode : LinqNode {

        public string FunctionName { get; set; }

        public LinqNode Argument { get; set; }

        public bool Distinct { get; set; }

        public override T Accept<T>(ILinqNodeVisitor<T> visitor) => visitor.VisitAggregate(this);
    }
}
