using Microsoft.EntityFrameworkCore;
using static System.String;

namespace PatternsRepo.Data
{
    // CP001: SQL built with Format after using static System.String. Insecure on purpose: this file is never built.
    public class StaticFormatQueries
    {
        private readonly ShopContext _db;

        public int DeleteByEmail(string email)
        {
            return _db.Database.ExecuteSqlRaw(Format("DELETE FROM Users WHERE Email = '{0}'", email)); // CP001
        }
    }
}
