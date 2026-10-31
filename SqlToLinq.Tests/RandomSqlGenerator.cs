using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace SqlToLinq.Tests {

    public class RandomSqlGenerator {

        private readonly Random _rng;

        private record TableSpec(
            string Table,
            string Alias,
            string JoinClause,
            string JoinType,
            string[] StringCols,
            string[] NumericCols
        );

        private static readonly TableSpec[] Chain = {
            new("Users", "u", null, null,
                new[] { "u.Name", "u.Role" },
                new[] { "u.Age", "u.Points", "u.Bonus", "u.Id" }),

            new("Orders", "o", "Orders o ON u.id = o.owner", null,
                new[] { "o.Item" },
                new[] { "o.Qty", "o.Id" }),

            new("Products", "p", "Products p ON o.id = p.parent", null,
                new[] { "p.Title" },
                new[] { "p.Price", "p.Id" }),
        };

        private static readonly string[] JoinTypes = { "JOIN" };

        private static readonly string[] StringOps = { "=", "<>" };
        private static readonly string[] NumericOps = { "=", "<>", ">=", "<=", ">", "<" };

        private static readonly string[] LikePatterns = { "B%", "_ob", "B_b", "%o%", "Ali%", "B.b", "%", "A%", "%in" };

        private static readonly string[] StringValues = { "Bob", "Alice", "Admin", "User", "Moderator", "Bab", "Bcb" };
        private static readonly string[] NumericValues = { "0", "5", "10", "15", "20", "50", "80", "100", "200" };

        private static readonly string[] RoleValues = { "Admin", "User", "Moderator" };
        private static readonly string[] NameValues = { "Bob", "Alice", "Bab" };

        private TableSpec[] _activeTables = null;

        public RandomSqlGenerator(int seed) {
            _rng = new Random(seed);
        }

        public string NextSelect() {

            int tableCount = _rng.Next(10) switch {
                < 5 => 1,
                < 8 => 2,
                _ => 3,
            };

            _activeTables = Chain.Take(tableCount).ToArray();

            bool hasJoin = tableCount > 1;
            bool distinct = !hasJoin && _rng.Next(4) == 0;
            bool hasGroupBy = !hasJoin && !distinct && _rng.Next(3) == 0;
            bool asFromSubquery = !hasJoin && !distinct && !hasGroupBy && _rng.Next(8) == 0;

            if (asFromSubquery) return RandomFromSubquerySelect();

            string columns = BuildColumnList(distinct, hasGroupBy);

            bool addScalarSubquery = !hasJoin && !hasGroupBy && !distinct
                                     && columns != "*" && _rng.Next(5) == 0;

            if (addScalarSubquery) {
                columns += ", " + RandomScalarSubqueryColumn();
            }

            var sql = new StringBuilder("SELECT ");

            if (distinct) sql.Append("DISTINCT ");

            sql.Append(columns);
            sql.Append($" FROM {_activeTables[0].Table} {_activeTables[0].Alias}");

            if (hasJoin) {
                for (int i = 1; i < _activeTables.Length; i++) {
                    string joinType = Pick(JoinTypes);
                    sql.Append($" {joinType} {_activeTables[i].JoinClause}");
                }
            }

            if (_rng.Next(2) == 0) {
                sql.Append($" WHERE {RandomCondition(depth: 0)}");
            }

            if (hasGroupBy) {
                string groupCol = Pick(UnqualifiedColumns());
                sql.Append($" GROUP BY {groupCol}");
                if (_rng.Next(2) == 0) {
                    sql.Append($" HAVING COUNT(*) {Pick(NumericOps)} {_rng.Next(1, 6)}");
                }
            }

            if (_rng.Next(2) == 0) {

                string orderCol = hasJoin ? Pick(AllColumns()) : Pick(UnqualifiedColumns());

                if (distinct && columns != "*") {

                    var selectCols = columns.Split(',')
                        .Select(c => c.Trim().Split(' ')[0])
                        .Where(c => !c.StartsWith("CASE")
                                    && !c.Contains("(")
                                    && !c.Contains(")")
                                    && !c.All(char.IsDigit))
                        .ToArray();

                    if (selectCols.Length > 0) orderCol = Pick(selectCols);
                }

                sql.Append($" ORDER BY {orderCol} {(_rng.Next(2) == 0 ? "ASC" : "DESC")}");
            }

            if (_rng.Next(3) == 0) {
                sql.Append($" LIMIT {_rng.Next(1, 5)}");
                if (_rng.Next(2) == 0) {
                    sql.Append($" OFFSET {_rng.Next(0, 3)}");
                }
            }

            return sql.ToString() + ";";
        }

        private string RandomScalarSubqueryColumn() {

            string alias = $"Sub{_rng.Next(1, 4)}";
            string[] aggregates = { "COUNT(*)", "MAX(Age)", "MIN(Age)", "MAX(Points)", "MIN(Points)" };
            string agg = Pick(aggregates);

            return $"(SELECT {agg} FROM Users) AS {alias}";
        }

        private string RandomFromSubquerySelect() {

            string innerCol1 = Pick(new[] { "Name", "Age", "Role", "Points", "Bonus" });
            string[] col2Options = new[] { "Age", "Points", "Id" }.Where(c => c != innerCol1).ToArray();

            string innerCol2 = Pick(col2Options);
            string innerCols = $"{innerCol1}, {innerCol2}";

            var inner = new StringBuilder($"SELECT {innerCols} FROM Users u");

            if (_rng.Next(2) == 0) {
                inner.Append($" WHERE {RandomCompareCondition()}");
            }

            if (_rng.Next(2) == 0) {
                string orderCol = _rng.Next(2) == 0 ? innerCol1 : innerCol2;
                inner.Append($" ORDER BY {orderCol} {(_rng.Next(2) == 0 ? "ASC" : "DESC")}");
            }

            if (_rng.Next(3) == 0) {
                inner.Append($" LIMIT {_rng.Next(1, 5)}");
            }

            string outerCol = _rng.Next(2) == 0 ? innerCol1 : innerCol2;
            var outer = new StringBuilder($"SELECT {outerCol} FROM ({inner}) AS sub");

            if (_rng.Next(2) == 0) {

                string filterCol = _rng.Next(2) == 0 ? innerCol1 : innerCol2;
                bool isString = filterCol == "Name" || filterCol == "Role";
                string filterExpr = isString
                    ? $"{filterCol} {Pick(StringOps)} '{Pick(StringValues)}'"
                    : $"{filterCol} {Pick(NumericOps)} {Pick(NumericValues)}";
                outer.Append($" WHERE {filterExpr}");
            }

            if (_rng.Next(2) == 0) {
                outer.Append($" ORDER BY {outerCol} {(_rng.Next(2) == 0 ? "ASC" : "DESC")}");
            }

            if (_rng.Next(3) == 0) {
                outer.Append($" LIMIT {_rng.Next(1, 5)}");
            }

            return outer.ToString() + ";";
        }

        private string[] StringColumns() {
            bool hasJoin = _activeTables.Length > 1;
            return _activeTables.SelectMany(t => hasJoin
                ? t.StringCols
                : t.StringCols.Select(c => c.Split('.')[1]).ToArray()).ToArray();
        }

        private string[] NumericColumns() {
            bool hasJoin = _activeTables.Length > 1;
            return _activeTables.SelectMany(t => hasJoin
                ? t.NumericCols
                : t.NumericCols.Select(c => c.Split('.')[1]).ToArray()).ToArray();
        }

        private string[] AllColumns() =>
            StringColumns().Concat(NumericColumns()).ToArray();

        private string[] UnqualifiedColumns() =>
            AllColumns().Select(c => c.Contains('.') ? c.Split('.')[1] : c).ToArray();

        private string BuildColumnList(bool distinct, bool hasGroupBy = false) {

            bool hasJoin = _activeTables.Length > 1;

            if (!hasJoin && !distinct && !hasGroupBy && _rng.Next(3) == 0) return "*";

            var cols = RandomSubset(AllColumns()).ToList();

            if (hasJoin) {
                var seen = new HashSet<string>();
                cols = cols.Where(c => seen.Add(c.Contains('.') ? c.Split('.')[1] : c)).ToList();
                if (cols.Count == 0) cols.Add(AllColumns()[0]);
            }

            if (!hasJoin && _rng.Next(4) == 0 && cols.Count > 0) {

                int idx = _rng.Next(cols.Count);
                cols[idx] = $"{RandomCaseExpr()} AS CaseResult";
            }

            return string.Join(", ", cols);
        }

        private string RandomCondition(int depth) {

            if (depth >= 2 || _rng.Next(3) == 0) return RandomSimpleCondition();

            if (_rng.Next(5) == 0) return $"NOT ({RandomCondition(depth + 1)})";

            string left = RandomCondition(depth + 1);
            string right = RandomCondition(depth + 1);

            string op = _rng.Next(2) == 0 ? "AND" : "OR";

            return _rng.Next(2) == 0 ? $"({left} {op} {right})" : $"{left} {op} {right}";
        }

        private string RandomSimpleCondition() {

            bool canSubquery = _activeTables.Length == 1;

            int bound = canSubquery ? 13 : 10;
            int kind = _rng.Next(bound);

            if (kind < 4) return RandomCompareCondition();
            if (kind < 6) return RandomLikeCondition();
            if (kind < 8) return RandomBetweenCondition();
            if (kind < 9) return RandomInCondition();
            if (kind < 10) return RandomIsNullCondition();
            if (kind < 11) return RandomInSubqueryCondition();
            if (kind < 12) return RandomExistsCondition(correlated: false);
            return RandomExistsCondition(correlated: true);
        }

        private string RandomInSubqueryCondition() {

            string not = _rng.Next(3) == 0 ? "NOT " : "";
            string alias = "s";
            string innerWhere = _rng.Next(2) == 0
                ? $" WHERE {alias}.Qty {Pick(NumericOps)} {_rng.Next(1, 3)}"
                : "";

            return $"Id {not}IN (SELECT {alias}.Owner FROM Orders {alias}{innerWhere})";
        }

        private string RandomExistsCondition(bool correlated) {

            string not = _rng.Next(3) == 0 ? "NOT " : "";
            string alias = "s";

            string innerWhere;

            if (correlated) {

                string outerAlias = _activeTables[0].Alias;
                string extra = _rng.Next(2) == 0
                    ? $" AND {alias}.Qty {Pick(NumericOps)} {_rng.Next(1, 3)}"
                    : "";
                innerWhere = $"WHERE {alias}.Owner = {outerAlias}.Id{extra}";
            } else {
                innerWhere = $"WHERE {alias}.Owner {Pick(NumericOps)} {_rng.Next(1, 7)}";
            }

            return $"{not}EXISTS (SELECT 1 FROM Orders {alias} {innerWhere})";
        }

        private string RandomCompareCondition() {

            if (_rng.Next(2) == 0) {

                string col = Pick(StringColumns());

                return $"{col} {Pick(StringOps)} '{Pick(StringValues)}'";
            } else {

                string col = Pick(NumericColumns());
                string val = col is "u.Age" ? _rng.Next(15, 50).ToString() : Pick(NumericValues);

                return $"{col} {Pick(NumericOps)} {val}";
            }
        }

        private string RandomLikeCondition() {

            string col = Pick(StringColumns());
            string not = _rng.Next(3) == 0 ? "NOT " : "";

            return $"{col} {not}LIKE '{Pick(LikePatterns)}'";
        }

        private string RandomBetweenCondition() {

            string not = _rng.Next(3) == 0 ? "NOT " : "";
            string col = Pick(NumericColumns());
            int a = _rng.Next(0, 100), b = _rng.Next(0, 100);

            return $"{col} {not}BETWEEN {Math.Min(a, b)} AND {Math.Max(a, b)}";
        }

        private string RandomInCondition() {

            string not = _rng.Next(3) == 0 ? "NOT " : "";

            if (_rng.Next(2) == 0) {

                string col = Pick(StringColumns());

                string[] pool = col == "u.Role" ? RoleValues
                              : col == "u.Name" ? NameValues
                              : StringValues;

                var values = RandomSubset(pool).Select(v => $"'{v}'");
                string list = string.Join(", ", values);

                if (string.IsNullOrEmpty(list)) list = "'Admin'";

                return $"{col} {not}IN ({list})";
            } else {

                string col = Pick(NumericColumns());
                int count = _rng.Next(1, 4);

                string list = string.Join(", ", Enumerable.Range(0, count).Select(_ => _rng.Next(0, 200).ToString()));

                return $"{col} {not}IN ({list})";
            }
        }

        private string RandomIsNullCondition() {

            string col = Pick(StringColumns());
            string not = _rng.Next(2) == 0 ? "NOT " : "";

            return $"{col} IS {not}NULL";
        }

        private string RandomCaseExpr() {

            int branches = _rng.Next(1, 3);
            var sb = new StringBuilder("CASE");

            bool useBool = _rng.Next(5) == 0;
            bool useNumeric = !useBool && _rng.Next(2) == 0;

            for (int i = 0; i < branches; i++) {

                string cond = RandomSimpleCondition();
                string result = useBool ? (_rng.Next(2) == 0 ? "TRUE" : "FALSE")
                              : useNumeric ? _rng.Next(0, 100).ToString()
                              : $"'{Pick(StringValues)}'";

                sb.Append($" WHEN {cond} THEN {result}");
            }

            string elseVal = useBool ? (_rng.Next(2) == 0 ? "TRUE" : "FALSE")
                           : useNumeric ? _rng.Next(0, 100).ToString()
                           : $"'{Pick(StringValues)}'";

            sb.Append($" ELSE {elseVal} END");

            return sb.ToString();
        }

        private IEnumerable<string> RandomSubset(string[] items) {

            var subset = items.Where(_ => _rng.Next(2) == 0).ToList();
            return subset.Count > 0 ? subset : new List<string> { Pick(items) };
        }

        private IEnumerable<string> RandomSubset(IEnumerable<string> items) =>
            RandomSubset(items.ToArray());

        public string NextDelete() {

            _activeTables = new[] { Chain[0] };

            var sql = new StringBuilder("DELETE FROM Users");

            if (_rng.Next(2) == 0) {
                sql.Append($" WHERE {RandomCondition(depth: 0)}");
            }

            return sql.ToString() + ";";
        }

        public string NextInsert() {

            _activeTables = new[] { Chain[0] };

            var columns = new List<string>();
            var values = new List<string>();

            string[] stringCols = { "Name", "Role" };
            string[] numericCols = { "Age", "Points", "Bonus" };

            foreach (var col in stringCols) {
                if (_rng.Next(2) == 0) {

                    columns.Add(col);
                    values.Add($"'{Pick(StringValues)}'");
                }
            }

            foreach (var col in numericCols) {
                if (_rng.Next(2) == 0) {

                    columns.Add(col);
                    bool useNull = _rng.Next(5) == 0;
                    values.Add(useNull ? "NULL" : Pick(NumericValues));
                }
            }

            if (columns.Count == 0) {
                columns.Add("Name");
                values.Add($"'{Pick(StringValues)}'");
            }

            int rowCount = _rng.Next(3) == 0 ? _rng.Next(2, 4) : 1;

            if (rowCount == 1) {
                return $"INSERT INTO Users ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)});";
            }

            var rows = new List<string> { $"({string.Join(", ", values)})" };
            for (int i = 1; i < rowCount; i++) {

                var extraVals = columns.Select(col => {
                    if (col == "Name" || col == "Role") return $"'{Pick(StringValues)}'";
                    if (_rng.Next(5) == 0) return "NULL";
                    return Pick(NumericValues);
                }).ToList();

                rows.Add($"({string.Join(", ", extraVals)})");
            }

            return $"INSERT INTO Users ({string.Join(", ", columns)}) VALUES {string.Join(", ", rows)};";
        }

        public string NextUpdate() {

            _activeTables = new[] { Chain[0] };

            var assignments = new List<string>();

            if (_rng.Next(2) == 0) {
                assignments.Add($"Role = '{Pick(RoleValues)}'");
            }

            if (_rng.Next(2) == 0) {

                bool useExpr = _rng.Next(3) == 0;
                string val = useExpr ? $"Points + {_rng.Next(1, 50)}" : Pick(NumericValues);
                assignments.Add($"Points = {val}");
            }

            if (_rng.Next(2) == 0) {

                bool useNull = _rng.Next(4) == 0;
                string val = useNull ? "NULL" : Pick(NumericValues);
                assignments.Add($"Bonus = {val}");
            }

            if (assignments.Count == 0) {
                assignments.Add($"Role = '{Pick(RoleValues)}'");
            }

            var sql = new StringBuilder($"UPDATE Users SET {string.Join(", ", assignments)}");

            if (_rng.Next(2) == 0) {
                sql.Append($" WHERE {RandomCondition(depth: 0)}");
            }

            return sql.ToString() + ";";
        }

        private string Pick(string[] items) => items[_rng.Next(items.Length)];
    }
}