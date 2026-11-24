
using Npgsql;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace SmartParking.Infrastructure.Data;

public class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        var connectionString =
            configuration["DATABASE_URL"]
            ?? configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Không tìm thấy DATABASE_URL hoặc ConnectionStrings:DefaultConnection.");
        }

        _connectionString = NormalizeConnectionString(connectionString);
    }

    private static string NormalizeConnectionString(string connectionString)
    {
        // Hỗ trợ PostgreSQL URI từ Neon:
        // postgresql://username:password@host/database?sslmode=require
        if (connectionString.StartsWith(
            "postgres://", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith(
            "postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException(
                    "DATABASE_URL không đúng định dạng PostgreSQL URI.");
            }

            var userInfo = uri.UserInfo.Split(':', 2);

            if (userInfo.Length != 2 || string.IsNullOrWhiteSpace(uri.AbsolutePath))
            {
                throw new InvalidOperationException(
                    "DATABASE_URL thiếu username, password hoặc database.");
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(
                    uri.AbsolutePath.TrimStart('/')),
                Username = Uri.UnescapeDataString(userInfo[0]),
                Password = Uri.UnescapeDataString(userInfo[1]),
                SslMode = SslMode.Require,
                Pooling = true,
                Timeout = 30
            };

            return builder.ConnectionString;
        }

        // Hỗ trợ connection string dạng key=value của Npgsql.
        return new NpgsqlConnectionStringBuilder(connectionString)
            .ConnectionString;
    }

    public IDbConnection CreateConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}
