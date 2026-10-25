namespace SqlToLinq.Core {

    // Visitor over the intermediate tree. Every node type has exactly one
    // Visit method, so adding a new node type to the tree causes a compile
    // error in every implementation until the new type is handled.

    public interface ILinqNodeVisitor<out T> {

        // Query structure
        T VisitQuery(LinqQueryNode node);
        T VisitInlineViewQuery(LinqInlineViewQueryNode node);
        T VisitMethodCall(LinqMethodCallNode node);
        T VisitLambda(LinqLambdaNode node);
        T VisitSetOperation(LinqSetOperationNode node);

        // Joins
        T VisitJoin(LinqJoinNode node);
        T VisitLeftJoin(LinqLeftJoinNode node);
        T VisitCrossJoin(LinqCrossJoinNode node);

        // References
        T VisitParameter(LinqParameterNode node);
        T VisitMemberAccess(LinqMemberAccessNode node);
        T VisitGroupKey(LinqGroupKeyNode node);
        T VisitGroupElement(LinqGroupElementNode node);

        // Expressions
        T VisitConstant(LinqConstantNode node);
        T VisitDateConstant(LinqDateConstantNode node);
        T VisitBinaryExpression(LinqBinaryExpressionNode node);
        T VisitUnaryExpression(LinqUnaryExpressionNode node);
        T VisitParens(LinqParensNode node);
        T VisitAnonymousObject(LinqAnonymousObjectNode node);
        T VisitStringFunction(LinqStringFunctionNode node);
        T VisitAggregate(LinqAggregateNode node);
        T VisitCase(LinqCaseNode node);
        T VisitCast(LinqCastNode node);

        // Conditions
        T VisitLike(LinqLikeNode node);
        T VisitInExpression(LinqInExpressionNode node);
        T VisitExists(LinqExistsNode node);
        T VisitSubquery(LinqSubqueryNode node);

        // Data manipulation
        T VisitDelete(LinqDeleteNode node);
        T VisitUpdate(LinqUpdateNode node);
        T VisitInsert(LinqInsertNode node);
        T VisitInsertSelect(LinqInsertSelectNode node);
    }

    // A code generator turns the intermediate tree into source code of a
    // target language. CSharpCodeGenerator produces EF Core LINQ, other
    // implementations can target other languages or libraries.

    public interface ICodeGenerator : ILinqNodeVisitor<string> {

        // Generates the complete code for the root node of the tree
        string Generate(LinqNode root);
    }
}
