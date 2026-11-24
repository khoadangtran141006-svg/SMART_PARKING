using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Smartparking.Api;

public static class DatabaseBootstrapper
{
    private const string DatabaseName = "SmartParking";
    private static readonly Regex SafeLoginName = new("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public static async Task InitializeAsync(IConfiguration configuration)
    {
        var host = Required(configuration, "DB_HOST");
        var port = configuration["DB_PORT"] ?? "1433";
        var adminUser = Required(configuration, "DB_ADMIN_USER");
        var adminPassword = Required(configuration, "DB_ADMIN_PASSWORD");
        var applicationUser = Required(configuration, "APP_DB_USER");
        var applicationPassword = Required(configuration, "APP_DB_PASSWORD");

        if (!SafeLoginName.IsMatch(applicationUser))
        {
            throw new InvalidOperationException("APP_DB_USER chỉ được chứa chữ, số và dấu gạch dưới.");
        }

        var adminConnectionString = BuildConnectionString(
            host,
            port,
            "master",
            adminUser,
            adminPassword,
            configuration);

        await using var adminConnection = new SqlConnection(adminConnectionString);
        await adminConnection.OpenAsync();
        await ExecuteAsync(
            adminConnection,
            "IF DB_ID(N'SmartParking') IS NULL CREATE DATABASE [SmartParking];");
        await EnsureApplicationLoginAsync(
            adminConnection,
            applicationUser,
            applicationPassword);

        var databaseConnectionString = new SqlConnectionStringBuilder(adminConnectionString)
        {
            InitialCatalog = DatabaseName,
        }.ConnectionString;

        await using (var databaseConnection = new SqlConnection(databaseConnectionString))
        {
            await databaseConnection.OpenAsync();
            var schemaPath = Path.Combine(
                AppContext.BaseDirectory,
                "database",
                "init_smartparking_sqlserver.sql");
            var schema = await File.ReadAllTextAsync(schemaPath);

            foreach (var batch in Regex.Split(schema, @"(?im)^\s*GO\s*(?:--.*)?$"))
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await ExecuteAsync(databaseConnection, batch);
                }
            }

            await EnsureApplicationUserAsync(
                databaseConnection,
                applicationUser);
        }

        Console.WriteLine("Database schema and restricted application login are ready.");
    }

    private static string BuildConnectionString(
        string host,
        string port,
        string database,
        string user,
        string password,
        IConfiguration configuration)
    {
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database,
            UserID = user,
            Password = password,
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

        return connectionString.ConnectionString;
    }

    private static async Task EnsureApplicationLoginAsync(
        SqlConnection connection,
        string user,
        string password)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = @UserName)
            BEGIN
                DECLARE @statement nvarchar(max) =
                    N'CREATE LOGIN ' + QUOTENAME(@UserName) +
                    N' WITH PASSWORD = ' + QUOTENAME(@Password, '''') +
                    N', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF';
                EXEC sp_executesql @statement;
            END
            """;
        command.Parameters.Add("@UserName", SqlDbType.NVarChar, 128).Value = user;
        command.Parameters.Add("@Password", SqlDbType.NVarChar, 128).Value = password;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureApplicationUserAsync(
        SqlConnection connection,
        string user)
    {
        await using (var createUser = connection.CreateCommand())
        {
            createUser.CommandText = """
                IF DATABASE_PRINCIPAL_ID(@UserName) IS NULL
                BEGIN
                    DECLARE @statement nvarchar(max) =
                        N'CREATE USER ' + QUOTENAME(@UserName) +
                        N' FOR LOGIN ' + QUOTENAME(@UserName);
                    EXEC sp_executesql @statement;
                END
                """;
            createUser.Parameters.Add("@UserName", SqlDbType.NVarChar, 128).Value = user;
            await createUser.ExecuteNonQueryAsync();
        }

        foreach (var role in new[] { "db_datareader", "db_datawriter" })
        {
            await using var addRole = connection.CreateCommand();
            addRole.CommandText = """
                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.database_role_members AS membership
                    INNER JOIN sys.database_principals AS database_role
                        ON database_role.principal_id = membership.role_principal_id
                    INNER JOIN sys.database_principals AS member
                        ON member.principal_id = membership.member_principal_id
                    WHERE database_role.name = @RoleName
                      AND member.name = @UserName
                )
                BEGIN
                    DECLARE @statement nvarchar(max) =
                        N'ALTER ROLE ' + QUOTENAME(@RoleName) +
                        N' ADD MEMBER ' + QUOTENAME(@UserName);
                    EXEC sp_executesql @statement;
                END
                """;
            addRole.Parameters.Add("@RoleName", SqlDbType.NVarChar, 128).Value = role;
            addRole.Parameters.Add("@UserName", SqlDbType.NVarChar, 128).Value = user;
            await addRole.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static string Required(IConfiguration configuration, string key)
    {
        return configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Thiếu cấu hình bắt buộc {key}.");
    }
}
