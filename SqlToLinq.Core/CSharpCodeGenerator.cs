using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SqlToLinq.Core {

    // Generates EF Core 8 compatible C# LINQ method syntax from the intermediate tree.
    // All C# specific syntax of the transpiler lives in this class.

    public class CSharpCodeGenerator : ICodeGenerator {

        public string Generate(LinqNode root) => root.Accept(this);

        private string Gen(LinqNode node) => node.Accept(this);

        // Query structure

        public string VisitQuery(LinqQueryNode node) {

            string result = $"db.{node.SourceTable}";

            foreach (var method in node.Methods) {
                result += Gen(method);
            }
            return result;
        }

        public string VisitInlineViewQuery(LinqInlineViewQueryNode node) {

            string result = $"({InlineViewInnerCode(node)})";

            foreach (var method in node.Methods) {
                result += Gen(method);
            }
            return result;
        }

        public string VisitMethodCall(LinqMethodCallNode node) {

            if (node.Arguments == null || node.Arguments.Count == 0) {
                return $".{node.MethodName}()";
            }

            var argsStr = string.Join(", ", node.Arguments.Select(Gen));
            return $".{node.MethodName}({argsStr})";
        }

        public string VisitLambda(LinqLambdaNode node) {
            return $"{node.ParameterName} => {Gen(node.Body)}";
        }

        public string VisitSetOperation(LinqSetOperationNode node) {
            return $"{Gen(node.Left)}.{node.MethodName}({Gen(node.Right)})";
        }

        // Joins

        public string VisitJoin(LinqJoinNode node) {

            return $".Join(db.{node.InnerTable}, " +
                   $"{node.OuterParam} => {Gen(node.OuterKey)}, " +
                   $"{node.InnerParam} => {Gen(node.InnerKey)}, " +
                   $"({node.OuterParam}, {node.InnerParam}) => {ResultSelectorCode(node)})";
        }

        // LEFT JOIN is expressed as GroupJoin + SelectMany(DefaultIfEmpty)

        public string VisitLeftJoin(LinqLeftJoinNode node) {

            string collectionParam = $"_{node.InnerParam}";
            string gj = "gj";

            string groupJoinResult = $"new {{ {node.OuterParam}, {collectionParam} }}";
            string selectManyResult = $"new {{ {gj}.{node.OuterParam}, {node.InnerParam} }}";

            if (node.ResultSelector is LinqAnonymousObjectNode anonNode) {

                var rewritten = anonNode.Properties.Select(p => {

                    string expr = Gen(p.Expression);

                    foreach (var alias in node.OuterAliases) {
                        if (expr == alias || expr.StartsWith(alias + ".")) {

                            expr = $"{gj}.{expr}";
                            break;
                        }
                    }
                    return string.IsNullOrEmpty(p.Name) ? expr : $"{p.Name} = {expr}";
                });
                selectManyResult = $"new {{ {string.Join(", ", rewritten)} }}";
            }

            return
                $".GroupJoin(db.{node.InnerTable}, " +
                $"{node.OuterParam} => {Gen(node.OuterKey)}, " +
                $"{node.InnerParam} => {Gen(node.InnerKey)}, " +
                $"({node.OuterParam}, {collectionParam}) => {groupJoinResult})" +
                $".SelectMany({gj} => {gj}.{collectionParam}.DefaultIfEmpty(), " +
                $"({gj}, {node.InnerParam}) => {selectManyResult})";
        }

        public string VisitCrossJoin(LinqCrossJoinNode node) {

            return $".SelectMany({node.OuterParam} => db.{node.InnerTable}, " +
                   $"({node.OuterParam}, {node.InnerParam}) => {ResultSelectorCode(node)})";
        }

        private string ResultSelectorCode(LinqJoinBaseNode node) {
            return node.ResultSelector != null
                ? Gen(node.ResultSelector)
                : $"new {{ {node.OuterParam}, {node.InnerParam} }}";
        }

        // References

        public string VisitParameter(LinqParameterNode node) => node.Name;

        public string VisitMemberAccess(LinqMemberAccessNode node) {
            return $"{Gen(node.Target)}.{node.MemberName}";
        }

        public string VisitGroupKey(LinqGroupKeyNode node) {
            return node.MemberName == null
                ? $"{node.GroupParameter}.Key"
                : $"{node.GroupParameter}.Key.{node.MemberName}";
        }

        public string VisitGroupElement(LinqGroupElementNode node) {
            return $"{node.GroupParameter}.FirstOrDefault()";
        }

        // Expressions

        public string VisitConstant(LinqConstantNode node) {

            if (node.Value is string str) {
                return $"\"{str}\"";
            }

            if (node.Value is bool b) {
                return b ? "true" : "false";
            }

            if (node.Value is double d) {
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return node.Value?.ToString() ?? "null";
        }

        public string VisitDateConstant(LinqDateConstantNode node) {
            return node.Kind switch {
                "Date" => "DateTime.Today",
                "UtcNow" => "DateTime.UtcNow",
                _ => "DateTime.Now"
            };
        }

        public string VisitBinaryExpression(LinqBinaryExpressionNode node) {
            return $"{Gen(node.Left)} {BinaryOperator(node.Operator)} {Gen(node.Right)}";
        }

        public string VisitUnaryExpression(LinqUnaryExpressionNode node) {
            return $"{UnaryOperator(node.Operator)}({Gen(node.Operand)})";
        }

        private static string BinaryOperator(LinqBinaryOperator op) => op switch {
            LinqBinaryOperator.Equal => "==",
            LinqBinaryOperator.NotEqual => "!=",
            LinqBinaryOperator.LessThan => "<",
            LinqBinaryOperator.LessThanOrEqual => "<=",
            LinqBinaryOperator.GreaterThan => ">",
            LinqBinaryOperator.GreaterThanOrEqual => ">=",
            LinqBinaryOperator.And => "&&",
            LinqBinaryOperator.Or => "||",
            LinqBinaryOperator.Add => "+",
            LinqBinaryOperator.Subtract => "-",
            LinqBinaryOperator.Multiply => "*",
            LinqBinaryOperator.Divide => "/",
            _ => throw new NotSupportedException($"[ERROR] Unsupported binary operator: {op}.")
        };

        private static string UnaryOperator(LinqUnaryOperator op) => op switch {
            LinqUnaryOperator.Not => "!",
            _ => throw new NotSupportedException($"[ERROR] Unsupported unary operator: {op}.")
        };

        public string VisitParens(LinqParensNode node) {
            return $"({Gen(node.InnerNode)})";
        }

        public string VisitAnonymousObject(LinqAnonymousObjectNode node) {

            var props = node.Properties.Select(p => {

                // If the property name is empty, the name is inferred from the expression

                if (string.IsNullOrEmpty(p.Name)) {
                    return Gen(p.Expression);
                }
                return $"{p.Name} = {Gen(p.Expression)}";
            });
            return $"new {{ {string.Join(", ", props)} }}";
        }

        public string VisitAggregate(LinqAggregateNode node) {

            if (node.Distinct) {
                return $"g.Select(x => {Gen(node.Argument)}).Distinct().Count()";
            }

            if (node.Argument == null) {
                return $"g.{node.FunctionName}()";
            }

            return $"g.{node.FunctionName}(x => {Gen(node.Argument)})";
        }

        public string VisitCase(LinqCaseNode node) {

            string elseStr = node.ElseExpression != null ? Gen(node.ElseExpression) : "null";
            string result = elseStr;

            for (int i = node.WhenClauses.Count - 1; i >= 0; i--) {

                var clause = node.WhenClauses[i];
                string condStr;

                if (node.Operand != null) {
                    condStr = $"{Gen(node.Operand)} == {Gen(clause.Condition)}";
                } else {
                    condStr = Gen(clause.Condition);
                }

                result = $"{condStr} ? {Gen(clause.Result)} : {result}";
            }

            return $"({result})";
        }

        public string VisitCast(LinqCastNode node) {

            string typeName = node.CastType switch {
                LinqCastType.Int => "int",
                LinqCastType.Long => "long",
                LinqCastType.Double => "double",
                LinqCastType.Decimal => "decimal",
                LinqCastType.String => "string",
                LinqCastType.Bool => "bool",
                LinqCastType.DateTime => "DateTime",
                _ => throw new NotSupportedException($"[ERROR] Unsupported CAST target type: {node.CastType}")
            };

            return $"({typeName})({Gen(node.Operand)})";
        }

        // Scalar functions

        public string VisitStringFunction(LinqStringFunctionNode node) {

            var args = node.Arguments;

            string arg0 = args.Count > 0 ? Gen(args[0]) : "";
            string arg1 = args.Count > 1 ? Gen(args[1]) : "";
            string arg2 = args.Count > 2 ? Gen(args[2]) : "";
            string arg3 = args.Count > 3 ? Gen(args[3]) : "";

            return node.FunctionName.ToUpperInvariant() switch {

                // Case conversion
                "UPPER" => $"{arg0}.ToUpper()",
                "LOWER" => $"{arg0}.ToLower()",
                "LCASE" => $"{arg0}.ToLower()",
                "UCASE" => $"{arg0}.ToUpper()",

                // Trimming
                "TRIM" => $"{arg0}.Trim()",
                "LTRIM" => $"{arg0}.TrimStart()",
                "RTRIM" => $"{arg0}.TrimEnd()",

                // String length
                "LENGTH" => $"{arg0}.Length",
                "LEN" => $"{arg0}.Length",

                // Substring / searching
                "SUBSTRING" => $"{arg0}.Substring({arg1} - 1, {arg2})",
                "CHARINDEX" => $"({arg1}).IndexOf({arg0}) + 1",
                "INSTR" => $"({arg0}).IndexOf({arg1}) + 1",

                // String modification
                "REPLACE" => $"{arg0}.Replace({arg1}, {arg2})",
                "STUFF" => $"({arg0}).Remove({arg1} - 1, {arg2}).Insert({arg1} - 1, {arg3})",
                "REVERSE" => $"new string(({arg0}).Reverse().ToArray())",

                // String construction (also used for the || operator)
                "CONCAT" => $"string.Concat({arg0}, {arg1})",
                "REPEAT" => $"string.Concat(System.Linq.Enumerable.Repeat({arg0}, {arg1}))",
                "SPACE" => $"new string(' ', {arg0})",

                // Conversion / formatting
                "STR" => $"({arg0}).ToString()",
                "FORMAT" => $"({arg0})!.ToString({arg1})",

                "COALESCE" => args.Count == 2
                        ? $"({arg0} ?? {arg1})"
                        : $"({string.Join(" ?? ", args.Select(Gen))})",

                "ISNULL" => $"({arg0} ?? {arg1})",
                "NVL" => $"({arg0} ?? {arg1})",
                "NULLIF" => $"({arg0} == {arg1} ? null : {arg0})",

                "ABS" => $"Math.Abs((int)({arg0}))",
                "MOD" => $"({arg0}) % ({arg1})",
                "SIGN" => $"Math.Sign((int)({arg0}))",

                "ROUND" => arg1 != ""
                    ? $"Math.Round((double)({arg0}), {arg1})"
                    : $"Math.Round((double)({arg0}))",

                "FLOOR" => $"Math.Floor((double)({arg0}))",
                "CEIL" => $"Math.Ceiling((double)({arg0}))",
                "CEILING" => $"Math.Ceiling((double)({arg0}))",
                "TRUNCATE" => $"Math.Floor((double)({arg0}) * Math.Pow(10, {arg1})) / Math.Pow(10, {arg1})",

                "POWER" => $"Math.Pow((double)({arg0}), (double)({arg1}))",
                "SQRT" => $"Math.Sqrt((double)({arg0}))",

                "EXP" => $"Math.Exp((double)({arg0}))",
                "LOG" => $"Math.Log((double)({arg0}))",
                "LOG10" => $"Math.Log10((double)({arg0}))",

                "RAND" => "new Random().NextDouble()",

                "YEAR" => $"({arg0}).Value.Year",
                "MONTH" => $"({arg0}).Value.Month",
                "DAY" => $"({arg0}).Value.Day",

                "HOUR" => $"({arg0}).Value.Hour",
                "MINUTE" => $"({arg0}).Value.Minute",
                "SECOND" => $"({arg0}).Value.Second",

                "DATEADD" => GenerateDateAdd(arg0, arg1, arg2),
                "DATEDIFF" => GenerateDateDiff(arg0, arg1, arg2),

                "DATEPART" => GenerateDatePart(arg0, arg1),

                _ => throw new NotSupportedException(

                    $"[ERROR] Unsupported function: '{node.FunctionName}'. " +
                    $"Supported string: UPPER, LOWER, TRIM, LTRIM, RTRIM, LENGTH, SUBSTRING, CONCAT, " +
                    $"COALESCE, NULLIF, REPLACE, LEFT, RIGHT, CHARINDEX, INSTR, REPEAT, REVERSE, SPACE, STR. " +
                    $"Supported math: ABS, FLOOR, CEIL, CEILING, ROUND, POWER, SQRT, SIGN, MOD. " +
                    $"Supported date: YEAR, MONTH, DAY, HOUR, MINUTE, SECOND, FORMAT, DATEADD, DATEDIFF, DATEPART.")
            };
        }

        private static string GenerateDateAdd(string part, string n, string date) {

            string p = part.Trim('"').Trim('\'').ToUpperInvariant()
                           .Replace("X.", "").Replace("_", "");
            return p switch {
                "YEAR" => $"({date}).Value.AddYears({n})",
                "MONTH" => $"({date}).Value.AddMonths({n})",
                "DAY" => $"({date}).Value.AddDays({n})",
                "HOUR" => $"({date}).Value.AddHours({n})",
                "MINUTE" => $"({date}).Value.AddMinutes({n})",
                "SECOND" => $"({date}).Value.AddSeconds({n})",
                _ => throw new NotSupportedException($"[ERROR] DATEADD: unsupported part '{part}'.")
            };
        }

        private static string GenerateDateDiff(string part, string start, string end) {

            string p = part.Trim('"').Trim('\'').ToUpperInvariant()
                           .Replace("X.", "").Replace("_", "");

            string s = IsDateTimeConstant(start) ? start : $"({start}).Value";
            string e = IsDateTimeConstant(end) ? $"({end})" : $"({end}).Value";

            return p switch {
                "YEAR" => $"({e}.Year - {s}.Year)",
                "MONTH" => $"(({e}.Year - {s}.Year) * 12 + {e}.Month - {s}.Month)",
                "DAY" => $"(int)({e} - {s}).TotalDays",
                "HOUR" => $"(int)({e} - {s}).TotalHours",
                "MINUTE" => $"(int)({e} - {s}).TotalMinutes",
                "SECOND" => $"(int)({e} - {s}).TotalSeconds",
                _ => throw new NotSupportedException($"[ERROR] DATEDIFF: unsupported part '{part}'.")
            };
        }

        private static bool IsDateTimeConstant(string expr) =>
            expr == "DateTime.Now" || expr == "DateTime.Today" || expr == "DateTime.UtcNow";

        private static string GenerateDatePart(string part, string date) {

            string p = part.Trim('"').Trim('\'').ToUpperInvariant()
                           .Replace("X.", "").Replace("_", "");
            return p switch {
                "YEAR" => $"({date}).Value.Year",
                "MONTH" => $"({date}).Value.Month",
                "DAY" => $"({date}).Value.Day",
                "HOUR" => $"({date}).Value.Hour",
                "MINUTE" => $"({date}).Value.Minute",
                "SECOND" => $"({date}).Value.Second",
                _ => throw new NotSupportedException($"[ERROR] DATEPART: unsupported part '{part}'.")
            };
        }

        // Conditions

        public string VisitLike(LinqLikeNode node) {
            string escapedPattern = node.Pattern?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
            return $"EF.Functions.Like({Gen(node.Target)}, \"{escapedPattern}\")";
        }

        public string VisitInExpression(LinqInExpressionNode node) {

            if (node.Subquery != null) {
                if (node.Subquery.Inner is LinqQueryNode qn) {

                    string baseQuery = QueryUpToSelect(qn);
                    var selectMethod = qn.Methods.OfType<LinqMethodCallNode>()
                        .FirstOrDefault(m => m.MethodName == "Select");

                    if (selectMethod?.Arguments.Count == 1 &&
                        selectMethod.Arguments[0] is LinqLambdaNode lambda) {

                        string param = lambda.ParameterName;
                        string colExpr = lambda.Body is LinqAnonymousObjectNode anon && anon.Properties.Count == 1
                            ? Gen(anon.Properties[0].Expression)
                            : Gen(lambda.Body);

                        return $"({baseQuery}).Any({param} => {colExpr} == {Gen(node.Target)})";
                    }
                }

                // Fallback

                string inner = ScalarInnerQuery(node.Subquery.Inner);
                return $"({inner}).Contains({Gen(node.Target)})";
            }

            var valuesStr = string.Join(", ", node.Values.Select(Gen));
            bool allNumeric = node.Values.All(v => v is LinqConstantNode c && c.Value is not string);
            string arrayType = allNumeric ? "int?" : "string";

            return $"new {arrayType}[] {{ {valuesStr} }}.Contains({Gen(node.Target)})";
        }

        public string VisitExists(LinqExistsNode node) {

            string inner = node.Subquery.Inner is LinqQueryNode qn
                ? QueryUpToSelect(qn)
                : Gen(node.Subquery.Inner);

            string anyCall = $"({inner}).Any()";
            return node.Negated ? $"!{anyCall}" : anyCall;
        }

        public string VisitSubquery(LinqSubqueryNode node) {

            string inner = Gen(node.Inner);

            if (!node.AsScalar) return inner;

            bool alreadyScalar = inner.EndsWith(".Count()")
                || Regex.IsMatch(inner, @"\.(Sum|Max|Min|Average)\([^)]*\)$");

            return alreadyScalar ? $"({inner})" : $"({inner}).FirstOrDefault()";
        }

        // Data manipulation

        public string VisitDelete(LinqDeleteNode node) {

            if (node.WhereCondition != null) {
                return $"db.{node.SourceTable}.Where({node.LambdaParam} => {Gen(node.WhereCondition)}).ExecuteDeleteAsync()";
            }

            return $"db.{node.SourceTable}.ExecuteDeleteAsync()";
        }

        public string VisitUpdate(LinqUpdateNode node) {

            var setProps = string.Join("\n", node.Assignments.Select(a => SetPropertyCode(node, a.ColumnName, a.Value)));

            string executeUpdate = $"ExecuteUpdateAsync(s => s\n{setProps})";

            if (node.WhereCondition != null) {
                return $"db.{node.SourceTable}.Where({node.LambdaParam} => {Gen(node.WhereCondition)}).{executeUpdate}";
            }

            return $"db.{node.SourceTable}.{executeUpdate}";
        }

        private string SetPropertyCode(LinqUpdateNode node, string columnName, LinqNode value) {

            string colLambda = $"{node.LambdaParam} => {node.LambdaParam}.{columnName}";

            if (value is LinqConstantNode { Value: null }) {
                return $".SetProperty({colLambda}, x => null)";
            }

            if (ReferencesColumn(value)) {
                return $".SetProperty({colLambda}, {node.LambdaParam} => {Gen(value)})";
            }

            return $".SetProperty({colLambda}, {Gen(value)})";
        }

        // Returns true if the node contains any column reference,
        // meaning the new value depends on the current row.

        private static bool ReferencesColumn(LinqNode node) {

            return node switch {
                LinqParameterNode => true,
                LinqMemberAccessNode => true,
                LinqGroupKeyNode => true,
                LinqGroupElementNode => true,
                LinqBinaryExpressionNode b => ReferencesColumn(b.Left) || ReferencesColumn(b.Right),
                LinqUnaryExpressionNode u => ReferencesColumn(u.Operand),
                LinqParensNode p => ReferencesColumn(p.InnerNode),
                LinqStringFunctionNode f => f.Arguments.Any(ReferencesColumn),
                LinqCastNode c => ReferencesColumn(c.Operand),
                LinqCaseNode c => c.WhenClauses.Any(w =>
                                                 ReferencesColumn(w.Condition) ||
                                                 ReferencesColumn(w.Result)) ||
                                             (c.ElseExpression != null && ReferencesColumn(c.ElseExpression)) ||
                                             (c.Operand != null && ReferencesColumn(c.Operand)),
                _ => false
            };
        }

        public string VisitInsert(LinqInsertNode node) {

            string MakeInit(List<(string ColumnName, LinqNode Value)> row) {
                var props = string.Join(", ", row.Select(p => $"{p.ColumnName} = {Gen(p.Value)}"));
                return $"new {node.SourceTable} {{ {props} }}";
            }

            if (node.Rows.Count == 1) {
                string init = MakeInit(node.Rows[0]);
                return $"db.{node.SourceTable}.Add({init});\ndb.SaveChanges();";
            }

            var items = string.Join($",\n    ", node.Rows.Select(MakeInit));
            return $"db.{node.SourceTable}.AddRange(new List<{node.SourceTable}>\n{{\n{items}\n}});\ndb.SaveChanges();";
        }

        public string VisitInsertSelect(LinqInsertSelectNode node) {

            bool hasColumns = node.ColumnNames != null && node.ColumnNames.Count > 0;

            if (hasColumns && node.SelectQuery is LinqQueryNode qn) {

                int selectIdx = -1;
                for (int i = qn.Methods.Count - 1; i >= 0; i--) {
                    if (qn.Methods[i] is LinqMethodCallNode m && m.MethodName == "Select") {
                        selectIdx = i;
                        break;
                    }
                }

                if (selectIdx >= 0 &&
                    qn.Methods[selectIdx] is LinqMethodCallNode selectMethod &&
                    selectMethod.Arguments.Count == 1 &&
                    selectMethod.Arguments[0] is LinqLambdaNode lambda &&
                    lambda.Body is LinqAnonymousObjectNode anon &&
                    anon.Properties.Count == node.ColumnNames.Count) {

                    string param = lambda.ParameterName;
                    string baseChain = $"db.{qn.SourceTable}";

                    for (int i = 0; i < selectIdx; i++) {
                        baseChain += Gen(qn.Methods[i]);
                    }

                    var assignments = node.ColumnNames
                        .Zip(anon.Properties, (col, prop) => $"{col} = {Gen(prop.Expression)}")
                        .ToList();

                    string init = $"new {node.TargetTable} {{ {string.Join(", ", assignments)} }}";

                    return $"db.{node.TargetTable}.AddRange(({baseChain}).Select({param} => {init}).ToList());\ndb.SaveChanges();";
                }
            }

            string inner = StripToList(Gen(node.SelectQuery));

            return $"db.{node.TargetTable}.AddRange(({inner}).ToList());\ndb.SaveChanges();";
        }

        // Helpers

        private static string StripToList(string code) {
            return code.EndsWith(".ToList()")
                ? code.Substring(0, code.Length - ".ToList()".Length)
                : code;
        }

        private string InlineViewInnerCode(LinqInlineViewQueryNode node) {
            return StripToList(Gen(node.Inner));
        }

        // The query chain up to (but not including) the first Select or ToList call

        private string QueryUpToSelect(LinqQueryNode node) {

            string result = $"db.{node.SourceTable}";

            foreach (var method in node.Methods) {
                if (method is LinqMethodCallNode m &&
                    (m.MethodName == "Select" || m.MethodName == "ToList")) break;
                result += Gen(method);
            }
            return result;
        }

        // The inner query of an IN subquery, with a single column projection
        // turned into a scalar projection so that Contains() can be applied

        private string ScalarInnerQuery(LinqNode inner) {

            if (inner is LinqInlineViewQueryNode iv) {

                var ivSb = new StringBuilder($"({InlineViewInnerCode(iv)})");

                foreach (var method in iv.Methods) {
                    if (method is LinqMethodCallNode m) {
                        if (m.MethodName == "ToList") break;

                        if (m.MethodName == "Select" &&
                            m.Arguments.Count == 1 &&
                            m.Arguments[0] is LinqLambdaNode ivLambda &&
                            ivLambda.Body is LinqAnonymousObjectNode ivAnon &&
                            ivAnon.Properties.Count == 1) {

                            ivSb.Append($".Select({ivLambda.ParameterName} => {Gen(ivAnon.Properties[0].Expression)})");
                            continue;
                        }
                    }
                    ivSb.Append(Gen(method));
                }
                return ivSb.ToString();
            }

            if (inner is LinqQueryNode qn) {
                var sb = new StringBuilder($"db.{qn.SourceTable}");

                LinqMethodCallNode scalarSelect = null;

                foreach (var method in qn.Methods) {
                    if (method is LinqMethodCallNode m) {
                        if (m.MethodName == "ToList") break;
                        if (m.MethodName == "Select") {
                            scalarSelect = m;
                            break;
                        }
                    }

                    sb.Append(Gen(method));
                }

                if (scalarSelect?.Arguments.Count == 1 &&
                    scalarSelect.Arguments[0] is LinqLambdaNode lambda &&
                    lambda.Body is LinqAnonymousObjectNode anon &&
                    anon.Properties.Count == 1) {
                    string param = lambda.ParameterName;
                    string colExpr = Gen(anon.Properties[0].Expression);

                    sb.Append($".Select({param} => {colExpr})");
                }

                return sb.ToString();
            }

            string code = Gen(inner);
            return code.Substring(0, code.Length - ".ToList()".Length);
        }
    }
}
