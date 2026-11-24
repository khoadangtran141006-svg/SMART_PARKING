using Smartparking.Api;
using SmartParking.Application.Interfaces;
using SmartParking.Application.Services;
using SmartParking.Infrastructure.Data;
using SmartParking.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

var allowedOrigins = builder.Configuration["CORS_ALLOWED_ORIGINS"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? [];

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy(
        "VercelFrontend",
        policy => policy
            .WithOrigins(allowedOrigins)
            .WithMethods("GET", "POST", "DELETE", "OPTIONS")
            .AllowAnyHeader()));
}

builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddScoped<IPlateScanRepository, PlateScanRepository>();
builder.Services.AddScoped<IPlateScanService, PlateScanService>();

if (args.Contains("--initialize-db", StringComparer.Ordinal))
{
    await DatabaseBootstrapper.InitializeAsync(builder.Configuration);
    return;
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (allowedOrigins.Length > 0)
{
    app.UseCors("VercelFrontend");
}

app.UseHttpsRedirection();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();
