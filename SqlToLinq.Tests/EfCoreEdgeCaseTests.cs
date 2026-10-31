using System;
using System.Linq;
using SqlToLinq.Core;
using NUnit.Framework;
using System.Reflection;
using System.Collections;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;

namespace SqlToLinq.Tests {

    // These tests cover constructs where:
    //
    //   (1) the Roslyn compiler accepts the generated code, but
    //   (2) EF Core may fail to translate it to SQL server-side, OR
    //   (3) subtle type/null semantics differ between in-memory and real providers.
    //
    // Every test here runs via Roslyn + EF Core + SQLite so that EF Core actually
    // translates to SQL — if it cannot, it throws InvalidOperationException, which
    // is exactly what would happen in a real ASP.NET application.

    [TestFixture]
    public class EfCoreEdgeCaseTests {

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

        // LIKE - EF.Functions.Like

        [Test]
        public void Like_Pattern_Translates_Server_Side_Via_Ef_Functions() {
            string sql = "SELECT Name FROM Users WHERE Name LIKE 'B%'";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Not_Like_Pattern_Translates_Server_Side_Via_Ef_Functions() {
            string sql = "SELECT Name FROM Users WHERE Name NOT LIKE 'A%'";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Like_Underscore_Wildcard_Translates_Server_Side() {
            string sql = "SELECT Name FROM Users WHERE Name LIKE 'B_b'";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Like_Case_Sensitivity_Matches_Sql_Behaviour() {
            string sql = "SELECT Name FROM Users WHERE Name LIKE '%ob'";
            AssertSemanticallyEqual(sql);
        }

        // Math functions — explicit casts

        [Test]
        public void Abs_With_Explicit_Cast_Translates_On_Sqlite() {
            string sql = "SELECT ABS(Points) AS AbsPoints FROM Users";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Round_With_Explicit_Cast_Translates_On_Sqlite() {
            string sql = "SELECT ROUND(Age, 0) AS RoundedAge FROM Users";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Floor_With_Explicit_Cast_Translates_On_Sqlite() {
            string sql = "SELECT FLOOR(Age) AS FloorAge FROM Users";
            AssertSemanticallyEqual(sql);
        }

        // Nullable int IN list — type inference

        [Test]
        public void Nullable_Int_In_List_Translates_Correctly() {
            string sql = "SELECT Name FROM Users WHERE Id IN (1, 2, 6)";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Nullable_Int_Not_In_List_Translates_Correctly() {
            string sql = "SELECT Name FROM Users WHERE Id NOT IN (1, 2, 6)";
            AssertSemanticallyEqual(sql);
        }

        // NULL semantics in CASE WHEN

        [Test]
        public void Case_When_Without_Else_Returns_Null_Server_Side() {
            string sql = "SELECT Name, CASE WHEN Age > 30 THEN 'Senior' END AS Label FROM Users";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Case_When_In_Where_Clause_Translates_Server_Side() {
            string sql = "SELECT Name FROM Users WHERE CASE WHEN Age > 18 THEN 1 ELSE 0 END = 1";
            AssertSemanticallyEqual(sql);
        }

        // Scalar subquery vs aggregate return type

        [Test]
        public void Scalar_Subquery_Max_Does_Not_Double_Wrap() {
            string sql = "SELECT Name FROM Users WHERE Age = (SELECT MAX(Age) FROM Users)";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Scalar_Subquery_Count_In_Select_Does_Not_Double_Wrap() {
            string sql = "SELECT Name, (SELECT COUNT(*) FROM Orders o WHERE o.Owner = u.Id) AS OrderCount FROM Users u";
            AssertSemanticallyEqual(sql);
        }

        // LEFT JOIN null propagation

        [Test]
        public void Left_Join_Unmatched_Rows_Have_Null_Right_Side_Server_Side() {
            string sql = "SELECT u.Name, o.Item FROM Users u LEFT JOIN Orders o ON u.Id = o.Owner";
            AssertSemanticallyEqual(sql);
        }

        // Correlated subquery scope

        [Test]
        public void Correlated_Exists_Outer_Param_Resolved_Server_Side() {
            string sql = "SELECT Name FROM Users u WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.Owner = u.Id)";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Correlated_Not_Exists_Outer_Param_Resolved_Server_Side() {
            string sql = "SELECT Name FROM Users u WHERE NOT EXISTS (SELECT 1 FROM Orders o WHERE o.Owner = u.Id)";
            AssertSemanticallyEqual(sql);
        }

        [Test]
        public void Correlated_In_Subquery_Outer_Param_Resolved_Server_Side() {
            string sql = "SELECT Name FROM Users u WHERE u.Id IN (SELECT Owner FROM Orders o WHERE o.Qty > 1)";
            AssertSemanticallyEqual(sql);
        }

        // Date functions — static expected values based on seed data

        [Test]
        public void Year_Extracts_Correct_Year_From_Nullable_DateTime() {

            string sql = "SELECT Name, YEAR(CreatedAt) AS Yr FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["yr"]?.ToString(), Is.EqualTo("2022"), $"LINQ: {linq}");
        }

        [Test]
        public void Month_Extracts_Correct_Month_From_Nullable_DateTime() {

            string sql = "SELECT Name, MONTH(CreatedAt) AS Mo FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["mo"]?.ToString(), Is.EqualTo("1"), $"LINQ: {linq}");
        }

        [Test]
        public void Day_Extracts_Correct_Day_From_Nullable_DateTime() {

            string sql = "SELECT Name, DAY(CreatedAt) AS D FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["d"]?.ToString(), Is.EqualTo("15"), $"LINQ: {linq}");
        }

        [Test]
        public void Year_In_Where_Filters_By_Year() {

            string sql = "SELECT Name FROM Users WHERE YEAR(CreatedAt) = 2022";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            var names = rows.Select(r => r["name"]?.ToString()).OrderBy(n => n).ToList();
            Assert.That(names, Is.EqualTo(new[] { "Bab", "Bob" }), $"LINQ: {linq}");
        }

        [Test]
        public void Dateadd_Day_Adds_Correct_Days() {

            string sql = "SELECT Name, DATEADD('day', 7, CreatedAt) AS NextWeek FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");

            var nextWeek = (DateTime?)rows[0]["nextweek"];

            Assert.That(nextWeek?.Day, Is.EqualTo(22), $"LINQ: {linq}");
            Assert.That(nextWeek?.Month, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(nextWeek?.Year, Is.EqualTo(2022), $"LINQ: {linq}");
        }

        [Test]
        public void Dateadd_Month_Adds_Correct_Months() {

            string sql = "SELECT Name, DATEADD('month', 1, CreatedAt) AS NextMonth FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");

            var nextMonth = (DateTime?)rows[0]["nextmonth"];

            Assert.That(nextMonth?.Month, Is.EqualTo(2), $"LINQ: {linq}");
            Assert.That(nextMonth?.Year, Is.EqualTo(2022), $"LINQ: {linq}");
        }

        [Test]
        public void Getdate_Returns_DateTime_Greater_Than_All_Seed_Dates() {

            string sql = "SELECT Name FROM Users WHERE CreatedAt < GETDATE()";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(6), $"All 6 users should pass. LINQ: {linq}");
        }

        [Test]
        public void Datepart_Year_Extracts_Correct_Year() {

            string sql = "SELECT Name, DATEPART('year', CreatedAt) AS Yr FROM Users WHERE Name = 'Alice'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["yr"]?.ToString(), Is.EqualTo("2024"), $"LINQ: {linq}");
        }

        [Test]
        public void Datepart_Month_Extracts_Correct_Month() {

            string sql = "SELECT Name, DATEPART('month', CreatedAt) AS Mo FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["mo"]?.ToString(), Is.EqualTo("1"), $"LINQ: {linq}");
        }

        [Test]
        public void Datepart_Day_Extracts_Correct_Day() {

            string sql = "SELECT Name, DATEPART('day', CreatedAt) AS D FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["d"]?.ToString(), Is.EqualTo("15"), $"LINQ: {linq}");
        }

        [Test]
        public void Dateadd_Year_Adds_Correct_Years() {

            string sql = "SELECT Name, DATEADD('year', 1, CreatedAt) AS NextYear FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");

            var nextYear = (DateTime?)rows[0]["nextyear"];

            Assert.That(nextYear?.Year, Is.EqualTo(2023), $"LINQ: {linq}");
            Assert.That(nextYear?.Month, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(nextYear?.Day, Is.EqualTo(15), $"LINQ: {linq}");
        }

        [Test]
        public void Datediff_Year_Between_Dates() {

            string sql = "SELECT Name, DATEDIFF('year', CreatedAt, GETDATE()) AS Yrs FROM Users WHERE Name = 'bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");

            var yrs = Convert.ToInt32(rows[0]["yrs"]);

            Assert.That(yrs, Is.GreaterThanOrEqualTo(3), $"LINQ: {linq}");
        }

        [Test]
        public void Datediff_Day_Between_Dates() {

            string sql = "SELECT Name, DATEDIFF('day', CreatedAt, GETDATE()) AS Days FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");

            var days = Convert.ToInt32(rows[0]["days"]);

            Assert.That(days, Is.GreaterThanOrEqualTo(700), $"LINQ: {linq}");
        }

        [Test]
        public void Hour_Extracts_Hour_From_Utc_DateTime() {

            string sql = "SELECT Name, HOUR(CreatedAt) AS Hr FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["hr"]?.ToString(), Is.EqualTo("0"), $"LINQ: {linq}");
        }

        [Test]
        public void Minute_Extracts_Minute_From_Utc_DateTime() {

            string sql = "SELECT Name, MINUTE(CreatedAt) AS Mi FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["mi"]?.ToString(), Is.EqualTo("0"), $"LINQ: {linq}");
        }

        [Test]
        public void Second_Extracts_Second_From_Utc_DateTime() {

            string sql = "SELECT Name, SECOND(CreatedAt) AS Sc FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["sc"]?.ToString(), Is.EqualTo("0"), $"LINQ: {linq}");
        }

        [Test]
        public void Current_Date_Filters_All_Past_Seed_Dates() {

            string sql = "SELECT Name FROM Users WHERE CreatedAt < CURRENT_DATE";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(6), $"All 6 users should pass. LINQ: {linq}");
        }

        [Test]
        public void Current_Timestamp_Filters_All_Past_Seed_Dates() {

            string sql = "SELECT Name FROM Users WHERE CreatedAt < CURRENT_TIMESTAMP";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows.Count, Is.EqualTo(6), $"All 6 users should pass. LINQ: {linq}");
        }

        // String functions — static expected values

        [Test]
        public void Ltrim_Removes_Leading_Spaces() {

            string sql = "SELECT LTRIM(Name) AS T FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["t"]?.ToString(), Is.EqualTo("Bob"), $"LINQ: {linq}");
        }

        [Test]
        public void Rtrim_Removes_Trailing_Spaces() {

            string sql = "SELECT RTRIM(Name) AS T FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["t"]?.ToString(), Is.EqualTo("Bob"), $"LINQ: {linq}");
        }

        [Test]
        public void Concat_Joins_Two_Strings() {

            string sql = "SELECT CONCAT(Name, Role) AS Full FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["full"]?.ToString(), Is.EqualTo("BobAdmin"), $"LINQ: {linq}");
        }

        [Test]
        public void Instr_Returns_Position_Of_Substring() {

            string sql = "SELECT INSTR(Name, 'o') AS Pos FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["pos"]?.ToString(), Is.EqualTo("2"), $"LINQ: {linq}");
        }

        [Test]
        public void Charindex_Returns_Position_Of_Substring() {

            string sql = "SELECT CHARINDEX('o', Name) AS Pos FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["pos"]?.ToString(), Is.EqualTo("2"), $"LINQ: {linq}");
        }

        [Test]
        public void Reverse_Reverses_String() {

            string sql = "SELECT REVERSE(Name) AS Rev FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["rev"]?.ToString(), Is.EqualTo("boB"), $"LINQ: {linq}");
        }

        [Test]
        public void Repeat_Repeats_String() {

            string sql = "SELECT REPEAT(Name, 2) AS Rep FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["rep"]?.ToString(), Is.EqualTo("BobBob"), $"LINQ: {linq}");
        }

        [Test]
        public void Space_Generates_Correct_Number_Of_Spaces() {

            string sql = "SELECT SPACE(3) AS Spaces FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["spaces"]?.ToString(), Is.EqualTo("   "), $"LINQ: {linq}");
        }

        [Test]
        public void Lcase_Lowercases_String() {

            string sql = "SELECT LCASE(Name) AS Lower FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["lower"]?.ToString(), Is.EqualTo("bob"), $"LINQ: {linq}");
        }

        [Test]
        public void Ucase_Uppercases_String() {

            string sql = "SELECT UCASE(Name) AS Upper FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["upper"]?.ToString(), Is.EqualTo("BOB"), $"LINQ: {linq}");
        }

        [Test]
        public void Replace_Replaces_Substring() {

            string sql = "SELECT REPLACE(Name, 'Bob', 'Robert') AS NewName FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["newname"]?.ToString(), Is.EqualTo("Robert"), $"LINQ: {linq}");
        }

        [Test]
        public void Mod_Returns_Correct_Remainder() {

            string sql = "SELECT Name, MOD(Age, 2) AS AgeRem FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);

            Assert.That(rows[0]["agerem"]?.ToString(), Is.EqualTo("1"), $"LINQ: {linq}");
        }

        [Test]
        public void Len_Returns_String_Length() {
            string sql = "SELECT Name, LEN(Name) AS L FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);
            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["l"]?.ToString(), Is.EqualTo("3"), $"LINQ: {linq}");
        }

        [Test]
        public void Isnull_Returns_Fallback_When_Null() {

            _db.Users.Add(new User { Id = 7, Name = "NullBonus", Age = 33, Role = "User", Points = 0, Bonus = null });
            _db.SaveChanges();

            string sql = "SELECT Name, ISNULL(Bonus, 99) AS B FROM Users WHERE Name = 'NullBonus'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);
            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["b"]?.ToString(), Is.EqualTo("99"), $"LINQ: {linq}");
        }

        [Test]
        public void Nvl_Returns_Fallback_When_Null() {
            string sql = "SELECT Name, NVL(Bonus, 99) AS B FROM Users WHERE Name = 'Bob'";
            string linq = SqlToLinqConverter.Convert(sql);
            var rows = RunGeneratedLinq(linq);
            Assert.That(rows.Count, Is.EqualTo(1), $"LINQ: {linq}");
            Assert.That(rows[0]["b"]?.ToString(), Is.EqualTo("10"), $"LINQ: {linq}");
        }

        // DELETE

        [Test]
        public void Delete_With_Where_Removes_Matching_Rows() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert("DELETE FROM Users WHERE Age < 18"));

            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(_db.Users.Count(), Is.EqualTo(5));
            Assert.That(_db.Users.Any(u => u.Name == "Bcb"), Is.False);
        }

        [Test]
        public void Delete_Without_Where_Removes_All_Rows() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert("DELETE FROM Users"));

            Assert.That(deleted, Is.EqualTo(6));
            Assert.That(_db.Users.Count(), Is.EqualTo(0));
        }

        [Test]
        public void Delete_With_And_Condition_Removes_Correct_Rows() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert("DELETE FROM Users WHERE Age > 20 AND Role = 'User'"));

            Assert.That(deleted, Is.EqualTo(2));
            Assert.That(_db.Users.Any(u => u.Name == "Bab"), Is.False);
            Assert.That(_db.Users.Count(), Is.EqualTo(4));
        }

        [Test]
        public void Delete_With_In_List_Removes_Correct_Rows() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert("DELETE FROM Users WHERE Role IN ('Admin', 'Moderator')"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Users.Any(u => u.Role == "Admin"), Is.False);
            Assert.That(_db.Users.Any(u => u.Role == "Moderator"), Is.False);
            Assert.That(_db.Users.Count(), Is.EqualTo(3));
        }

        [Test]
        public void Delete_With_Is_Null_Removes_No_Rows_When_None_Match() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert("DELETE FROM Orders WHERE Owner IS NULL"));

            Assert.That(deleted, Is.EqualTo(0));
            Assert.That(_db.Orders.Count(), Is.EqualTo(4));
        }

        [Test]
        public void Delete_With_Like_Removes_Matching_Rows() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert("DELETE FROM Users WHERE Name LIKE 'Ali%'"));

            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(_db.Users.Any(u => u.Name == "Alice"), Is.False);
            Assert.That(_db.Users.Count(), Is.EqualTo(5));
        }

        [Test]
        public void Delete_With_In_Subquery_Removes_Correct_Rows() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Orders WHERE Owner IN (SELECT Id FROM Users WHERE Role = 'Admin')"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Orders.Any(o => o.Item == "Laptop"), Is.False);
            Assert.That(_db.Orders.Any(o => o.Item == "Mouse"), Is.False);
            Assert.That(_db.Orders.Any(o => o.Item == "Monitor"), Is.False);
            Assert.That(_db.Orders.Count(), Is.EqualTo(1));
        }

        [Test]
        public void Delete_With_Exists_Subquery_Removes_Users_With_Orders() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Users u WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.Owner = u.Id)"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Users.Any(u => u.Name == "Bob"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "Bab"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "Alice"), Is.False);
            Assert.That(_db.Users.Count(), Is.EqualTo(3));
        }

        [Test]
        public void Delete_With_Not_Exists_Subquery_Removes_Users_Without_Orders() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Users u WHERE NOT EXISTS (SELECT 1 FROM Orders o WHERE o.Owner = u.Id)"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Users.Any(u => u.Name == "Bcb"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "bob"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "B.b"), Is.False);
            Assert.That(_db.Users.Count(), Is.EqualTo(3));
        }

        [Test]
        public void Delete_With_Between_Removes_Correct_Age_Range() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Users WHERE Age BETWEEN 18 AND 25"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Users.Any(u => u.Name == "Bob"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "B.b"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "Alice"), Is.False);

            Assert.That(_db.Users.Any(u => u.Name == "Bcb"), Is.True);
            Assert.That(_db.Users.Any(u => u.Name == "bob"), Is.True);
            Assert.That(_db.Users.Any(u => u.Name == "Bab"), Is.True);
        }

        [Test]
        public void Delete_With_Or_Condition_Removes_Both_Matching_Groups() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Users WHERE Role = 'Admin' OR Role = 'Moderator'"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Users.Any(u => u.Name == "Bob"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "Alice"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "bob"), Is.False);
            Assert.That(_db.Users.Count(), Is.EqualTo(3));
        }

        [Test]
        public void Delete_With_Not_In_List_Removes_Non_Matching_Rows() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Users WHERE Role NOT IN ('Admin', 'Moderator')"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Users.Any(u => u.Role == "User"), Is.False);
            Assert.That(_db.Users.Count(), Is.EqualTo(3));
        }

        [Test]
        public void Delete_With_Correlated_In_Subquery_Uses_Alias() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Orders o WHERE o.Owner IN (SELECT u.Id FROM Users u WHERE u.Role = 'Admin')"));

            Assert.That(deleted, Is.EqualTo(3));
            Assert.That(_db.Orders.Any(o => o.Item == "Laptop"), Is.False);
            Assert.That(_db.Orders.Any(o => o.Item == "Mouse"), Is.False);
            Assert.That(_db.Orders.Any(o => o.Item == "Monitor"), Is.False);
            Assert.That(_db.Orders.Any(o => o.Item == "Keyboard"), Is.True);
        }

        [Test]
        public void Delete_With_Combined_And_Or_Subquery_Condition() {

            int deleted = RunGeneratedDelete(
                SqlToLinqConverter.Convert(
                    "DELETE FROM Users u WHERE Age > 25 AND NOT EXISTS (SELECT 1 FROM Orders o WHERE o.Owner = u.Id)"));

            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(_db.Users.Any(u => u.Name == "bob"), Is.False);
            Assert.That(_db.Users.Any(u => u.Name == "Bab"), Is.True);

            Assert.That(_db.Users.Count(), Is.EqualTo(5));
        }

        private int RunGeneratedDelete(string linqCode) {

            string source = $@"
                using System;
                using System.Linq;
                using Microsoft.EntityFrameworkCore;
                using SqlToLinq.Tests;

                public static class DeleteRunner {{
                    public static int Run(TestDbContext db) {{
                        return {linqCode}.GetAwaiter().GetResult();
                    }}
            }}";

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName: "DeleteRunner",
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
            var type = assembly.GetType("DeleteRunner");
            var method = type.GetMethod("Run");

            return (int)method.Invoke(null, new object[] { _db });
        }

        private void AssertSemanticallyEqual(string sql, bool orderSensitive = false) {

            var sqlRows = RunRawSql(sql);
            string linq = SqlToLinqConverter.Convert(sql);
            var linqRows = RunGeneratedLinq(linq);

            Assert.That(linqRows.Count, Is.EqualTo(sqlRows.Count),
                $"[ERROR] Row count differs.\nSQL: {sql}\nLINQ: {linq}");

            var expected = sqlRows.Select(SerializeRow).ToList();
            var actual = linqRows.Select(SerializeRow).ToList();

            if (!orderSensitive) {
                expected.Sort(StringComparer.Ordinal);
                actual.Sort(StringComparer.Ordinal);
            }

            Assert.That(actual, Is.EqualTo(expected),
                $"[ERROR] Results differ.\nSQL: {sql}\nLINQ: {linq}");
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
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName: "EfCoreEdgeCaseRunner",
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

            return ToRowList(method.Invoke(null, new object[] { _db }));
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


        [Test]
        public void Insert_With_Explicit_Column_List_Adds_Row() {

            int countBefore = _db.Users.Count();

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Users (Name, Age, Role) VALUES ('Charlie', 35, 'Admin')"));

            Assert.That(_db.Users.Count(), Is.EqualTo(countBefore + 1));

            var added = _db.Users.FirstOrDefault(u => u.Name == "Charlie");

            Assert.That(added, Is.Not.Null);
            Assert.That(added.Age, Is.EqualTo(35));
            Assert.That(added.Role, Is.EqualTo("Admin"));
        }

        [Test]
        public void Insert_With_Null_Value_Persists_Null_Column() {

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Users (Name, Age, Bonus) VALUES ('NullBonusUser', 21, NULL)"));

            var added = _db.Users.FirstOrDefault(u => u.Name == "NullBonusUser");
            Assert.That(added, Is.Not.Null, "A sor nem kerülte be az adatbázisba.");
            Assert.That(added.Bonus, Is.Null, "A Bonus oszlopnak NULL-nak kell lennie.");
        }

        [Test]
        public void Insert_MultiRow_AddRange_Adds_All_Rows() {

            int countBefore = _db.Users.Count();

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Users (Name, Age) VALUES ('MultiA', 31), ('MultiB', 32)"));

            Assert.That(_db.Users.Count(), Is.EqualTo(countBefore + 2));
            Assert.That(_db.Users.Any(u => u.Name == "MultiA" && u.Age == 31), Is.True);
            Assert.That(_db.Users.Any(u => u.Name == "MultiB" && u.Age == 32), Is.True);
        }

        [Test]
        public void Insert_With_Arithmetic_Expression_In_Value_Computes_Correctly() {

            int countBefore = _db.Orders.Count();

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Orders (Owner, Qty) VALUES (1, 2 + 3)"));

            Assert.That(_db.Orders.Count(), Is.EqualTo(countBefore + 1));

            var added = _db.Orders
                .OrderByDescending(o => o.Id)
                .FirstOrDefault(o => o.Owner == 1 && o.Qty == 5);

            Assert.That(added, Is.Not.Null, "Owner=1, Qty=5 sor nem jött létre.");
        }

        [Test]
        public void Insert_Into_Orders_Table_Adds_Row_Correctly() {

            int countBefore = _db.Orders.Count();

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Orders (Owner, Item, Qty) VALUES (2, 'Tablet', 1)"));

            Assert.That(_db.Orders.Count(), Is.EqualTo(countBefore + 1));

            var added = _db.Orders.FirstOrDefault(o => o.Item == "Tablet");

            Assert.That(added, Is.Not.Null);
            Assert.That(added.Owner, Is.EqualTo(2));
            Assert.That(added.Qty, Is.EqualTo(1));
        }

        [Test]
        public void Insert_With_String_And_Int_Values_Sets_Both_Correctly() {

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Users (Name, Age, Role) VALUES ('TypeCheck', 28, 'User')"));

            var added = _db.Users.FirstOrDefault(u => u.Name == "TypeCheck");
            Assert.That(added, Is.Not.Null);
            Assert.That(added.Age, Is.EqualTo(28));
            Assert.That(added.Role, Is.EqualTo("User"));
        }

        [Test]
        public void Insert_Transpiler_Output_Matches_Expected_Linq_For_Single_Row() {

            string generated = SqlToLinqConverter.Convert(
                "INSERT INTO Users (Name, Age, Role) VALUES ('Bob', 25, 'Admin')");

            string expected =
                "db.Users.Add(new Users { Name = \"Bob\", Age = 25, Role = \"Admin\" });\n" +
                "db.SaveChanges();";

            Assert.That(
                generated.Replace(" ", "").Replace("\r", ""),
                Is.EqualTo(expected.Replace(" ", "").Replace("\r", "")));
        }

        [Test]
        public void Insert_Transpiler_Output_Matches_Expected_Linq_For_Null_Value() {

            string generated = SqlToLinqConverter.Convert(
                "INSERT INTO Users (Name, Age, Bonus) VALUES ('Alice', 19, NULL)");

            string expected =
                "db.Users.Add(new Users { Name = \"Alice\", Age = 19, Bonus = null });\n" +
                "db.SaveChanges();";

            Assert.That(
                generated.Replace(" ", "").Replace("\r", ""),
                Is.EqualTo(expected.Replace(" ", "").Replace("\r", "")));
        }

        [Test]
        public void Insert_Transpiler_Output_Matches_Expected_Linq_For_MultiRow() {

            string generated = SqlToLinqConverter.Convert(
                "INSERT INTO Users (Name, Age) VALUES ('Bob', 25), ('Alice', 19)");

            string expected =
                "db.Users.AddRange(new List<Users>\n{\n    new Users { Name = \"Bob\", Age = 25 },\n    new Users { Name = \"Alice\", Age = 19 }\n});\ndb.SaveChanges();";

            Assert.That(
                generated.Replace(" ", "").Replace("\r", ""),
                Is.EqualTo(expected.Replace(" ", "").Replace("\r", "")));
        }

        [Test]
        public void Insert_Select_With_Where_Adds_Filtered_Rows_As_New_Entity() {

            int countBefore = _db.Orders.Count();

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Orders (Owner, Item, Qty) SELECT Id, 'Default', 0 FROM Users WHERE Role = 'Admin'"));

            Assert.That(_db.Orders.Count(), Is.EqualTo(countBefore + 2));
            Assert.That(_db.Orders.Any(o => o.Owner == 1 && o.Item == "Default" && o.Qty == 0), Is.True);
            Assert.That(_db.Orders.Any(o => o.Owner == 6 && o.Item == "Default" && o.Qty == 0), Is.True);
        }

        [Test]
        public void Insert_Select_Without_Where_Adds_All_Source_Rows() {

            int countBefore = _db.Orders.Count();

            RunGeneratedInsert(
                SqlToLinqConverter.Convert(
                    "INSERT INTO Orders (Owner, Qty) SELECT Id, Points FROM Users"));

            Assert.That(_db.Orders.Count(), Is.EqualTo(countBefore + 6));
            Assert.That(_db.Orders.Any(o => o.Owner == 1 && o.Qty == 100), Is.True);
            Assert.That(_db.Orders.Any(o => o.Owner == 6 && o.Qty == 200), Is.True);
        }

        [Test]
        public void Update_With_Where_Updates_Only_Matching_Rows() {

            RunGeneratedUpdate(
                SqlToLinqConverter.Convert(
                    "UPDATE Users SET Role = 'Admin' WHERE Age > 30"));

            Assert.That(_db.Users.Where(u => u.Age > 30).All(u => u.Role == "Admin"), Is.True);

            // Only bob (Age 40) is affected, the other rows keep their original role
            Assert.That(_db.Users.First(u => u.Name == "bob").Role, Is.EqualTo("Admin"));
            Assert.That(_db.Users.First(u => u.Name == "Bab").Role, Is.EqualTo("User"));
            Assert.That(_db.Users.First(u => u.Name == "Bcb").Role, Is.EqualTo("User"));
            Assert.That(_db.Users.First(u => u.Name == "B.b").Role, Is.EqualTo("User"));
            Assert.That(_db.Users.Count(u => u.Role == "Admin"), Is.EqualTo(3));
        }

        [Test]
        public void Update_Without_Where_Updates_All_Rows() {

            RunGeneratedUpdate(
                SqlToLinqConverter.Convert(
                    "UPDATE Users SET Points = 0"));

            Assert.That(_db.Users.All(u => u.Points == 0), Is.True);
        }

        [Test]
        public void Update_Multiple_Columns_Mixed_Constant_And_Expression() {

            RunGeneratedUpdate(
                SqlToLinqConverter.Convert(
                    "UPDATE Users SET Points = Points + 10, Bonus = 0 WHERE Role = 'User'"));

            Assert.That(_db.Users.First(u => u.Name == "Bab").Points, Is.EqualTo(60));
            Assert.That(_db.Users.First(u => u.Name == "Bcb").Points, Is.EqualTo(30));
            Assert.That(_db.Users.First(u => u.Name == "B.b").Points, Is.EqualTo(20));

            Assert.That(_db.Users.Where(u => u.Role == "User").All(u => u.Bonus == 0), Is.True);
            Assert.That(_db.Users.First(u => u.Name == "Bob").Points, Is.EqualTo(100));
        }

        [Test]
        public void Update_With_Null_Value_Sets_Column_To_Null() {

            RunGeneratedUpdate(
                SqlToLinqConverter.Convert(
                    "UPDATE Users SET Bonus = NULL WHERE Age < 18"));

            Assert.That(_db.Users.First(u => u.Name == "Bcb").Bonus, Is.Null);
            Assert.That(_db.Users.First(u => u.Name == "Bob").Bonus, Is.EqualTo(10));
        }

        [Test]
        public void Update_Transpiler_Output_Matches_Expected_For_Expression_Value() {

            string generated = SqlToLinqConverter.Convert(
                "UPDATE Users SET Points = Points * 2 WHERE Role = 'Admin'");

            string expected =
                "db.Users.Where(x => x.Role == \"Admin\").ExecuteUpdateAsync(s => s\n" +
                "        .SetProperty(x => x.Points, x => x.Points * 2))";

            Assert.That(
                generated.Replace(" ", "").Replace("\r", ""),
                Is.EqualTo(expected.Replace(" ", "").Replace("\r", "")));
        }

        private void RunGeneratedUpdate(string linqCode) {

            string source = $@"
        using System;
        using System.Linq;
        using System.Collections.Generic;
        using Microsoft.EntityFrameworkCore;
        using SqlToLinq.Tests;

        using Users      = SqlToLinq.Tests.User;
        using Orders     = SqlToLinq.Tests.Order;
        using Products   = SqlToLinq.Tests.Product;
        using Categories = SqlToLinq.Tests.Category;
        using Warehouses = SqlToLinq.Tests.Warehouse;

        public static class UpdateRunner {{
            public static void Run(TestDbContext db) {{
                {linqCode}.GetAwaiter().GetResult();
                db.ChangeTracker.Clear();
            }}
        }}";

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName: "UpdateRunner",
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
            var type = assembly.GetType("UpdateRunner");
            var method = type.GetMethod("Run");

            method.Invoke(null, new object[] { _db });
        }

        private void RunGeneratedInsert(string linqCode) {

            string source = $@"
                using System;
                using System.Linq;
                using System.Collections.Generic;
                using Microsoft.EntityFrameworkCore;
                using SqlToLinq.Tests;

                using Users      = SqlToLinq.Tests.User;
                using Orders     = SqlToLinq.Tests.Order;
                using Products   = SqlToLinq.Tests.Product;
                using Categories = SqlToLinq.Tests.Category;
                using Warehouses = SqlToLinq.Tests.Warehouse;

                public static class InsertRunner {{
                    public static void Run(TestDbContext db) {{
                        {linqCode}
                    }}
            }}";

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName: "InsertRunner",
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
            var type = assembly.GetType("InsertRunner");
            var method = type.GetMethod("Run");

            method.Invoke(null, new object[] { _db });
        }
    }
}
