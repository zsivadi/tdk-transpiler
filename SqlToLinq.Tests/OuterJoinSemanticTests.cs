using System;
using System.Linq;
using SqlToLinq.Core;
using NUnit.Framework;
using System.Reflection;
using System.Collections;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SqlToLinq.Tests {

    // The seed data is designed so that unmatched rows exist on BOTH sides of every
    // join relationship. This lets the tests verify that LEFT JOIN preserves left-only
    // rows with nulls on the right, and RIGHT JOIN preserves right-only rows with
    // nulls on the left — something that cannot be tested with a "complete" dataset
    // where every foreign key has a matching primary key.
    //
    // Customers:  1 Alice, 2 Bob, 3 Carol (no orders), 4 Dave (no orders)
    // Invoices:   1→1, 2→1, 3→2, 4→99 (owner 99 does not exist → RIGHT JOIN test row)

    public record Customer(int Id, string Name, string City);

    public record Invoice(int Id, int? Owner, string Product, int? Amount);

    public class OuterJoinDbContext : DbContext {

        public DbSet<Customer> Customers { get; set; }
        public DbSet<Invoice> Invoices { get; set; }

        private readonly SqliteConnection _connection;

        public OuterJoinDbContext(SqliteConnection connection) {
            _connection = connection;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
            optionsBuilder.UseSqlite(_connection);
        }
    }

    [TestFixture]
    public class OuterJoinSemanticTests {

        private SqliteConnection _connection;
        private OuterJoinDbContext _db;

        [SetUp]
        public void SetUp() {

            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            _db = new OuterJoinDbContext(_connection);
            _db.Database.EnsureCreated();

            _db.Customers.AddRange(
                new Customer(1, "Alice", "Budapest"),
                new Customer(2, "Bob", "Debrecen"),
                new Customer(3, "Carol", "Pécs"),    
                new Customer(4, "Dave", "Győr")     
            );

            _db.Invoices.AddRange(
                new Invoice(1, 1, "Laptop", 1200),  
                new Invoice(2, 1, "Mouse", 25),  
                new Invoice(3, 2, "Keyboard", 75),  
                new Invoice(4, 99, "Monitor", 350)   
            );

            _db.SaveChanges();
        }

        [TearDown]
        public void TearDown() {
            _db.Dispose();
            _connection.Dispose();
        }

        [Test]
        public void Left_Join_Preserves_Unmatched_Left_Rows() {
            string sql = "SELECT c.name, i.product FROM Customers c LEFT JOIN Invoices i ON c.id = i.owner";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Left_Join_Where_On_Left_Table() {
            string sql = "SELECT c.name, i.product FROM Customers c LEFT JOIN Invoices i ON c.id = i.owner WHERE c.city = 'Budapest'";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Left_Join_Where_On_Right_Table_Filters_Nulls() {
            string sql = "SELECT c.name, i.product FROM Customers c LEFT JOIN Invoices i ON c.id = i.owner WHERE i.product = 'Laptop'";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Left_Join_Order_By_Left_Column() {
            string sql = "SELECT c.name, i.product FROM Customers c LEFT JOIN Invoices i ON c.id = i.owner ORDER BY c.name ASC";
            AssertSemanticallyEqual(sql, orderSensitive: true);
        }

        [Test]
        public void Left_Join_Limit() {
            string sql = "SELECT c.name, i.product FROM Customers c LEFT JOIN Invoices i ON c.id = i.owner ORDER BY c.name ASC LIMIT 3";
            AssertSemanticallyEqual(sql, orderSensitive: true);
        }

        [Test]
        public void Left_Outer_Join_Explicit_Outer_Keyword() {
            string sql = "SELECT c.name, i.product FROM Customers c LEFT OUTER JOIN Invoices i ON c.id = i.owner";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Left_Join_Reversed_On_Condition() {
            string sql = "SELECT c.name, i.product FROM Customers c LEFT JOIN Invoices i ON i.owner = c.id";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Right_Join_Preserves_Unmatched_Right_Rows() {
            string sql = "SELECT c.name, i.product FROM Customers c RIGHT JOIN Invoices i ON c.id = i.owner";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Right_Join_Where_On_Right_Table() {
            string sql = "SELECT c.name, i.product FROM Customers c RIGHT JOIN Invoices i ON c.id = i.owner WHERE i.product = 'Monitor'";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Right_Join_Reversed_On_Condition() {
            string sql = "SELECT c.name, i.product FROM Customers c RIGHT JOIN Invoices i ON i.owner = c.id";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Right_Join_Order_By_Right_Column() {
            string sql = "SELECT c.name, i.product FROM Customers c RIGHT JOIN Invoices i ON c.id = i.owner ORDER BY i.product ASC";
            AssertSemanticallyEqual(sql, orderSensitive: true);
        }

        [Test]
        public void Right_Outer_Join_Explicit_Outer_Keyword() {
            string sql = "SELECT c.name, i.product FROM Customers c RIGHT OUTER JOIN Invoices i ON c.id = i.owner";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Inner_Join_Drops_Both_Unmatched_Sides() {
            string sql = "SELECT c.name, i.product FROM Customers c JOIN Invoices i ON c.id = i.owner";
            AssertSemanticallyEqual(sql);
        }

        private void AssertSemanticallyEqual(string sql, bool orderSensitive = false) {

            var sqlRows = RunRawSql(sql);

            string generatedLinq = SqlToLinqConverter.Convert(sql);
            var linqRows = RunGeneratedLinq(generatedLinq);

            Assert.That(linqRows.Count, Is.EqualTo(sqlRows.Count),
                $"[ERROR] Row count differs.\nSQL: {sql}\nLINQ: {generatedLinq}\nExpected: {sqlRows.Count}, Got: {linqRows.Count}");

            var expectedSerialized = sqlRows.Select(SerializeRow).ToList();
            var actualSerialized = linqRows.Select(SerializeRow).ToList();

            if (!orderSensitive) {
                expectedSerialized.Sort(StringComparer.Ordinal);
                actualSerialized.Sort(StringComparer.Ordinal);
            }

            Assert.That(actualSerialized, Is.EqualTo(expectedSerialized),
                $"[ERROR] Results differ.\nSQL: {sql}\nLINQ: {generatedLinq}");
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
                using Microsoft.EntityFrameworkCore;
                using SqlToLinq.Tests;

                public static class LinqRunner {{
                    public static object Run(OuterJoinDbContext db) {{
                        return {linqCode};
                    }}
            }}";

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName: "OuterJoinLinqRunner",
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new System.IO.MemoryStream();
            var result = compilation.Emit(ms);

            if (!result.Success) {

                var errors = string.Join("\n", result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));

                throw new InvalidOperationException(
                    $"[ERROR] Roslyn compilation failed.\nLINQ: {linqCode}\nErrors:\n{errors}");
            }

            ms.Seek(0, System.IO.SeekOrigin.Begin);
            var assembly = Assembly.Load(ms.ToArray());
            var type = assembly.GetType("LinqRunner");
            var method = type.GetMethod("Run");

            object res = method.Invoke(null, new object[] { _db });
            return ToRowList(res);
        }

        private List<Dictionary<string, object>> ToRowList(object result) {

            if (result == null) return new List<Dictionary<string, object>>();

            if (result is not IEnumerable enumerable) {
                throw new InvalidOperationException(
                    $"[ERROR] Generated LINQ did not return IEnumerable: {result.GetType()}");
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

        private static string NormalizeValue(object value) {

            if (value == null) return "NULL";
            if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value is decimal m) return m.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return value?.ToString() ?? "NULL";
        }

        private static string SerializeRow(Dictionary<string, object> row) {
            return string.Join("|", row.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                       .Select(kv => $"{kv.Key}={NormalizeValue(kv.Value)}"));
        }
    }
}