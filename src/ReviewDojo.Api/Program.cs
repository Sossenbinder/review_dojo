using Microsoft.EntityFrameworkCore;
using ReviewDojo.Api;
using ReviewDojo.Data;
using ReviewDojo.Generator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddDbContext<ReviewDojoContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=reviewdojo.db"));
if (builder.Configuration.GetValue<bool>("Anthropic:UseMock"))
    builder.Services.AddSingleton<IAnthropicClient, MockAnthropicClient>();
else
    builder.Services.AddHttpClient<IAnthropicClient, AnthropicClient>();
builder.Services.AddScoped(sp => new DiffGenerator(
    sp.GetRequiredService<IAnthropicClient>(),
    builder.Configuration["Anthropic:Model"] ?? "claude-sonnet-4-6"));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ReviewDojoContext>().Database.Migrate();

// This single app also serves the Blazor WebAssembly client (same origin — no CORS,
// one port, one command). Skipped under test hosting, which only drives /api routes.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
}

app.MapDojo();

if (!app.Environment.IsEnvironment("Testing"))
    app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }   // for WebApplicationFactory
