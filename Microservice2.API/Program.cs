using Microservice2.API;
using Microservice2.API.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// PostgreSQL bağlantısı. Dev: appsettings.Development.json (NodePort 30432), Kubernetes: appsettings.Production.json (postgres-primary Service).
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// RabbitMQ consumer. Development -> localhost:30672 (NodePort), Production -> rabbitmq-external Service (appsettings.Production.json).
builder.Services.AddSingleton<ReceivedMessageStore>();
builder.Services.AddHostedService<RabbitMqConsumer>();

var app = builder.Build();

// Bekleyen migration'ları uygular (veritabanı yoksa önce oluşturur).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

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

app.MapGet("/todos", async (AppDbContext db) => await db.Todos.AsNoTracking().OrderBy(t => t.Id).ToListAsync())
    .WithName("GetTodos");

app.MapGet("/todos/{id:int}", async (int id, AppDbContext db) =>
        await db.Todos.FindAsync(id) is { } todo ? Results.Ok(todo) : Results.NotFound())
    .WithName("GetTodo");

app.MapPost("/todos", async (TodoItem todo, AppDbContext db) =>
    {
        db.Todos.Add(todo);
        await db.SaveChangesAsync();
        return Results.Created($"/todos/{todo.Id}", todo);
    })
    .WithName("CreateTodo");

app.MapPut("/todos/{id:int}", async (int id, TodoItem input, AppDbContext db) =>
    {
        var todo = await db.Todos.FindAsync(id);
        if (todo is null) return Results.NotFound();
        todo.Title = input.Title;
        todo.IsCompleted = input.IsCompleted;
        await db.SaveChangesAsync();
        return Results.Ok(todo);
    })
    .WithName("UpdateTodo");

app.MapDelete("/todos/{id:int}", async (int id, AppDbContext db) =>
    {
        var todo = await db.Todos.FindAsync(id);
        if (todo is null) return Results.NotFound();
        db.Todos.Remove(todo);
        await db.SaveChangesAsync();
        return Results.NoContent();
    })
    .WithName("DeleteTodo");

// Kuyruktan alınan son mesajlar (bellekte tutulur).
app.MapGet("/messages", (ReceivedMessageStore store) => store.GetAll()).WithName("GetReceivedMessages");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}