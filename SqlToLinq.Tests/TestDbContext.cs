using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SqlToLinq.Tests {

    public class User {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? Age { get; set; }
        public string? Role { get; set; }
        public int? Points { get; set; }
        public int? Bonus { get; set; }
        public DateTime? CreatedAt { get; set; }
    }

    public class Order {
        public int Id { get; set; }
        public int? Owner { get; set; }
        public string? Item { get; set; }
        public int? Qty { get; set; }
    }

    public class Product {
        public int Id { get; set; }
        public int? Parent { get; set; }
        public string? Title { get; set; }
        public int? Price { get; set; }
    }

    public class Category {
        public int Id { get; set; }
        public int? Parent { get; set; }
        public string? Label { get; set; }
    }

    public class Warehouse {
        public int Id { get; set; }
        public int? Parent { get; set; }
        public string? Location { get; set; }
    }

    public class TestDbContext : DbContext {

        public DbSet<User> Users { get; set; }

        public DbSet<Order> Orders { get; set; }

        public DbSet<Product> Products { get; set; }

        public DbSet<Category> Categories { get; set; }

        public DbSet<Warehouse> Warehouses { get; set; }

        private readonly SqliteConnection _connection;

        public TestDbContext(SqliteConnection connection) {
            _connection = connection;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
            optionsBuilder.UseSqlite(_connection);
        }
    }

    public static class TestSeedData {
        public static void Seed(DbContext db) {

            db.Set<User>().AddRange(
                new User { Id = 1, Name = "Bob", Age = 25, Role = "Admin", Points = 100, Bonus = 10, CreatedAt = new DateTime(2022, 1, 15, 0, 0, 0, DateTimeKind.Utc) },
                new User { Id = 2, Name = "Bab", Age = 30, Role = "User", Points = 50, Bonus = 5, CreatedAt = new DateTime(2022, 3, 20, 0, 0, 0, DateTimeKind.Utc) },
                new User { Id = 3, Name = "Bcb", Age = 17, Role = "User", Points = 20, Bonus = 0, CreatedAt = new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
                new User { Id = 4, Name = "bob", Age = 40, Role = "Moderator", Points = 80, Bonus = 15, CreatedAt = new DateTime(2021, 11, 5, 0, 0, 0, DateTimeKind.Utc) },
                new User { Id = 5, Name = "B.b", Age = 22, Role = "User", Points = 10, Bonus = 0, CreatedAt = new DateTime(2023, 8, 22, 0, 0, 0, DateTimeKind.Utc) },
                new User { Id = 6, Name = "Alice", Age = 19, Role = "Admin", Points = 200, Bonus = 50, CreatedAt = new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc) }
            );

            db.Set<Order>().AddRange(
                new Order { Id = 1, Owner = 1, Item = "Laptop", Qty = 1 },
                new Order { Id = 2, Owner = 1, Item = "Mouse", Qty = 2 },
                new Order { Id = 3, Owner = 2, Item = "Keyboard", Qty = 1 },
                new Order { Id = 4, Owner = 6, Item = "Monitor", Qty = 1 }
            );

            db.Set<Product>().AddRange(
                new Product { Id = 1, Parent = 1, Title = "Keyboard Skin", Price = 15 },
                new Product { Id = 2, Parent = 1, Title = "Mouse Pad", Price = 8 },
                new Product { Id = 3, Parent = 3, Title = "USB Cable", Price = 5 },
                new Product { Id = 4, Parent = 4, Title = "HDMI Cable", Price = 12 }
            );

            db.Set<Category>().AddRange(
                new Category { Id = 1, Parent = 1, Label = "Accessories" },
                new Category { Id = 2, Parent = 2, Label = "Accessories" },
                new Category { Id = 3, Parent = 4, Label = "Cables" }
            );

            db.Set<Warehouse>().AddRange(
                new Warehouse { Id = 1, Parent = 1, Location = "Budapest" },
                new Warehouse { Id = 2, Parent = 3, Location = "Debrecen" }
            );

            db.SaveChanges();
        }
    }
}