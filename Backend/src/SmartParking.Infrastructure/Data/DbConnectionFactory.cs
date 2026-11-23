using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace SmartParking.Infrastructure.Data;

public class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        var host = configuration["DB_HOST"];
        if (!string.IsNullOrWhiteSpace(host))
        {
            var port = configuration["DB_PORT"] ?? "1433";
            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = $"{host},{port}",
                InitialCatalog = configuration["DB_NAME"] ?? "SmartParking",
                UserID = configuration["DB_USER"] ?? throw new InvalidOperationException("Thiếu DB_USER."),
                Password = configuration["DB_PASSWORD"] ?? throw new InvalidOperationException("Thiếu DB_PASSWORD."),
                TrustServerCertificate = bool.TryParse(configuration["DB_TRUST_SERVER_CERTIFICATE"], out var trustCertificate)
                    && trustCertificate,
                ConnectTimeout = 30,
            };

            connectionString.Encrypt = string.Equals(
                configuration["DB_ENCRYPT"],
                "false",
                StringComparison.OrdinalIgnoreCase)
                    ? SqlConnectionEncryptOption.Optional
                    : SqlConnectionEncryptOption.Mandatory;

            _connectionString = connectionString.ConnectionString;
            return;
        }

        _connectionString =
            configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Không tìm thấy DB_HOST hoặc ConnectionStrings:DefaultConnection.");
    }

    public IDbConnection CreateConnection()
    {
        return new SqlConnection(_connectionString);
    }
}
