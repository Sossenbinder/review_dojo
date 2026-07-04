using Microsoft.EntityFrameworkCore;
using ReviewDojo.Api;
using ReviewDojo.Data;
using ReviewDojo.Generator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddDbContext<ReviewDojoContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=reviewdojo.db"));
var useMock = builder.Configuration.GetValue<bool>("Anthropic:UseMock");
var openAiBase = builder.Configuration["OpenAI:BaseUrl"];

if (useMock)
{
    builder.Services.AddSingleton<IAnthropicClient, MockAnthropicClient>();
}
else if (!string.IsNullOrEmpty(openAiBase))
{
    builder.Services.AddHttpClient("llm");
    builder.Services.AddSingleton<IAnthropicClient>(sp =>
        new OpenAiCompatibleClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm"),
            openAiBase!, builder.Configuration["OpenAI:ApiKey"],
            builder.Configuration.GetValue("OpenAI:JsonMode", true)));
}
else
{
    builder.Services.AddHttpClient<IAnthropicClient, AnthropicClient>();
}

// Model string flows through to the LLM request. Pick per provider.
var model = useMock ? "mock"
    : !string.IsNullOrEmpty(openAiBase) ? (builder.Configuration["OpenAI:Model"] ?? "local-model")
    : (builder.Configuration["Anthropic:Model"] ?? "claude-sonnet-4-6");
builder.Services.AddScoped(sp => new DiffGenerator(sp.GetRequiredService<IAnthropicClient>(), model));

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
