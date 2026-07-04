using Microsoft.EntityFrameworkCore;
using ReviewDojo.Api;
using ReviewDojo.Data;
using ReviewDojo.Generator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddDbContext<ReviewDojoContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=reviewdojo.db"));
builder.Services.AddHttpClient<IAnthropicClient, AnthropicClient>();
builder.Services.AddScoped(sp => new DiffGenerator(
    sp.GetRequiredService<IAnthropicClient>(),
    builder.Configuration["Anthropic:Model"] ?? "claude-sonnet-4-6"));
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(builder.Configuration["ClientOrigin"] ?? "https://localhost:7002")
     .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ReviewDojoContext>().Database.Migrate();
app.UseCors();
app.MapDojo();
app.Run();

public partial class Program { }   // for WebApplicationFactory
