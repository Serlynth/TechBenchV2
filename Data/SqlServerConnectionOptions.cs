using Microsoft.Data.SqlClient;

namespace TechBench.Data;

public sealed record SqlServerConnectionOptions(
    string Server,
    string Database,
    bool TrustServerCertificate = false)
{
    public const string DefaultServerName = "CSRI-SQL.CSRI.local";
    public const string DefaultDatabaseName = "TechBench";
    public const string DefaultApplicationName = "TechBench V2";
    public const int DefaultConnectTimeoutSeconds = 15;
    public const int DefaultCommandTimeoutSeconds = 30;

    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;
    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

    public SqlServerConnectionOptions NormalizeAndValidate()
    {
        var server = Server?.Trim() ?? string.Empty;
        var database = Database?.Trim() ?? string.Empty;
        if (server.Length == 0)
        {
            throw new ArgumentException(
                "Enter the Microsoft SQL Server name or server instance.",
                nameof(Server));
        }

        if (database.Length == 0)
        {
            throw new ArgumentException(
                "Enter the TechBench SQL Server database name.",
                nameof(Database));
        }

        if (ConnectTimeoutSeconds is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConnectTimeoutSeconds),
                "The SQL Server connection timeout must be between 1 and 120 seconds.");
        }

        if (CommandTimeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeoutSeconds),
                "The SQL Server command timeout must be between 1 and 600 seconds.");
        }

        return this with
        {
            Server = server,
            Database = database
        };
    }

    public string BuildConnectionString()
    {
        var options = NormalizeAndValidate();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Server,
            InitialCatalog = options.Database,
            IntegratedSecurity = true,
            PersistSecurityInfo = false,
            ApplicationName = DefaultApplicationName,
            ConnectTimeout = options.ConnectTimeoutSeconds,
            Pooling = true,
            MultipleActiveResultSets = false,
            TrustServerCertificate = options.TrustServerCertificate
        };

        // Use the keyword indexer so this remains compatible with SqlClient
        // versions where Encrypt is represented by either a Boolean or an enum.
        builder["Encrypt"] = true;
        return builder.ConnectionString;
    }
}
