using Microservice1.API.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Kubernetes probe'ları için health check'ler.
// "live" etiketli check'ler /health/live, "ready" etiketli check'ler /health/ready endpoint'inde çalışır.
// Bağımlılık check'leri (veritabanı, cache vb.) liveness'a değil, sadece readiness'a eklenir.
builder.Services.AddHealthChecks()
    // Liveness: sadece uygulama süreci cevap veriyor mu. Hiçbir bağımlılığa bakmaz.
    .AddCheck("self", () => HealthCheckResult.Healthy("API ayakta"), tags: ["live"])
    // Hazır EF Core health check'i: DbContext ile veritabanına bağlanılabiliyor mu (CanConnectAsync).
    // Readiness: veritabanı bağımlılığı.
    .AddDbContextCheck<AppDbContext>(name: "database", tags: ["ready"]);

// EF Core InMemory: gerçek bir veritabanı olmadan, bellek içinde çalışan örnek veritabanı.
builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("Microservice1Db"));

// Açılış durumu ve seed data. Seed bitene kadar /health/startup 503 döner.
builder.Services.AddSingleton<StartupState>();
builder.Services.AddHostedService<DatabaseSeeder>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    
}
app.MapOpenApi();
app.MapScalarApiReference();

// Startup: açılış işleri (seed data) bitti mi? startupProbe bu endpoint'i kullanır.
// Bitene kadar 503 döner. Bu sırada liveness ve readiness probe'ları henüz çalışmaz.
app.MapGet("/health/startup", (StartupState state) =>
    state.IsCompleted ? Results.Ok("Started") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
    .ExcludeFromDescription();

// Liveness: uygulama süreci cevap veriyor mu? Sadece "live" etiketli check'ler çalışır, bağımlılık kontrol edilmez.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });

// Readiness: uygulama trafik almaya hazır mı? Sadece "ready" etiketli check'ler çalışır.
// Yanıt gövdesinde hangi check'in ne durumda olduğu görünsün diye JSON yazılır.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description })
        });
    }
});



var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
    {
        var forecast = Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                (
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast");

// ConfigMap ve Secret'tan ortam değişkeni olarak gelen değerler IConfiguration'a otomatik girer
// ("App__Mode" ortam değişkeni -> "App:Mode" ayarı).
// ConfigMap değerleri olduğu gibi döner. Secret'ın değeri asla döndürülmez, sadece tanımlı olup olmadığı ve uzunluğu görünür.
app.MapGet("/config", (IConfiguration config) => new
{
    Mode = config["App:Mode"],
    MaxItems = config.GetValue<int?>("App:MaxItems"),
    DbUserSet = !string.IsNullOrEmpty(config["Database:User"]),
    DbPasswordSet = !string.IsNullOrEmpty(config["Database:Password"]),
    DbPasswordLength = config["Database:Password"]?.Length ?? 0
}).WithName("GetConfig");

app.MapGet("/products", async (AppDbContext db) => await db.Products.AsNoTracking().ToListAsync())
    .WithName("GetProducts");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}