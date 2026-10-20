namespace Microservice1.API.Data;

// Uygulama başladıktan sonra arka planda seed data yükler.
// Gerçek hayattaki yavaş açılışı (migration, cache ısıtma, büyük veri yükleme) taklit etmek için
// Startup:SeedDelaySeconds kadar bekler. Bu sürede HTTP sunucusu ayaktadır ama uygulama hazır değildir.
public class DatabaseSeeder(
    IServiceScopeFactory scopeFactory,
    StartupState startupState,
    IConfiguration configuration,
    ILogger<DatabaseSeeder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = configuration.GetValue("Startup:SeedDelaySeconds", 0);
        logger.LogInformation("Seed işlemi başladı, {Delay} sn beklenecek", delay);
        await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!db.Products.Any())
        {
            db.Products.AddRange(
                new Product { Name = "Laptop", Price = 35000m, Stock = 10 },
                new Product { Name = "Mouse", Price = 450m, Stock = 100 },
                new Product { Name = "Klavye", Price = 1200m, Stock = 50 },
                new Product { Name = "Monitör", Price = 8500m, Stock = 25 },
                new Product { Name = "Kulaklık", Price = 1800m, Stock = 40 });
            await db.SaveChangesAsync(stoppingToken);
        }

        startupState.MarkCompleted();
        logger.LogInformation("Seed işlemi tamamlandı, uygulama başlatıldı");
    }
}
