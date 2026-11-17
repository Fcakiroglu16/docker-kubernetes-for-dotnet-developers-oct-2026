var builder = WebApplication.CreateBuilder(args);

// Kubernetes probe'ları için health check.
builder.Services.AddHealthChecks();

var app = builder.Build();

// Image sürümü Dockerfile'da APP_VERSION ortam değişkeni ile verilir.
// Pod adı (MachineName) hangi pod'un cevap verdiğini gösterir.
app.MapGet("/", () => Results.Ok(new
{
    message = "Week6.API çalışıyor........",
    version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "local",
    pod = Environment.MachineName
}));

app.MapHealthChecks("/health");

app.Run();
