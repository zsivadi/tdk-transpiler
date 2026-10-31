using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using SqlToLinq.Core;

namespace SqlToLinq.Tests {

    [TestFixture]
    public class FuzzTests {

        [Test]
        public void Randomly_Generated_Sql_Should_Not_Throw_And_Should_Produce_Valid_Csharp() {

            var generator = new RandomSqlGenerator(seed: 12345);
            int testCount = 2500;

            string logFilePath = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "SelectFuzzOutput.log");
            if (System.IO.File.Exists(logFilePath)) System.IO.File.Delete(logFilePath);
            TestContext.Progress.WriteLine($"[INFO] Select fuzz log: {logFilePath}");

            for (int i = 0; i < testCount; i++) {

                string sql = generator.NextSelect();
                string linq;

                try {
                    linq = SqlToLinqConverter.Convert(sql);
                } catch (NotSupportedException ex) {
                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [SKIP] SQL: {sql}\nReason: {ex.Message}\n{new string('-', 50)}\n");
                    continue;
                } catch (System.Exception ex) {
                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [FAIL] SQL: {sql}\nException: {ex.Message}\n{new string('-', 50)}\n");
                    Assert.Fail($"[ERROR] Transpiler threw an exception. SQL: {sql}\n{ex}");
                    return;
                }

                var expression = SyntaxFactory.ParseExpression(linq);
                var errors = expression.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Count > 0) {
                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [FAIL] SQL: {sql}\n           LINQ: {linq}\nErrors: {string.Join(", ", errors)}\n{new string('-', 50)}\n");
                    Assert.That(errors, Is.Empty,
                        $"[ERROR] The generated code is not valid C#.\nSQL: {sql}\nLINQ: {linq}\nErrors: {string.Join(", ", errors)}");
                } else {
                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [PASS] SQL: {sql}\n           LINQ: {linq}\n{new string('-', 50)}\n");
                }
            }
        }
        [Test]
        public void Randomly_Generated_Dml_Should_Not_Throw_And_Should_Produce_Valid_Csharp() {

            var generator = new RandomSqlGenerator(seed: 54321);
            int testCount = 3000;

            string logFilePath = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "DmlFuzzOutput.log");
            if (System.IO.File.Exists(logFilePath)) System.IO.File.Delete(logFilePath);
            TestContext.Progress.WriteLine($"[INFO] DML fuzz log: {logFilePath}");

            for (int i = 0; i < testCount; i++) {

                string sql = i switch {
                    < 1000 => generator.NextDelete(),
                    < 2000 => generator.NextInsert(),
                    _ => generator.NextUpdate()
                };

                string linq;

                try {

                    linq = SqlToLinqConverter.Convert(sql);
                } catch (NotSupportedException ex) {

                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [SKIP] SQL: {sql}\nReason: {ex.Message}\n{new string('-', 50)}\n");
                    continue;
                } catch (System.Exception ex) {

                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [FAIL] SQL: {sql}\nException: {ex.Message}\n{new string('-', 50)}\n");
                    Assert.Fail($"[ERROR] Transpiler threw an exception. SQL: {sql}\n{ex}");
                    return;
                }

                string wrappedSource = linq.Contains("SaveChanges")
                    ? $"class C {{ void M(TestDbContext db) {{ {linq} }} }}"
                    : $"class C {{ async System.Threading.Tasks.Task M(TestDbContext db) {{ await {linq}; }} }}";

                var tree = CSharpSyntaxTree.ParseText(wrappedSource);
                var errors = tree.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Count > 0) {

                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [FAIL] SQL: {sql}\n           LINQ: {linq}\nErrors: {string.Join(", ", errors)}\n{new string('-', 50)}\n");
                    Assert.That(errors, Is.Empty,
                        $"[ERROR] The generated DML code is not valid C#.\nSQL: {sql}\nLINQ: {linq}\nErrors: {string.Join(", ", errors)}");
                } else {
                    System.IO.File.AppendAllText(logFilePath, $"[{i + 1:D4}/{testCount}] [PASS] SQL: {sql}\n           LINQ: {linq}\n{new string('-', 50)}\n");
                }
            }
        }
    }
}