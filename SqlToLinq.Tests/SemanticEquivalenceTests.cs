using System;
using System.IO;
using System.Linq;
using SqlToLinq.Core;
using NUnit.Framework;
using System.Reflection;
using System.Collections;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;

namespace SqlToLinq.Tests {

    /// <summary>
    /// Semantic (execution-based) equivalence test: it executes every SqlInput
    /// in the JSON both as raw SQL AND as the LINQ generated from it
    /// on the same seeded SQLite in-memory database, then compares the two
    /// result sets. This test does not use the ExpectedLinq field,
    /// the string-based regression test is provided by TranspilerTests.
    /// </summary>

    [TestFixture]
    public class SemanticEquivalenceTests {

        // Cases are skipped when their SemanticSkip field is set in IOPairs.json.
        // The reasons are: functions the SQLite provider does not support
        // (sqlite-date, sqlite-function), the different handling of boolean
        // literals (sqlite-bool), non-deterministic results (nondeterministic)
        // and data manipulation statements (dml), which are verified by
        // EfCoreEdgeCaseTests. The transpiler output of every skipped case is
        // still verified by TranspilerTests.

        public static IEnumerable<TestCaseData> GetSemanticTestCases() {

            foreach (var tc in TranspilerTests.LoadTestCases()) {

                yield return new TestCaseData(tc.SqlInput, tc.SemanticSkip)
                    .SetName(TranspilerTests.TestName(tc));
            }
        }

        private SqliteConnection _connection;
        private TestDbContext _db;

        [SetUp]
        public void SetUp() {

            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            _db = new TestDbContext(_connection);
            _db.Database.EnsureCreated();

            TestSeedData.Seed(_db);
        }

        [TearDown]
        public void TearDown() {
            _db.Dispose();
            _connection.Dispose();
        }

        [TestCaseSource(nameof(GetSemanticTestCases))]
        public void Generated_Linq_Should_Return_Same_Rows_As_Sql(string sqlInput, string semanticSkip) {

            if (semanticSkip != null) {
                Assert.Ignore($"Skipped ({semanticSkip}). Verified by TranspilerTests.");
            }

            var sqlRows = RunRawSql(sqlInput);

            string generatedLinq = SqlToLinqConverter.Convert(sqlInput);

            var linqRows = RunGeneratedLinq(generatedLinq);

            bool orderSensitive = sqlInput.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase) >= 0;

            AssertRowsEqual(sqlRows, linqRows, orderSensitive,
                $"\nSQL: {sqlInput}\nGenerált LINQ: {generatedLinq}");
        }

        private List<Dictionary<string, object>> RunRawSql(string sql) {

            var rows = new List<Dictionary<string, object>>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = cmd.ExecuteReader();

            while (reader.Read()) {

                var row = new Dictionary<string, object>();

                for (int i = 0; i < reader.FieldCount; i++) {
                    row[NormalizeKey(reader.GetName(i))] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
            return rows;
        }

        private List<Dictionary<string, object>> RunGeneratedLinq(string linqCode) {

            string source = $@" 
                using System;
                using System.Linq;
                using System.Collections.Generic;
                using System.Text.RegularExpressions;
                using Microsoft.EntityFrameworkCore;
                using SqlToLinq.Tests;

                public static class LinqRunner {{
                    public static object Run(TestDbContext db) {{
                        return {linqCode};
                    }}
            }}";

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                assemblyName: "LinqRunnerAssembly",
                syntaxTrees: new[] {
                    Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)
                },
                references: references,
                options: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

            using var ms = new System.IO.MemoryStream();
            var emitResult = compilation.Emit(ms);

            if (!emitResult.Success) {
                var errors = string.Join("\n", emitResult.Diagnostics
                    .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
                throw new InvalidOperationException(
                    $"[ERROR] Roslyn compilation failed.\nLINQ: {linqCode}\nErrors:\n{errors}");
            }

            ms.Seek(0, System.IO.SeekOrigin.Begin);
            var assembly = System.Reflection.Assembly.Load(ms.ToArray());
            var type = assembly.GetType("LinqRunner");
            var method = type.GetMethod("Run");

            object result = method.Invoke(null, new object[] { _db });
            return ToRowList(result);
        }

        private List<Dictionary<string, object>> ToRowList(object result) {

            if (result == null) return new List<Dictionary<string, object>>();

            if (result is ValueType || result is string) {
                return new List<Dictionary<string, object>> {
                    new Dictionary<string, object> { { "scalar_result", result } }
                };
            }

            if (result is not IEnumerable enumerable) {
                throw new InvalidOperationException(
                    $"[ERROR] The generated LINQ did not return an IEnumerable result: {result.GetType()}");
            }

            var rows = new List<Dictionary<string, object>>();

            foreach (var item in enumerable) {

                var row = new Dictionary<string, object>();

                foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                    row[NormalizeKey(prop.Name)] = prop.GetValue(item);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static string NormalizeKey(string name) => name.Replace("_", "").ToLowerInvariant();


        private void AssertRowsEqual(

            List<Dictionary<string, object>> expected,
            List<Dictionary<string, object>> actual,

            bool orderSensitive,
            string context) {

            Assert.That(actual.Count, Is.EqualTo(expected.Count),
                $"[ERROR] Different row count. {context}");

            if (expected.Count == 1 && actual.Count == 1 && expected[0].Count == 1 && actual[0].Count == 1) {

                var expectedVal = expected[0].Values.First();
                var actualVal = actual[0].Values.First();

                string expStr = expectedVal?.ToString() ?? "NULL";
                string actStr = actualVal?.ToString() ?? "NULL";

                Assert.That(actStr, Is.EqualTo(expStr), $"[ERROR] Scalar values differ. {context}");
                return;
            }

            var expectedSerialized = expected.Select(SerializeRow).ToList();
            var actualSerialized = actual.Select(SerializeRow).ToList();

            if (!orderSensitive) {

                expectedSerialized.Sort(StringComparer.Ordinal);
                actualSerialized.Sort(StringComparer.Ordinal);
            }

            Assert.That(actualSerialized, Is.EqualTo(expectedSerialized),
                $"[ERROR] The results are differ. {context}");
        }

        private static string SerializeRow(Dictionary<string, object> row) {
            var parts = row.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                           .Select(kv => $"{kv.Key}={NormalizeValue(kv.Value)}");

            return string.Join("|", parts);
        }

        private static string NormalizeValue(object value) {

            if (value == null) return "NULL";
            if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value is decimal m) return m.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return value.ToString();
        }

        [Test]
        public void Randomly_Generated_Fuzz_Sql_Should_Return_Same_Rows_As_Linq() {

            var generator = new RandomSqlGenerator(seed: 83056);
            int testCount = 2000;

            string logFilePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "SemanticFuzzOutput.log");
            if (File.Exists(logFilePath)) {
                File.Delete(logFilePath);
            }
            TestContext.Progress.WriteLine($"[INFO] Place of the fuzz log: {logFilePath}");

            for (int i = 0; i < testCount; i++) {

                string sqlInput = generator.NextSelect();
                string generatedLinq = "";

                try {
                    generatedLinq = SqlToLinqConverter.Convert(sqlInput);

                    bool hasBoolLiteral =
                        sqlInput.IndexOf(" TRUE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sqlInput.IndexOf(" FALSE", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (hasBoolLiteral) {
                        File.AppendAllText(logFilePath, $"[{i + 1:D3}/{testCount}] [SKIP] SQL: {sqlInput}\nReason: TRUE/FALSE bool vs int SQLite difference\n--------------------------------------------------\n");
                        continue;
                    }

                    var sqlRows = RunRawSql(sqlInput);
                    var linqRows = RunGeneratedLinq(generatedLinq);

                    bool orderSensitive = sqlInput.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase) >= 0;

                    AssertRowsEqual(sqlRows, linqRows, orderSensitive,
                        $"\nSQL: {sqlInput}\nGenerated LINQ: {generatedLinq}");

                    File.AppendAllText(logFilePath, $"[{i + 1:D3}/{testCount}] [PASS] SQL: {sqlInput}\n           LINQ: {generatedLinq}\n--------------------------------------------------\n");

                } catch (NotSupportedException ex) {

                    File.AppendAllText(logFilePath, $"[{i + 1:D3}/{testCount}] [SKIP] SQL: {sqlInput}\nReason: {ex.Message}\n--------------------------------------------------\n");

                } catch (Exception ex) {

                    File.AppendAllText(logFilePath, $"[{i + 1:D3}/{testCount}] [FAIL] SQL: {sqlInput}\n           LINQ: {generatedLinq}\nException: {ex.Message}\n--------------------------------------------------\n");

                    Assert.Fail($"[ERROR] Fuzz failed in {i + 1}. iteration!\nSQL: {sqlInput}\nLINQ: {generatedLinq}\n{ex.Message}");
                    return;
                }
            }
        }

    }
}
