using Microsoft.EntityFrameworkCore;

namespace Microservice1.API.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}
