using System;
using System.Linq;
using System.Text;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SqlToLinq.Core {

    public class SqlVisitor : SqlParserBaseVisitor<LinqNode> {

        private bool _inWhereClause = false;
        private bool _inAggregate = false;
        private bool _hasJoin = false;
        private bool _inOrderBy = false;

        // Active only while resolving a single join step's ON-condition and flatten expressions.
        // Maps each alias visible at that point to the access path needed to reach its
        // entity from within that step's outer/inner lambda scope (see the JOIN loop below).

        private Dictionary<string, string> _joinKeyAliasMap = null;

        private Dictionary<string, string> _selectAliases = new Dictionary<string, string>();
        private Dictionary<string, SqlParserParser.ExprContext> _selectAliasExprs = new Dictionary<string, SqlParserParser.ExprContext>();
        private Dictionary<string, string> _tableAliases = new Dictionary<string, string>();

        private readonly Stack<(Dictionary<string, string> TableAliases, string LambdaParam)>
            _outerScopes = new Stack<(Dictionary<string, string>, string)>();

        // Current innermost lambda parameter (default "x", changes inside JOIN lambdas).

        private string _currentLambdaParam = "x";

        // Scope marker for correlated subqueries inside a grouped query, where the
        // outer row is represented by an element of the current group.

        private const string GroupElementScope = "<group-element>";

        // Builds a parameter reference followed by member accesses from a dotted
        // name chain used for alias bookkeeping (e.g. "j1.u.Name").

        private static LinqNode Path(string dotted) {

            var parts = dotted.Split('.');
            LinqNode node = new LinqParameterNode { Name = parts[0] };

            for (int i = 1; i < parts.Length; i++) {
                node = new LinqMemberAccessNode { Target = node, MemberName = parts[i] };
            }
            return node;
        }

        private static LinqNode Member(LinqNode target, string memberName) =>
            new LinqMemberAccessNode { Target = target, MemberName = memberName };

        private static LinqNode Not(LinqNode operand) =>
            new LinqUnaryExpressionNode { Operator = LinqUnaryOperator.Not, Operand = operand };

        private SqlParserParser.SelectQueryContext UnwrapSubquery(SqlParserParser.SubqueryContext ctx) {
            while (ctx.subquery() != null) {
                ctx = ctx.subquery();
            }
            return ctx.selectQuery();
        }

        private SqlParserParser.ExprListContext UnwrapParenExprList(SqlParserParser.ParenExprListContext ctx) {
            while (ctx.parenExprList() != null) {
                ctx = ctx.parenExprList();
            }
            return ctx.exprList();
        }

        public override LinqNode VisitQuery([NotNull] SqlParserParser.QueryContext context) {

            if (context.selectQuery() != null) {
                return Visit(context.selectQuery());
            }

            if (context.deleteStmt() != null) {
                return Visit(context.deleteStmt());
            }

            if (context.insertStmt() != null) {
                return Visit(context.insertStmt());
            }

            if (context.updateStmt() != null) {
                return Visit(context.updateStmt());
            }

            throw new NotSupportedException(
                "[ERROR] Unsupported statement type. Only SELECT, DELETE, INSERT and UPDATE are supported.");

        }

        public override LinqNode VisitDeleteStmt([NotNull] SqlParserParser.DeleteStmtContext context) {

            var (tableName, _) = SetupTableScope(context.tableRef());

            LinqNode whereCondition = null;

            if (context.condition() != null) {
                whereCondition = VisitWhereCondition(context.condition());
            }

            _outerScopes.Pop();

            return new LinqDeleteNode {
                SourceTable = tableName,
                WhereCondition = whereCondition,
                LambdaParam = _currentLambdaParam
            };
        }

        public override LinqNode VisitUpdateStmt([NotNull] SqlParserParser.UpdateStmtContext context) {

            string tableName = ToPascalCase(context.tableName().GetText());

            _currentLambdaParam = "x";
            _tableAliases = new Dictionary<string, string> { [tableName.Substring(0, 1).ToLower()] = tableName };
            _outerScopes.Push((_tableAliases, _currentLambdaParam));

            var assignments = new List<(string ColumnName, LinqNode Value)>();

            foreach (var assignment in context.setClause().assignment()) {

                string colName = ToPascalCase(assignment.identifier().GetText());

                _inWhereClause = true;
                LinqNode valueNode = Visit(assignment.expr());
                _inWhereClause = false;

                assignments.Add((colName, valueNode));
            }

            LinqNode whereCondition = null;

            if (context.condition() != null) {
                whereCondition = VisitWhereCondition(context.condition());
            }

            _outerScopes.Pop();

            return new LinqUpdateNode {
                SourceTable = tableName,
                Assignments = assignments,
                WhereCondition = whereCondition,
                LambdaParam = _currentLambdaParam
            };
        }

        public override LinqNode VisitInsertValues([NotNull] SqlParserParser.InsertValuesContext context) {

            string tableName = ToPascalCase(context.tableName().GetText());

            bool hasExplicitColumns = context.idList() != null;
            var columnNames = hasExplicitColumns
                ? context.idList().identifier().Select(id => ToPascalCase(id.GetText())).ToList()
                : new List<string>();

            var rows = new List<List<(string ColumnName, LinqNode Value)>>();

            foreach (var row in context.valueRow()) {

                var values = row.valueList().expr();
                var cols = hasExplicitColumns
                    ? columnNames
                    : values.Select((_, i) => $"Col{i + 1}").ToList();

                if (hasExplicitColumns && cols.Count != values.Length) {
                    throw new NotSupportedException(
                        $"[ERROR] INSERT column count ({cols.Count}) does not match VALUES count ({values.Length}).");
                }

                var rowPairs = cols.Zip(values, (col, val) => (col, Visit(val))).ToList();
                rows.Add(rowPairs);
            }

            return new LinqInsertNode {
                SourceTable = tableName,
                Rows = rows,
                HasExplicitColumns = hasExplicitColumns
            };
        }

        public override LinqNode VisitInsertSelect([NotNull] SqlParserParser.InsertSelectContext context) {

            string tableName = ToPascalCase(context.tableName().GetText());

            var columnNames = context.idList() != null
                ? context.idList().identifier().Select(id => ToPascalCase(id.GetText())).ToList()
                : new List<string>();

            _outerScopes.Push((_tableAliases, _currentLambdaParam));
            LinqNode selectNode = Visit(context.selectQuery());
            _outerScopes.Pop();

            return new LinqInsertSelectNode {
                TargetTable = tableName,
                ColumnNames = columnNames,
                SelectQuery = selectNode
            };
        }

        public override LinqNode VisitSelectQuery([NotNull] SqlParserParser.SelectQueryContext context) {

            var selects = context.selectStmt();

            if (selects.Length == 1) {
                return Visit(selects[0]);
            }

            var result = Visit(selects[0]);

            for (int i = 1; i < selects.Length; i++) {

                var right = Visit(selects[i]);

                bool isAll = context.ALL(i - 1) != null;
                bool isIntersect = context.INTERSECT(i - 1) != null;
                bool isExcept = context.EXCEPT(i - 1) != null;

                string methodName = isIntersect ? "Intersect"
                                  : isExcept ? "Except"
                                  : isAll ? "Concat"
                                  : "Union";

                result = new LinqSetOperationNode {
                    Left = result,
                    Right = right,
                    MethodName = methodName
                };
            }

            return result;
        }

        // Snake_case to PascalCase conversion for table and column names

        private string ToPascalCase(string input) {

            if (string.IsNullOrEmpty(input)) return input;

            if ((input.StartsWith("\"") && input.EndsWith("\"")) ||
                (input.StartsWith("`") && input.EndsWith("`"))) {
                input = input.Substring(1, input.Length - 2);
            } else if (input.StartsWith("[") && input.EndsWith("]")) {
                input = input.Substring(1, input.Length - 2);
            }

            if (string.IsNullOrEmpty(input)) return input;

            bool isSnakeCase = input.Contains('_');

            if (isSnakeCase) {

                var parts = input.Split('_');
                var sb = new StringBuilder();

                foreach (var part in parts) {
                    if (part.Length == 0) continue;
                    sb.Append(char.ToUpper(part[0]));
                    if (part.Length > 1) sb.Append(part.Substring(1).ToLower());
                }

                return sb.Length > 0 ? sb.ToString() : input;
            }

            bool isMixedCase = input.Any(char.IsUpper) && input.Any(char.IsLower);

            if (isMixedCase) {
                return char.ToUpper(input[0]) + input.Substring(1);
            }

            return char.ToUpper(input[0]) + input.Substring(1).ToLower();
        }

        // GROUP BY columns extraction

        private List<string> GetGroupByColumns(SqlParserParser.SelectStmtContext selectCtx) {

            if (selectCtx.groupClause() == null) return new List<string>();

            return selectCtx.groupClause().groupItem()
                .Select(item => {
                    var ids = item.identifier();
                    return ids.Length == 2
                        ? $"{ids[0].GetText()}.{ToPascalCase(ids[1].GetText())}"
                        : ToPascalCase(ids[0].GetText());
                })
                .ToList();
        }

        private Dictionary<string, string> BuildSelectAliases(SqlParserParser.ColumnListContext columnList) {

            var aliases = new Dictionary<string, string>();

            if (columnList == null || columnList.STAR() != null) return aliases;

            foreach (var item in columnList.selectItem()) {
                if (item.identifier() != null) {
                    string alias = ToPascalCase(item.identifier().GetText());
                    if (item.expr() is SqlParserParser.ColumnExprContext colCtx) {

                        string original = ToPascalCase(colCtx.GetText());
                        aliases[alias] = original;
                    } else {
                        aliases[alias] = alias;
                    }
                }
            }
            return aliases;
        }

        private Dictionary<string, SqlParserParser.ExprContext> BuildSelectAliasExprs(SqlParserParser.ColumnListContext columnList) {

            var result = new Dictionary<string, SqlParserParser.ExprContext>();

            if (columnList == null || columnList.STAR() != null) return result;

            foreach (var item in columnList.selectItem()) {
                if (item.identifier() != null) {
                    result[ToPascalCase(item.identifier().GetText())] = item.expr();
                }
            }
            return result;
        }

        // SELECT statement processing

        public override LinqNode VisitSelectStmt([NotNull] SqlParserParser.SelectStmtContext context) {

            var savedSelectAliases = _selectAliases;
            var savedSelectAliasExprs = _selectAliasExprs;
            var savedTableAliases = _tableAliases;
            var savedHasJoin = _hasJoin;
            var savedLambdaParam = _currentLambdaParam;
            var savedClauseState = SaveClauseState();

            ResetClauseState();

            _currentLambdaParam = "x";

            _selectAliases = BuildSelectAliases(context.columnList());
            _selectAliasExprs = BuildSelectAliasExprs(context.columnList());

            // FROM clause: base table + optional joins

            var fromCtx = context.fromClause();

            string baseTable;
            string baseAlias;

            if (fromCtx.tableSource() is SqlParserParser.SubquerySourceContext subSrc) {

                baseAlias = subSrc.alias().GetText();

                _outerScopes.Push((new Dictionary<string, string>(savedTableAliases), savedLambdaParam));

                var innerNode = Visit(subSrc.subquery().selectQuery());

                _outerScopes.Pop();

                var viewQuery = new LinqInlineViewQueryNode {
                    Inner = innerNode,
                    Alias = baseAlias
                };

                _tableAliases = savedTableAliases;
                _hasJoin = savedHasJoin;
                _selectAliases = savedSelectAliases;
                _selectAliasExprs = savedSelectAliasExprs;
                _currentLambdaParam = savedLambdaParam;

                RestoreClauseState(savedClauseState);

                return BuildInlineViewQuery(context, viewQuery, baseAlias);
            }

            var baseRef = ((SqlParserParser.TableRefSourceContext)fromCtx.tableSource()).tableRef();

            baseTable = ToPascalCase(baseRef.tableName().GetText());
            baseAlias = baseRef.alias() != null
                ? baseRef.alias().GetText()
                : baseTable.Substring(0, 1).ToLower();

            bool hasExplicitAlias = baseRef.alias() != null;
            bool isNestedQuery = _outerScopes.Count > 0 && savedTableAliases.Count > 0;

            _currentLambdaParam = isNestedQuery
                ? (hasExplicitAlias ? baseAlias : baseTable.Substring(0, 1).ToLower())
                : "x";

            _tableAliases = new Dictionary<string, string>();
            _tableAliases[baseAlias] = baseTable;

            _outerScopes.Push((_tableAliases, _currentLambdaParam));

            var queryNode = new LinqQueryNode { SourceTable = baseTable };

            // JOIN

            // Each join step consumes either the raw base table (for the very first join) or the
            // flattened wrapper object produced by the previous step, and always flattens its own
            // result the same way. See BuildJoinChain for the details; this is what lets an
            // arbitrary number of JOIN / CROSS JOIN clauses be stacked instead of only ever
            // supporting exactly one.

            bool hasJoin = fromCtx.joinClause().Length > 0;
            _hasJoin = hasJoin;

            BuildJoinChain(fromCtx, baseAlias, queryNode);

            // WHERE

            if (context.condition() != null) {
                queryNode.Methods.Add(BuildWhereMethod(context.condition(), _currentLambdaParam));
            }

            // GROUP BY 

            var groupKeys = GetGroupByColumns(context);
            bool hasGroupBy = groupKeys.Count > 0;

            if (hasGroupBy) {

                LinqNode keySelectorBody;

                if (groupKeys.Count == 1) {
                    keySelectorBody = Path($"x.{groupKeys[0]}");
                } else {
                    var compositeKey = new LinqAnonymousObjectNode();

                    foreach (var key in groupKeys) {
                        compositeKey.Properties.Add((null, Path($"x.{key}")));
                    }
                    keySelectorBody = compositeKey;
                }

                var groupByMethod = new LinqMethodCallNode { MethodName = "GroupBy" };

                groupByMethod.Arguments.Add(new LinqLambdaNode {
                    ParameterName = "x",
                    Body = keySelectorBody
                });
                queryNode.Methods.Add(groupByMethod);
            }

            // HAVING 

            if (context.havingClause() != null) {

                _inAggregate = true;
                var havingCondition = Visit(context.havingClause().condition());
                _inAggregate = false;

                var havingMethod = new LinqMethodCallNode { MethodName = "Where" };

                havingMethod.Arguments.Add(new LinqLambdaNode {
                    ParameterName = hasGroupBy ? "g" : "x",
                    Body = havingCondition
                });

                queryNode.Methods.Add(havingMethod);
            }

            bool hasDistinct = context.DISTINCT() != null;

            if (hasDistinct && context.orderClause() != null && context.columnList().STAR() == null) {

                var selectedColumns = context.columnList().selectItem()
                    .Select(item => {
                        if (item.identifier() != null) return ToPascalCase(item.identifier().GetText());
                        if (item.expr() is SqlParserParser.ColumnExprContext col) return ToPascalCase(col.GetText());
                        return null;
                    })
                    .Where(name => name != null)
                    .ToHashSet();

                foreach (var orderItem in context.orderClause().orderItem()) {
                    if (orderItem.expr() is SqlParserParser.ColumnExprContext orderCol) {
                        string orderColName = ToPascalCase(orderCol.GetText());
                        if (!selectedColumns.Contains(orderColName)) {
                            throw new NotSupportedException(
                                $"[ERROR] DISTINCT with ORDER BY '{orderColName}' is not supported " +
                                $"when '{orderColName}' is not in the SELECT list. " +
                                $"The sort order would be undefined after deduplication.");
                        }
                    }
                }
            }

            if (!hasDistinct && context.orderClause() != null) {
                foreach (var m in BuildOrderByMethods(context.orderClause(), hasGroupBy ? "g" : "x")) {
                    queryNode.Methods.Add(m);
                }
            }

            // Columns

            if (hasGroupBy && _outerScopes.Count > 0) {
                var (topAliases, _) = _outerScopes.Pop();
                _outerScopes.Push((topAliases, GroupElementScope));
            }

            var columnsNode = Visit(context.columnList());
            bool isGlobalSingleAggregate = false;

            if (!hasGroupBy && columnsNode is LinqAnonymousObjectNode globalAnonNode && globalAnonNode.Properties.Count == 1 && globalAnonNode.Properties[0].Expression is LinqAggregateNode globalAggNode && !globalAggNode.Distinct) {

                isGlobalSingleAggregate = true;

                var aggMethod = new LinqMethodCallNode { MethodName = globalAggNode.FunctionName };

                if (globalAggNode.Argument != null) {
                    aggMethod.Arguments.Add(new LinqLambdaNode {
                        ParameterName = _currentLambdaParam,
                        Body = globalAggNode.Argument
                    });
                }

                queryNode.Methods.Add(aggMethod);

            } else if (columnsNode is LinqAnonymousObjectNode anonNode) {

                var selectMethod = new LinqMethodCallNode { MethodName = "Select" };

                selectMethod.Arguments.Add(new LinqLambdaNode {
                    ParameterName = hasGroupBy ? "g" : _currentLambdaParam,
                    Body = anonNode
                });

                queryNode.Methods.Add(selectMethod);
            } else {

                // If there is a GROUP BY, we need to select the first item from each group

                if (hasGroupBy) {

                    var selectMethod = new LinqMethodCallNode { MethodName = "Select" };

                    selectMethod.Arguments.Add(new LinqLambdaNode {
                        ParameterName = "g",
                        Body = new LinqGroupElementNode()
                    });
                    queryNode.Methods.Add(selectMethod);
                }
            }

            // ToList() at the end

            if (!isGlobalSingleAggregate) {

                if (hasDistinct) {

                    queryNode.Methods.Add(new LinqMethodCallNode { MethodName = "Distinct" });

                    if (context.orderClause() != null) {
                        foreach (var m in BuildOrderByMethods(context.orderClause(), "x")) {
                            queryNode.Methods.Add(m);
                        }
                    }
                }

                if (context.offsetClause() != null) {

                    var offsetExpr = Visit(context.offsetClause().expr());
                    var skipMethod = new LinqMethodCallNode { MethodName = "Skip" };

                    skipMethod.Arguments.Add(offsetExpr);
                    queryNode.Methods.Add(skipMethod);
                }

                if (context.limitClause() != null) {

                    var limitExpr = Visit(context.limitClause().expr());
                    var takeMethod = new LinqMethodCallNode { MethodName = "Take" };

                    takeMethod.Arguments.Add(limitExpr);
                    queryNode.Methods.Add(takeMethod);
                }

                queryNode.Methods.Add(new LinqMethodCallNode { MethodName = "ToList" });
            }

            // Restore outer state

            _outerScopes.Pop();
            _tableAliases = savedTableAliases;
            _hasJoin = savedHasJoin;
            _selectAliases = savedSelectAliases;
            _selectAliasExprs = savedSelectAliasExprs;
            _currentLambdaParam = savedLambdaParam;
            RestoreClauseState(savedClauseState);

            return queryNode;
        }

        // Builder methods

        private LinqMethodCallNode BuildWhereMethod(
            SqlParserParser.ConditionContext conditionCtx,
            string lambdaParam) {

            _inWhereClause = true;
            var condNode = Visit(conditionCtx);
            _inWhereClause = false;

            return new LinqMethodCallNode {

                MethodName = "Where",
                Arguments = new List<LinqNode> {
                    new LinqLambdaNode { ParameterName = lambdaParam, Body = condNode }
                }
            };
        }

        private LinqNode VisitWhereCondition(SqlParserParser.ConditionContext ctx) {

            _inWhereClause = true;
            var node = Visit(ctx);
            _inWhereClause = false;

            return node;
        }

        private IEnumerable<LinqMethodCallNode> BuildOrderByMethods(
            SqlParserParser.OrderClauseContext orderClause,
            string lambdaParam) {

            var items = orderClause.orderItem();
            var methods = new List<LinqMethodCallNode>();

            for (int i = 0; i < items.Length; i++) {

                _inOrderBy = true;
                var itemNode = Visit(items[i].expr());
                _inOrderBy = false;

                bool isDesc = items[i].DESC() != null;
                string methodName = i == 0
                    ? (isDesc ? "OrderByDescending" : "OrderBy")
                    : (isDesc ? "ThenByDescending" : "ThenBy");

                methods.Add(new LinqMethodCallNode {

                    MethodName = methodName,
                    Arguments = new List<LinqNode> {
                        new LinqLambdaNode { ParameterName = lambdaParam, Body = itemNode }
                    }
                });
            }

            return methods;
        }

        private (string TableName, string TableAlias) SetupTableScope(
            SqlParserParser.TableRefContext tableRef) {

            string tableName = ToPascalCase(tableRef.tableName().GetText());
            string tableAlias = tableRef.alias() != null
                ? tableRef.alias().GetText()
                : tableName.Substring(0, 1).ToLower();

            _currentLambdaParam = "x";
            _tableAliases = new Dictionary<string, string> { [tableAlias] = tableName };
            _outerScopes.Push((_tableAliases, _currentLambdaParam));

            return (tableName, tableAlias);
        }

        private (bool InWhere, bool InAggregate, bool InOrderBy) SaveClauseState() =>
            (_inWhereClause, _inAggregate, _inOrderBy);

        private void ResetClauseState() {
            _inWhereClause = false;
            _inAggregate = false;
            _inOrderBy = false;
        }

        private void RestoreClauseState((bool InWhere, bool InAggregate, bool InOrderBy) saved) {
            _inWhereClause = saved.InWhere;
            _inAggregate = saved.InAggregate;
            _inOrderBy = saved.InOrderBy;
        }

        private LinqNode BuildInlineViewQuery(
            SqlParserParser.SelectStmtContext context,
            LinqInlineViewQueryNode viewQuery,
            string alias) {

            _tableAliases = new Dictionary<string, string> { [alias] = alias };
            _hasJoin = false;
            _currentLambdaParam = "x";

            // WHERE

            if (context.condition() != null) {
                viewQuery.Methods.Add(BuildWhereMethod(context.condition(), "x"));
            }

            // ORDER BY

            if (context.orderClause() != null) {
                foreach (var m in BuildOrderByMethods(context.orderClause(), "x")) {
                    viewQuery.Methods.Add(m);
                }
            }

            // SELECT columns

            var columnsNode = Visit(context.columnList());

            if (columnsNode is LinqAnonymousObjectNode anonNode) {

                viewQuery.Methods.Add(new LinqMethodCallNode {
                    MethodName = "Select",
                    Arguments = new List<LinqNode> {
                        new LinqLambdaNode { ParameterName = "x", Body = anonNode }
                    }
                });
            }

            // OFFSET / LIMIT

            if (context.offsetClause() != null) {

                viewQuery.Methods.Add(new LinqMethodCallNode {
                    MethodName = "Skip",
                    Arguments = new List<LinqNode> { Visit(context.offsetClause().expr()) }
                });
            }

            if (context.limitClause() != null) {

                viewQuery.Methods.Add(new LinqMethodCallNode {
                    MethodName = "Take",
                    Arguments = new List<LinqNode> { Visit(context.limitClause().expr()) }
                });
            }

            viewQuery.Methods.Add(new LinqMethodCallNode { MethodName = "ToList" });

            return viewQuery;
        }

        // JOIN chain construction 

        private void BuildJoinChain(SqlParserParser.FromClauseContext fromCtx, string baseAlias, LinqQueryNode queryNode) {

            var aliasAccessPrefix = new Dictionary<string, string> { [baseAlias] = baseAlias };

            string previousWrapper = null;
            int joinStepIndex = 0;

            foreach (var joinCtx in fromCtx.joinClause()) {

                joinStepIndex++;

                bool isRight = joinCtx.joinType() != null && joinCtx.joinType().RIGHT() != null;

                if (isRight) {

                    var joinRef = joinCtx.tableRef();
                    string joinTable = ToPascalCase(joinRef.tableName().GetText());
                    string joinAlias = joinRef.alias() != null
                        ? joinRef.alias().GetText()
                        : joinTable.Substring(0, 1).ToLower();

                    _tableAliases[joinAlias] = joinTable;

                    queryNode.SourceTable = joinTable;

                    aliasAccessPrefix.Clear();
                    aliasAccessPrefix[joinAlias] = joinAlias;
                    aliasAccessPrefix[baseAlias] = baseAlias;

                    string outerParam = previousWrapper ?? joinAlias;
                    bool outerIsWrapper = previousWrapper != null;

                    _joinKeyAliasMap = BuildJoinKeyAliasMap(aliasAccessPrefix, outerParam, outerIsWrapper, baseAlias);

                    var flattenResultSelector = new LinqAnonymousObjectNode();
                    flattenResultSelector.Properties.Add((null, new LinqParameterNode { Name = joinAlias }));
                    flattenResultSelector.Properties.Add((null, new LinqParameterNode { Name = baseAlias }));

                    var onCondition = joinCtx.condition();
                    if (!(onCondition is SqlParserParser.CompareConditionContext cmpCtx) || cmpCtx.op.GetText() != "=") {
                        throw new NotSupportedException(
                            "[ERROR] RIGHT JOIN ON condition must be a simple equality (a.Key = b.Key).");
                    }

                    var (outerKey, innerKey) = ResolveJoinKeysDirectional(cmpCtx, joinAlias, baseAlias);

                    queryNode.Methods.Add(new LinqLeftJoinNode {
                        InnerTable = _tableAliases[baseAlias],
                        OuterParam = joinAlias,
                        InnerParam = baseAlias,
                        OuterKey = outerKey,
                        InnerKey = innerKey,
                        ResultSelector = flattenResultSelector,
                        OuterAliases = new List<string> { joinAlias },
                    });

                    _joinKeyAliasMap = null;

                    string wrapperName = $"j{joinStepIndex}";
                    aliasAccessPrefix[joinAlias] = $"{wrapperName}.{joinAlias}";
                    aliasAccessPrefix[baseAlias] = $"{wrapperName}.{baseAlias}";
                    previousWrapper = wrapperName;

                } else {
                    AddJoinStep(joinCtx, joinStepIndex, baseAlias, aliasAccessPrefix, ref previousWrapper, queryNode);
                }
            }
        }

        private void AddJoinStep(
            SqlParserParser.JoinClauseContext joinCtx,
            int joinStepIndex,
            string baseAlias,
            Dictionary<string, string> aliasAccessPrefix,
            ref string previousWrapper,
            LinqQueryNode queryNode) {

            var joinRef = joinCtx.tableRef();

            string joinTable = ToPascalCase(joinRef.tableName().GetText());
            string joinAlias = joinRef.alias() != null
                ? joinRef.alias().GetText()
                : joinTable.Substring(0, 1).ToLower();

            _tableAliases[joinAlias] = joinTable;

            string outerParam = previousWrapper ?? baseAlias;
            bool outerIsWrapper = previousWrapper != null;

            _joinKeyAliasMap = BuildJoinKeyAliasMap(aliasAccessPrefix, outerParam, outerIsWrapper, joinAlias);

            var flattenResultSelector = BuildFlattenResultSelector(aliasAccessPrefix.Keys, joinAlias);

            bool isCross = joinCtx.joinType() != null && joinCtx.joinType().CROSS() != null;
            bool isLeft = joinCtx.joinType() != null && joinCtx.joinType().LEFT() != null;

            if (isCross) {
                queryNode.Methods.Add(new LinqCrossJoinNode {
                    InnerTable = joinTable,
                    OuterParam = outerParam,
                    InnerParam = joinAlias,
                    ResultSelector = flattenResultSelector,
                });
            } else if (isLeft) {
                AddLeftJoinStep(joinCtx, joinTable, joinAlias, outerParam, aliasAccessPrefix, flattenResultSelector, queryNode);
            } else {
                AddEquiJoinStep(joinCtx, joinTable, joinAlias, outerParam, aliasAccessPrefix, flattenResultSelector, queryNode);
            }

            _joinKeyAliasMap = null;

            previousWrapper = AdvanceAliasAccessPrefix(aliasAccessPrefix, joinAlias, joinStepIndex);
        }

        private void AddEquiJoinStep(
            SqlParserParser.JoinClauseContext joinCtx,
            string joinTable,
            string joinAlias,
            string outerParam,
            Dictionary<string, string> aliasAccessPrefix,
            LinqAnonymousObjectNode flattenResultSelector,
            LinqQueryNode queryNode) {

            var onCondition = joinCtx.condition();

            if (onCondition is SqlParserParser.CompareConditionContext cmpCtx && cmpCtx.op.GetText() == "=") {

                var (outerKey, innerKey) = ResolveJoinKeys(cmpCtx, joinAlias, aliasAccessPrefix);

                queryNode.Methods.Add(new LinqJoinNode {
                    InnerTable = joinTable,
                    OuterParam = outerParam,
                    InnerParam = joinAlias,
                    OuterKey = outerKey,
                    InnerKey = innerKey,
                    ResultSelector = flattenResultSelector,
                });

            } else {

                queryNode.Methods.Add(new LinqCrossJoinNode {
                    InnerTable = joinTable,
                    OuterParam = outerParam,
                    InnerParam = joinAlias,
                    ResultSelector = flattenResultSelector,
                });

                _joinKeyAliasMap = null;
                LinqNode filterExpr = Visit(onCondition);

                queryNode.Methods.Add(new LinqMethodCallNode {
                    MethodName = "Where",
                    Arguments = new List<LinqNode> {
                        new LinqLambdaNode { ParameterName = "x", Body = filterExpr }
                    }
                });
            }
        }

        private void AddLeftJoinStep(
            SqlParserParser.JoinClauseContext joinCtx,
            string joinTable,
            string joinAlias,
            string outerParam,
            Dictionary<string, string> aliasAccessPrefix,
            LinqAnonymousObjectNode flattenResultSelector,
            LinqQueryNode queryNode) {

            var onCondition = joinCtx.condition();

            if (!(onCondition is SqlParserParser.CompareConditionContext cmpCtx) || cmpCtx.op.GetText() != "=") {
                throw new NotSupportedException(
                    "[ERROR] LEFT JOIN ON condition must be a simple equality (a.Key = b.Key).");
            }

            var (outerKey, innerKey) = ResolveJoinKeys(cmpCtx, joinAlias, aliasAccessPrefix);

            queryNode.Methods.Add(new LinqLeftJoinNode {
                InnerTable = joinTable,
                OuterParam = outerParam,
                InnerParam = joinAlias,
                OuterKey = outerKey,
                InnerKey = innerKey,
                ResultSelector = flattenResultSelector,
                OuterAliases = aliasAccessPrefix.Values.ToList(),
            });
        }

        private (LinqNode OuterKey, LinqNode InnerKey) ResolveJoinKeys(
            SqlParserParser.CompareConditionContext cmpCtx,
            string joinAlias,
            Dictionary<string, string> aliasAccessPrefix) {

            string LeftAlias() =>
                cmpCtx.left is SqlParserParser.QualifiedColumnExprContext lq ? lq.identifier(0).GetText() : null;

            string RightAlias() =>
                cmpCtx.right is SqlParserParser.QualifiedColumnExprContext rq ? rq.identifier(0).GetText() : null;

            string leftAlias = LeftAlias();
            string rightAlias = RightAlias();

            bool leftIsKnown = leftAlias != null && aliasAccessPrefix.ContainsKey(leftAlias);
            bool rightIsKnown = rightAlias != null && aliasAccessPrefix.ContainsKey(rightAlias);
            bool leftIsNew = leftAlias == joinAlias;
            bool rightIsNew = rightAlias == joinAlias;

            if (leftIsKnown && rightIsNew) {
                return (Visit(cmpCtx.left), Visit(cmpCtx.right));
            }

            if (leftIsNew && rightIsKnown) {
                return (Visit(cmpCtx.right), Visit(cmpCtx.left));
            }

            throw new NotSupportedException(
                $"[ERROR] JOIN ON condition must directly compare a previously introduced " +
                $"table alias with the newly joined alias ('{joinAlias}'), " +
                $"e.g. '{joinAlias}.Key = otherAlias.Key'.");
        }

        // Used by RIGHT JOIN where we already know which side is outer and which is inner —
        // outerAlias is the right-hand (preserved) table, innerAlias is the left-hand table.
        private (LinqNode OuterKey, LinqNode InnerKey) ResolveJoinKeysDirectional(
            SqlParserParser.CompareConditionContext cmpCtx,
            string outerAlias,
            string innerAlias) {

            string LeftAlias() =>
                cmpCtx.left is SqlParserParser.QualifiedColumnExprContext lq ? lq.identifier(0).GetText() : null;

            string RightAlias() =>
                cmpCtx.right is SqlParserParser.QualifiedColumnExprContext rq ? rq.identifier(0).GetText() : null;

            if (LeftAlias() == outerAlias && RightAlias() == innerAlias) {
                return (Visit(cmpCtx.left), Visit(cmpCtx.right));
            }

            if (LeftAlias() == innerAlias && RightAlias() == outerAlias) {
                return (Visit(cmpCtx.right), Visit(cmpCtx.left));
            }

            throw new NotSupportedException(
                $"[ERROR] RIGHT JOIN ON condition must compare '{outerAlias}' with '{innerAlias}'.");
        }

        private Dictionary<string, string> BuildJoinKeyAliasMap(
            Dictionary<string, string> aliasAccessPrefix,
            string outerParam,
            bool outerIsWrapper,
            string joinAlias) {

            var map = new Dictionary<string, string>();

            foreach (var known in aliasAccessPrefix.Keys) {
                map[known] = outerIsWrapper ? $"{outerParam}.{known}" : known;
            }
            map[joinAlias] = joinAlias;

            return map;
        }

        private LinqAnonymousObjectNode BuildFlattenResultSelector(IEnumerable<string> knownAliases, string joinAlias) {

            var node = new LinqAnonymousObjectNode();

            foreach (var known in knownAliases) {
                node.Properties.Add((null, Path(_joinKeyAliasMap[known])));
            }
            node.Properties.Add((null, new LinqParameterNode { Name = joinAlias }));

            return node;
        }

        private string AdvanceAliasAccessPrefix(Dictionary<string, string> aliasAccessPrefix, string joinAlias, int joinStepIndex) {

            string wrapperName = $"j{joinStepIndex}";

            foreach (var known in aliasAccessPrefix.Keys.ToList()) {
                aliasAccessPrefix[known] = $"{wrapperName}.{known}";
            }
            aliasAccessPrefix[joinAlias] = $"{wrapperName}.{joinAlias}";

            return wrapperName;
        }

        // SELECT column list processing

        public override LinqNode VisitColumnList([NotNull] SqlParserParser.ColumnListContext context) {
            if (context.STAR() != null) return null;

            var anonNode = new LinqAnonymousObjectNode();
            foreach (var item in context.selectItem()) {

                var exprNode = Visit(item.expr());

                string aliasName = item.identifier() != null ? item.identifier().GetText() : null;

                if (aliasName == null && exprNode is LinqGroupKeyNode keyNode) {
                    if (keyNode.MemberName == null) {
                        if (item.expr() is SqlParserParser.ColumnExprContext colCtx2) {
                            aliasName = ToPascalCase(colCtx2.GetText());
                        }
                    } else {
                        aliasName = keyNode.MemberName;
                    }
                }

                anonNode.Properties.Add((aliasName, exprNode));
            }
            return anonNode;
        }

        // Aggregate and string function processing

        private static readonly System.Collections.Generic.HashSet<string> StringFunctions =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase) {
                "UPPER", "LOWER", "TRIM", "LTRIM", "RTRIM", "LENGTH", "LEN",
                "ABS", "FLOOR", "CEIL", "CEILING", "SQRT", "SIGN", "ROUND",
                "TRUNCATE", "LOG", "LOG10", "EXP",
                "COALESCE", "NULLIF", "ISNULL", "NVL",
                "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND",
                "SPACE", "REVERSE", "STR", "LCASE", "UCASE",
                "CHARINDEX", "INSTR"
            };

        private static readonly System.Collections.Generic.HashSet<string> AggregateFunctions =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase) {
                "COUNT", "SUM", "AVG", "MIN", "MAX"
            };

        public override LinqNode VisitAggregateExpr([NotNull] SqlParserParser.AggregateExprContext context) {

            string rawName = context.IDENTIFIER().GetText();

            if (StringFunctions.Contains(rawName)) {

                var node = new LinqStringFunctionNode { FunctionName = rawName.ToUpperInvariant() };

                if (context.expr() != null) node.Arguments.Add(Visit(context.expr()));
                return node;
            }

            string funcName = ToPascalCase(rawName);
            if (funcName == "Avg") funcName = "Average";

            if (!AggregateFunctions.Contains(rawName)) {
                throw new NotSupportedException(
                    $"[ERROR] Unsupported function: '{rawName}'. " +
                    $"Supported aggregates: COUNT, SUM, AVG, MIN, MAX. " +
                    $"Supported string/math/date functions: see documentation.");
            }

            if (funcName == "Count") {
                return new LinqAggregateNode { FunctionName = "Count", Argument = null };
            }

            LinqNode argNode = null;
            if (context.expr() != null) {
                _inAggregate = true;
                argNode = Visit(context.expr());
                _inAggregate = false;
            }

            return new LinqAggregateNode {
                FunctionName = funcName,
                Argument = argNode
            };
        }

        // Two or more argument functions

        private static readonly Dictionary<string, int> FuncArgCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {

                // String — 2 args

                { "NULLIF",     2 },
                { "CONCAT",     2 },
                { "CHARINDEX",  2 },
                { "INSTR",      2 },
                { "REPEAT",     2 },
                { "POWER",      2 },
                { "MOD",        2 },
                { "FORMAT",     2 },
                { "ISNULL",     2 },
                { "NVL",        2 },
                { "TRUNCATE",   2 },

                // String — 3 args

                { "SUBSTRING",  3 },
                { "REPLACE",    3 },
                { "DATEADD",    3 },
                { "DATEDIFF",   3 },
                { "DATEPART",   2 }, 

                // String — 4 args

                { "STUFF",      4 },
            };

        public override LinqNode VisitFuncCallExpr([NotNull] SqlParserParser.FuncCallExprContext context) {

            string funcName = context.IDENTIFIER().GetText().ToUpperInvariant();
            var args = context.expr();

            if (FuncArgCounts.TryGetValue(funcName, out int expected) && args.Length != expected) {
                throw new NotSupportedException(
                    $"[ERROR] {funcName} expects {expected} argument(s), got {args.Length}.");
            }

            var node = new LinqStringFunctionNode { FunctionName = funcName };
            foreach (var arg in args) {
                node.Arguments.Add(Visit(arg));
            }
            return node;
        }

        // Conditions processing

        public override LinqNode VisitCompareCondition([NotNull] SqlParserParser.CompareConditionContext context) {

            string op = context.op.GetText();

            bool isOrderingOp = op == ">" || op == "<" || op == ">=" || op == "<=";

            if (isOrderingOp && (context.left is SqlParserParser.StringExprContext || context.right is SqlParserParser.StringExprContext)) {
                throw new NotSupportedException(
                    $"[ERROR] Ordering operator '{op}' is not supported for string operands. " +
                    $"Strings can only be compared with '=' or '<>'.");
            }

            var linqOp = op switch {
                "=" => LinqBinaryOperator.Equal,
                "<>" => LinqBinaryOperator.NotEqual,
                ">" => LinqBinaryOperator.GreaterThan,
                "<" => LinqBinaryOperator.LessThan,
                ">=" => LinqBinaryOperator.GreaterThanOrEqual,
                "<=" => LinqBinaryOperator.LessThanOrEqual,
                _ => throw new NotSupportedException($"[ERROR] Unsupported comparison operator: '{op}'.")
            };

            return new LinqBinaryExpressionNode {
                Left = Visit(context.left),
                Operator = linqOp,
                Right = Visit(context.right)
            };
        }

        // LIKE condition processing, the pattern is kept in its SQL form

        public override LinqNode VisitLikeCondition([NotNull] SqlParserParser.LikeConditionContext context) {

            var leftNode = Visit(context.left);

            string pattern = context.right.Text.Trim('\'');

            LinqNode likeNode = new LinqLikeNode {
                Target = leftNode,
                Pattern = pattern
            };

            if (context.NOT() != null) {
                return Not(likeNode);
            }

            return likeNode;
        }

        // BETWEEN condition processing, converting to two comparisons joined with AND

        public override LinqNode VisitBetweenCondition([NotNull] SqlParserParser.BetweenConditionContext context) {

            if (context.low is SqlParserParser.StringExprContext || context.high is SqlParserParser.StringExprContext) {
                throw new NotSupportedException(
                    $"[ERROR] BETWEEN is not supported for string operands. " +
                    $"String ordering has no equivalent in LINQ.");
            }

            var leftNode = Visit(context.left);

            var lowerBound = new LinqBinaryExpressionNode {
                Left = leftNode,
                Operator = LinqBinaryOperator.GreaterThanOrEqual,
                Right = Visit(context.low)
            };

            var upperBound = new LinqBinaryExpressionNode {
                Left = leftNode,
                Operator = LinqBinaryOperator.LessThanOrEqual,
                Right = Visit(context.high)
            };

            LinqNode betweenExpr = new LinqBinaryExpressionNode {
                Left = lowerBound,
                Operator = LinqBinaryOperator.And,
                Right = upperBound
            };

            if (context.NOT() != null) {
                return Not(betweenExpr);
            }

            return betweenExpr;
        }

        // NOT condition processing

        public override LinqNode VisitNotCondition([NotNull] SqlParserParser.NotConditionContext context) {
            return Not(Visit(context.condition()));
        }

        // IS NULL / IS NOT NULL 

        public override LinqNode VisitIsNullCondition([NotNull] SqlParserParser.IsNullConditionContext context) {
            return new LinqBinaryExpressionNode {
                Left = Visit(context.left),
                Operator = context.NOT() != null ? LinqBinaryOperator.NotEqual : LinqBinaryOperator.Equal,
                Right = new LinqConstantNode { Value = null }
            };
        }

        // IN 

        public override LinqNode VisitInCondition([NotNull] SqlParserParser.InConditionContext context) {

            var inNode = new LinqInExpressionNode {
                Target = Visit(context.left)
            };

            foreach (var valueExpr in UnwrapParenExprList(context.parenExprList()).expr()) {
                inNode.Values.Add(Visit(valueExpr));
            }

            if (context.NOT() != null) {
                return Not(inNode);
            }

            return inNode;
        }

        // IN (SELECT ...)

        public override LinqNode VisitInSubqueryCondition([NotNull] SqlParserParser.InSubqueryConditionContext context) {

            var innerNode = Visit(UnwrapSubquery(context.subquery()));
            var subquery = new LinqSubqueryNode { Inner = innerNode, AsScalar = false };

            var inNode = new LinqInExpressionNode {
                Target = Visit(context.left),
                Subquery = subquery
            };

            if (context.NOT() != null) {
                return Not(inNode);
            }

            return inNode;
        }

        // EXISTS (SELECT ...) / NOT EXISTS (SELECT ...)

        public override LinqNode VisitExistsCondition([NotNull] SqlParserParser.ExistsConditionContext context) {

            var innerNode = Visit(UnwrapSubquery(context.subquery()));
            var subquery = new LinqSubqueryNode { Inner = innerNode, AsScalar = false };

            return new LinqExistsNode {
                Subquery = subquery,
                Negated = context.NOT() != null
            };
        }

        // Scalar subquery in expr position:  (SELECT MAX(Points) FROM Users)

        public override LinqNode VisitScalarSubqueryExpr([NotNull] SqlParserParser.ScalarSubqueryExprContext context) {

            var innerNode = Visit(UnwrapSubquery(context.subquery()));

            return new LinqSubqueryNode {
                Inner = innerNode,
                AsScalar = true
            };
        }

        public override LinqNode VisitBooleanColumnCondition([NotNull] SqlParserParser.BooleanColumnConditionContext context) {
            return Visit(context.expr());
        }

        // Zero-argument functions: GETDATE(), NOW(), CURDATE() etc.

        public override LinqNode VisitNoArgFuncExpr([NotNull] SqlParserParser.NoArgFuncExprContext context) {

            string funcName = context.IDENTIFIER().GetText().ToUpperInvariant();

            return funcName switch {
                "GETDATE" => new LinqDateConstantNode { Kind = "Now" },
                "NOW" => new LinqDateConstantNode { Kind = "Now" },
                "CURDATE" => new LinqDateConstantNode { Kind = "Date" },
                "GETUTCDATE" => new LinqDateConstantNode { Kind = "UtcNow" },
                "RAND" => new LinqStringFunctionNode { FunctionName = "RAND" },
                _ => throw new NotSupportedException(
                    $"[ERROR] Unsupported zero-argument function: '{funcName}'. " +
                    $"Supported: GETDATE, NOW, SYSDATETIME, GETUTCDATE, CURDATE, CURTIME, RAND.")
            };
        }

        // CURRENT_DATE keyword (SQL:1999, no parentheses)

        public override LinqNode VisitCurrentDateExpr([NotNull] SqlParserParser.CurrentDateExprContext context) {
            return new LinqDateConstantNode { Kind = "Date" };
        }

        // CURRENT_TIMESTAMP keyword (SQL:1999, no parentheses)

        public override LinqNode VisitCurrentTimestampExpr([NotNull] SqlParserParser.CurrentTimestampExprContext context) {
            return new LinqDateConstantNode { Kind = "Now" };
        }

        // TRUE / FALSE boolean literals

        public override LinqNode VisitTrueExpr([NotNull] SqlParserParser.TrueExprContext context) {
            return new LinqConstantNode { Value = true };
        }

        public override LinqNode VisitFalseExpr([NotNull] SqlParserParser.FalseExprContext context) {
            return new LinqConstantNode { Value = false };
        }
        public override LinqNode VisitNullExpr([NotNull] SqlParserParser.NullExprContext context) {
            return new LinqConstantNode { Value = null };
        }

        // CASE WHEN 

        public override LinqNode VisitCaseExprAlt([NotNull] SqlParserParser.CaseExprAltContext context) {
            return Visit(context.caseExpr());
        }

        public override LinqNode VisitCaseExpr([NotNull] SqlParserParser.CaseExprContext context) {

            var caseNode = new LinqCaseNode();

            var allExprs = context.expr();
            var conditions = context.condition();

            bool hasOperand = context.caseOperand != null;
            int thenOffset = hasOperand ? 1 : 0;

            if (hasOperand) {
                caseNode.Operand = Visit(allExprs[0]);
            }

            for (int i = 0; i < conditions.Length; i++) {
                caseNode.WhenClauses.Add(new LinqCaseWhenClause {
                    Condition = Visit(conditions[i]),
                    Result = Visit(allExprs[thenOffset + i])
                });
            }

            if (context.elseExpr != null) {
                caseNode.ElseExpression = Visit(context.elseExpr);
            }

            return caseNode;
        }

        public override LinqNode VisitCastExpr([NotNull] SqlParserParser.CastExprContext context) {

            LinqNode inner = Visit(context.expr());
            string typeName = context.castType().identifier().GetText().ToUpperInvariant();

            LinqCastType castType = typeName switch {
                "INT" or "INTEGER" => LinqCastType.Int,
                "BIGINT" => LinqCastType.Long,
                "FLOAT" or "REAL" => LinqCastType.Double,
                "DECIMAL" or "NUMERIC" => LinqCastType.Decimal,
                "VARCHAR" or "NVARCHAR" or "CHAR" or "TEXT" or "STRING" => LinqCastType.String,
                "BIT" or "BOOLEAN" => LinqCastType.Bool,
                "DATE" or "DATETIME" or "TIMESTAMP" => LinqCastType.DateTime,
                _ => throw new NotSupportedException($"[ERROR] Unsupported CAST target type: {typeName}")
            };

            return new LinqCastNode {
                Operand = inner,
                CastType = castType
            };
        }

        // AND condition processing

        public override LinqNode VisitAndCondition([NotNull] SqlParserParser.AndConditionContext context) {

            return new LinqBinaryExpressionNode {
                Left = Visit(context.left),
                Operator = LinqBinaryOperator.And,
                Right = Visit(context.right)
            };
        }

        // OR condition processing

        public override LinqNode VisitOrCondition([NotNull] SqlParserParser.OrConditionContext context) {
            return new LinqBinaryExpressionNode {
                Left = Visit(context.left),
                Operator = LinqBinaryOperator.Or,
                Right = Visit(context.right)
            };
        }

        // Parentheses condition processing, wrapping the inner condition in parentheses

        public override LinqNode VisitParensCondition([NotNull] SqlParserParser.ParensConditionContext context) {
            return new LinqParensNode {
                InnerNode = Visit(context.condition())
            };
        }

        // Mathematical expression processing

        public override LinqNode VisitMathExpr([NotNull] SqlParserParser.MathExprContext context) {
            return new LinqBinaryExpressionNode {
                Left = Visit(context.left),
                Operator = context.op.GetText() switch {
                    "+" => LinqBinaryOperator.Add,
                    "-" => LinqBinaryOperator.Subtract,
                    "*" => LinqBinaryOperator.Multiply,
                    "/" => LinqBinaryOperator.Divide,
                    var other => throw new NotSupportedException($"[ERROR] Unsupported arithmetic operator: '{other}'.")
                },
                Right = Visit(context.right)
            };
        }

        public override LinqNode VisitQualifiedColumnExpr([NotNull] SqlParserParser.QualifiedColumnExprContext context) {

            string tableAlias = context.identifier(0).GetText();
            string columnName = ToPascalCase(context.identifier(1).GetText());

            if (_joinKeyAliasMap != null && _joinKeyAliasMap.TryGetValue(tableAlias, out var accessPrefix)) {
                return Member(Path(accessPrefix), columnName);
            }

            if (_tableAliases.ContainsKey(tableAlias)) {
                if (_hasJoin) {
                    return Member(Path($"x.{tableAlias}"), columnName);
                }

                // Use the current lambda parameter, not the raw SQL alias.
                // The SQL alias (e.g. "s", "u") only exists in SQL syntax;
                // the generated lambda param is _currentLambdaParam ("x", "s", "o" etc.)

                return Member(Path(_currentLambdaParam), columnName);
            }

            foreach (var (outerAliases, outerParam) in _outerScopes) {
                if (outerAliases.ContainsKey(tableAlias)) {
                    LinqNode outerRow = outerParam == GroupElementScope
                        ? new LinqGroupElementNode()
                        : Path(outerParam);

                    return Member(outerRow, columnName);
                }
            }

            throw new NotSupportedException(
                $"[ERROR] Unknown table alias '{tableAlias}' in '{tableAlias}.{columnName}'.");
        }

        // Column expression processing, considering the GROUP BY context

        public override LinqNode VisitColumnExpr([NotNull] SqlParserParser.ColumnExprContext context) {

            string rawColumnName = ToPascalCase(context.GetText());

            if (_inOrderBy && _selectAliasExprs.TryGetValue(rawColumnName, out var aliasExpr)
                    && aliasExpr is not SqlParserParser.ColumnExprContext) {

                _inOrderBy = false;
                var resolvedNode = Visit(aliasExpr);
                _inOrderBy = true;
                return resolvedNode;
            }

            if (_inOrderBy && _selectAliases.ContainsKey(rawColumnName)) {
                rawColumnName = _selectAliases[rawColumnName];
            }

            if (_inWhereClause || _inAggregate) {
                return Member(Path(_currentLambdaParam), rawColumnName);
            }

            RuleContext current = context.Parent;
            bool insideGroupBySelect = false;

            while (current != null) {
                if (current is SqlParserParser.SelectStmtContext selectCtx) {
                    if (selectCtx.groupClause() != null) {

                        insideGroupBySelect = true;

                        var groupKeys = selectCtx.groupClause().groupItem()
                            .Select(item => {
                                var ids = item.identifier();
                                return ids.Length == 2
                                    ? ToPascalCase(ids[1].GetText())
                                    : ToPascalCase(ids[0].GetText());
                            }).ToList();

                        if (groupKeys.Count > 0) {

                            if (groupKeys.Contains(rawColumnName)) {
                                if (groupKeys.Count == 1) {
                                    return new LinqGroupKeyNode();
                                } else {
                                    return new LinqGroupKeyNode { MemberName = rawColumnName };
                                }
                            }
                        }
                    }
                    break;
                }
                current = current.Parent;
            }

            if (insideGroupBySelect) {
                return Member(new LinqGroupElementNode(), rawColumnName);
            }

            return Member(Path(_currentLambdaParam), rawColumnName);
        }

        // Number literal processing

        public override LinqNode VisitNumberExpr([NotNull] SqlParserParser.NumberExprContext context) {
            return new LinqConstantNode { Value = int.Parse(context.GetText()) };
        }

        public override LinqNode VisitFloatExpr([NotNull] SqlParserParser.FloatExprContext context) {
            return new LinqConstantNode { Value = double.Parse(context.GetText(), System.Globalization.CultureInfo.InvariantCulture) };
        }

        // COUNT(DISTINCT col), only supported inside a GROUP BY context.
        // Produces an aggregate node with the Distinct flag set.

        public override LinqNode VisitDistinctAggregateExpr([NotNull] SqlParserParser.DistinctAggregateExprContext context) {

            string rawName = context.IDENTIFIER().GetText().ToUpperInvariant();

            if (rawName != "COUNT") {
                throw new NotSupportedException(
                    $"[ERROR] DISTINCT is only supported inside COUNT(). Got: {rawName}(DISTINCT ...).");
            }

            if (!IsInsideGroupByContext(context)) {
                throw new NotSupportedException(
                    "[ERROR] COUNT(DISTINCT col) is only supported inside a GROUP BY query. " +
                    "Use SELECT COUNT(DISTINCT col) FROM ... GROUP BY ... instead.");
            }

            _inAggregate = true;
            LinqNode argNode = Visit(context.expr());
            _inAggregate = false;

            return new LinqAggregateNode {
                FunctionName = "Count",
                Argument = argNode,
                Distinct = true
            };
        }

        private static bool IsInsideGroupByContext(Antlr4.Runtime.ParserRuleContext context) {

            var current = context.Parent;

            while (current != null) {
                if (current is SqlParserParser.SelectStmtContext selectCtx &&
                    selectCtx.groupClause() != null) {
                    return true;
                }
                current = current.Parent as Antlr4.Runtime.ParserRuleContext;
            }
            return false;
        }

        // String literal processing

        public override LinqNode VisitStringExpr([NotNull] SqlParserParser.StringExprContext context) {
            return new LinqConstantNode { Value = context.GetText().Trim('\'') };
        }

        // String concatenation (a || b), represented in the same way as CONCAT(a, b)

        public override LinqNode VisitConcatExpr([NotNull] SqlParserParser.ConcatExprContext context) {

            var left = Visit(context.left);
            var right = Visit(context.right);

            return new LinqStringFunctionNode {
                FunctionName = "CONCAT",
                Arguments = new List<LinqNode> { left, right }
            };
        }
    }
}
