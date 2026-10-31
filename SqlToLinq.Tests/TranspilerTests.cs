using System;
using System.IO;
using SqlToLinq.Core;
using Antlr4.Runtime;
using NUnit.Framework;
using System.Text.Json;
using System.Collections.Generic;

namespace SqlToLinq.Tests {
    [TestFixture]
    public class TranspilerTests {

        public static List<TestCase> LoadTestCases() {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IOPairs.json");

            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"[ERROR] Test file cannot found: {jsonPath}");

            string jsonString = File.ReadAllText(jsonPath);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<TestCase>>(jsonString, options);
        }

        public static string TestName(TestCase tc) => $"Test_{tc.Id}_{tc.Desc.Replace(" ", "_")}";

        public static IEnumerable<TestCaseData> GetSelectTestCases() {

            foreach (var tc in LoadTestCases()) {

                yield return new TestCaseData(tc.SqlInput, tc.ExpectedLinq)
                    .SetName(TestName(tc));
            }
        }

        [TestCaseSource(nameof(GetSelectTestCases))]
        public void Sql_To_Linq_Conversion_Should_Match_Expected(string sqlInput, string expectedLinq) {

            string generatedLinq = SqlToLinqConverter.Convert(sqlInput);

            string cleanGenerated = generatedLinq.Replace(" ", "").Trim();
            string cleanExpected = expectedLinq.Replace(" ", "").Trim();

            Assert.That(cleanGenerated, Is.EqualTo(cleanExpected),
                $"\n[ERROR] Error while transpiling: {sqlInput}\nGenrated: {generatedLinq}\nExpected: {expectedLinq}\n");
        }
    }

    public class TestCase {
        public int Id { get; set; }
        public string Desc { get; set; }
        public string SqlInput { get; set; }
        public string ExpectedLinq { get; set; }

        // Reason why the case is not executed by SemanticEquivalenceTests
        // (null if it is executed): sqlite-date, sqlite-function, sqlite-bool,
        // nondeterministic or dml
        public string SemanticSkip { get; set; }
    }

    [TestFixture]
    public class ErrorPathTests {

        [Test]
        public void Lexer_Error_Should_Throw_SqlSyntaxException() {

            Assert.Throws<SqlSyntaxException>(() =>
                SqlToLinqConverter.Convert("SELECT @ FROM Users"));
        }

        [Test]
        public void Parser_Error_Should_Throw_SqlSyntaxException() {

            Assert.Throws<SqlSyntaxException>(() =>
                SqlToLinqConverter.Convert("SELECT FROM FROM"));
        }

        [Test]
        public void Ddl_Statement_Should_Throw_SqlSyntaxException() {

            Assert.Throws<SqlSyntaxException>(() =>
                SqlToLinqConverter.Convert("CREATE TABLE Foo (Id INT)"));
        }

        [Test]
        public void String_GreaterThan_Should_Throw_NotSupportedException() {

            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT * FROM Users WHERE Name > 'Alice'"));
        }

        [Test]
        public void String_LessThan_Should_Throw_NotSupportedException() {

            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT * FROM Users WHERE Name < 'Alice'"));
        }

        [Test]
        public void String_GreaterOrEqual_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT * FROM Users WHERE Name >= 'Alice'"));
        }

        [Test]
        public void String_LessOrEqual_Should_Throw_NotSupportedException() {

            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT * FROM Users WHERE 'Alice' <= Name"));
        }

        [Test]
        public void String_Between_Should_Throw_NotSupportedException() {

            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT * FROM Users WHERE Name BETWEEN 'Alice' AND 'Bob'"));
        }

        [Test]
        public void Distinct_OrderBy_Column_Not_In_Select_Should_Throw_NotSupportedException() {

            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT DISTINCT Name FROM Users ORDER BY Age DESC"));
        }

        [Test]
        public void Distinct_With_Unaliased_Case_And_OrderBy_Not_In_Select_Should_Throw_NotSupportedException() {

            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT DISTINCT CASE WHEN Age > 18 THEN 1 ELSE 0 END FROM Users ORDER BY Age DESC"));
        }

        [Test]
        public void Unknown_Function_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT FOOBAR(Name) AS X FROM Users"));
        }

        [Test]
        public void Dateadd_Unsupported_Part_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT DATEADD('week', 1, CreatedAt) AS W FROM Users"));
        }

        [Test]
        public void Datediff_Unsupported_Part_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT DATEDIFF('week', CreatedAt, GETDATE()) AS W FROM Users"));
        }

        [Test]
        public void Datepart_Unsupported_Part_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT DATEPART('week', CreatedAt) AS W FROM Users"));
        }

        [Test]
        public void Left_Function_Should_Throw_SqlSyntaxException() {
            Assert.Throws<SqlSyntaxException>(() =>
                SqlToLinqConverter.Convert("SELECT LEFT(Name, 2) AS L FROM Users"));
        }

        [Test]
        public void Right_Function_Should_Throw_SqlSyntaxException() {
            Assert.Throws<SqlSyntaxException>(() =>
                SqlToLinqConverter.Convert("SELECT RIGHT(Name, 2) AS R FROM Users"));
        }

        [Test]
        public void Count_Distinct_Outside_Group_By_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT COUNT(DISTINCT Name) AS C FROM Users"));
        }

        [Test]
        public void Unknown_Zero_Arg_Function_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT UNKNOWN_FUNC() AS X FROM Users"));
        }

        [Test]
        public void Substring_With_Wrong_Arg_Count_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT SUBSTRING(Name, 1) AS S FROM Users"));
        }

        [Test]
        public void Replace_With_Wrong_Arg_Count_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT REPLACE(Name, 'a') AS R FROM Users"));
        }

        [Test]
        public void Stuff_With_Wrong_Arg_Count_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT STUFF(Name, 1, 2) AS S FROM Users"));
        }

        [Test]
        public void Left_Join_Non_Equality_On_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert(
                    "SELECT u.Name FROM Users u LEFT JOIN Orders o ON u.Id > o.Owner"));
        }

        [Test]
        public void Right_Join_Non_Equality_On_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert(
                    "SELECT u.Name FROM Users u RIGHT JOIN Orders o ON u.Id > o.Owner"));
        }

        [Test]
        public void Join_On_Unknown_Alias_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert(
                    "SELECT u.Name FROM Users u JOIN Orders o ON x.Id = o.Owner"));
        }

        [Test]
        public void Unknown_Table_Alias_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert("SELECT x.Name FROM Users"));
        }

        [Test]
        public void Right_Join_On_Wrong_Aliases_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert(
                    "SELECT u.Name FROM Users u RIGHT JOIN Orders o ON u.Id = u.Id"));
        }

        [Test]
        public void Sum_Distinct_Should_Throw_NotSupportedException() {
            Assert.Throws<NotSupportedException>(() =>
                SqlToLinqConverter.Convert(
                    "SELECT SUM(DISTINCT Age) AS S FROM Users GROUP BY Role"));
        }

        [Test]
        public void LinqStringFunctionNode_Unknown_Function_Throws_NotSupportedException() {

            var node = new LinqStringFunctionNode {
                FunctionName = "UNKNOWNFUNC",
                Arguments = new List<LinqNode> { new LinqConstantNode { Value = "x" } }
            };

            Assert.Throws<NotSupportedException>(() => new CSharpCodeGenerator().Generate(node));
        }

        [Test]
        public void Delete_Without_Where_Generates_ExecuteDelete() {

            string linq = SqlToLinqConverter.Convert("DELETE FROM Users");

            Assert.That(linq, Is.EqualTo("db.Users.ExecuteDeleteAsync()"));
        }

        [Test]
        public void Delete_With_Where_Generates_Where_ExecuteDelete() {

            string linq = SqlToLinqConverter.Convert("DELETE FROM Users WHERE Age < 18");

            Assert.That(linq, Is.EqualTo("db.Users.Where(x => x.Age < 18).ExecuteDeleteAsync()"));
        }
    }

    [TestFixture]
    public class PascalCaseTests {

        [Test]
        public void Snake_Case_Table_Name_Converted_To_PascalCase() {
            string linq = SqlToLinqConverter.Convert("SELECT * FROM user_orders");
            Assert.That(linq, Does.Contain("db.UserOrders"));
        }

        [Test]
        public void Mixed_Case_Column_Name_Preserved_With_Upper_First() {
            string linq = SqlToLinqConverter.Convert("SELECT userName FROM Users");
            Assert.That(linq, Does.Contain("x.UserName"));
        }

        [Test]
        public void All_Caps_Name_Converted_To_Pascal_Case() {
            string linq = SqlToLinqConverter.Convert("SELECT * FROM USERS");
            Assert.That(linq, Does.Contain("db.Users"));
        }

        [Test]
        public void ToPascalCase_AllUnderscores_ReturnsOriginalInput() {
            string linq = SqlToLinqConverter.Convert("SELECT a_b FROM Users");
            Assert.That(linq, Does.Contain("AB"));
        }

        [Test]
        public void All_Underscores_Table_Name_Returns_Original_Input() {
            string linq = SqlToLinqConverter.Convert("SELECT * FROM __");
            Assert.That(linq, Does.Contain("db.__"));
        }
    }
}
