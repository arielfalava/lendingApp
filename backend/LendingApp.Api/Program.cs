using System.Text.Json.Serialization;
using LendingApp.Api.Cqrs;
using LendingApp.Api.Features.Borrowers;
using LendingApp.Api.Features.Dashboard;
using LendingApp.Api.Features.History;
using LendingApp.Api.Features.Loans;
using LendingApp.Api.Features.Payments;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LendingDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Lending") ?? "Data Source=lending.db"));
builder.Services.AddCqrs(typeof(Program).Assembly);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Map domain exceptions to HTTP problem responses.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (ValidationException ex)
    {
        await Results.ValidationProblem(ex.Errors).ExecuteAsync(ctx);
    }
    catch (NotFoundException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound).ExecuteAsync(ctx);
    }
    catch (BadHttpRequestException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(ctx);
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // GET /openapi/v1.json
}

app.MapBorrowers();
app.MapLoans();
app.MapPayments();
app.MapHistory();
app.MapDashboard();

// Serve the built React app (frontend `npm run build` outputs into wwwroot) when present.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LendingDbContext>();
    await DatabaseSetup.EnsureSchemaAsync(db);
    if (app.Configuration.GetValue("SeedDemoData", true))
        await DemoData.SeedAsync(scope.ServiceProvider.GetRequiredService<IDispatcher>(), db);
}

app.Run();
