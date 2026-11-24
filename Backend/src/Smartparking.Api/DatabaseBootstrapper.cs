
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Smartparking.Api;

public static class DatabaseBootstrapper
{
    public static async Task InitializeAsync(IConfiguration configuration)
    {
        var connectionString = GetConnectionString(configuration);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "database",
            "init_smartparking_postgresql.sql");

        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException(
                $"Không tìm thấy file khởi tạo PostgreSQL: {schemaPath}");
        }

        var schema = await File.ReadAllTextAsync(schemaPath);

        await using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.CommandTimeout = 120;

        await command.ExecuteNonQueryAsync();

        Console.WriteLine(
            "PostgreSQL database schema initialized successfully.");
    }

    private static string GetConnectionString(IConfiguration configuration)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? configuration["DATABASE_URL"]
            ?? configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Thiếu cấu hình DATABASE_URL. " +
                "Hãy cấu hình chuỗi kết nối PostgreSQL trong biến môi trường.");
        }

        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            && (uri.Scheme == "postgres" || uri.Scheme == "postgresql"))
        {
            var userInfo = uri.UserInfo.Split(':', 2);

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(
                    uri.AbsolutePath.TrimStart('/')),
                Username = Uri.UnescapeDataString(userInfo[0]),
                Password = userInfo.Length > 1
                    ? Uri.UnescapeDataString(userInfo[1])
                    : string.Empty,
                SslMode = SslMode.Require,
                Pooling = true,
                Timeout = 30
            };

            return builder.ConnectionString;
        }

        return connectionString;
    }
}
