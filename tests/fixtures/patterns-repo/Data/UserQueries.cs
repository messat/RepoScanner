using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace PatternsRepo.Data
{
    // CP001: SQL built from strings. Insecure on purpose: this file is never built.
    public class UserQueries
    {
        private readonly ShopContext _db;
        private readonly SqlConnection _connection;

        public IQueryable<User> FindByName(string name)
        {
            return _db.Users.FromSqlRaw("SELECT * FROM Users WHERE Name = '" + name + "'"); // CP001
        }

        public IQueryable<User> FindByEmail(string email)
        {
            return _db.Users.FromSqlRaw($"SELECT * FROM Users WHERE Email = '{email}'"); // CP001
        }

        public int DeleteById(int id)
        {
            return _db.Database.ExecuteSqlRaw(string.Format("DELETE FROM Users WHERE Id = {0}", id)); // CP001
        }

        public SqlCommand CountByRole(string role)
        {
            SqlCommand command = new SqlCommand("SELECT COUNT(*) FROM Users WHERE Role = '" + role + "'", _connection); // CP001
            return command;
        }

        public SqlCommand ListByCity(string city)
        {
            return new SqlCommand { Connection = _connection, CommandText = $"SELECT * FROM Users WHERE City = '{city}'" }; // CP001
        }

        // Not findings: EF Core turns {name} into a parameter, plain SQL is safe however it is joined, a
        // parameter placeholder is safe, and SQL passed in a variable is not traced
        public IQueryable<User> FindByNameSafely(string name)
        {
            return _db.Users.FromSql($"SELECT * FROM Users WHERE Name = {name}");
        }

        public IQueryable<User> ListActive()
        {
            return _db.Users.FromSqlRaw("SELECT * FROM Users " + "WHERE Active = 1");
        }

        public SqlCommand FindByNameWithParameter()
        {
            return new SqlCommand("SELECT * FROM Users WHERE Name = @name", _connection);
        }

        public IQueryable<User> RunSaved(string sql)
        {
            return _db.Users.FromSqlRaw(sql);
        }
    }
}
