using System.Data;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TechBench.ServerManager;

internal sealed class SqlAdminRepository(AppPaths paths)
{
    internal const int MinimumSupportedSchemaVersion = 13;
    internal const int MaximumSupportedSchemaVersion = 15;
    public SynchronizationConfiguration Load()
    {
        using var connection = OpenAdminConnection();
        var configuration = new SynchronizationConfiguration();
        LoadSettings(connection, configuration);
        configuration.WhdStatus = LoadStatus(connection, "tb_app.GetWhdSyncStatus", false);
        configuration.SageStatus = LoadStatus(connection, "tb_app.GetSageSyncStatus", true);
        configuration.FireDrillStatus = LoadStatus(connection, "tb_app.GetFireDrillSyncStatus", true);
        LoadMappings(connection, configuration);
        LoadAuthPointMappings(connection, configuration);
        LoadTechnicians(connection, configuration);
        return configuration;
    }

    public void SaveSettings(IDictionary<string, string> settings, IDictionary<string, byte[]> expectedVersions)
    {
        using var connection = OpenAdminConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var pair in settings.OrderBy(static value => value.Key, StringComparer.OrdinalIgnoreCase))
            {
                using var command = StoredProcedure(connection, "tb_app.AdminSaveOrganizationSetting", transaction);
                command.Parameters.Add("@SettingKey", SqlDbType.NVarChar, 200).Value = pair.Key;
                command.Parameters.Add("@SettingValue", SqlDbType.NVarChar, -1).Value = pair.Value;
                command.Parameters.Add("@ExpectedRowVersion", SqlDbType.Binary, 8).Value =
                    expectedVersions.TryGetValue(pair.Key, out var rowVersion) ? rowVersion : DBNull.Value;
                command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException($"SQL Server did not save '{pair.Key}'.");
            }
            transaction.Commit();
        }
        catch
        {
            try
            {
                if (transaction.Connection is not null)
                    transaction.Rollback();
            }
            catch
            {
                // SQL Server may already have rolled back the outer transaction.
            }
            throw;
        }
    }

    public string RequestWhdSync()
        => RequestWhdSync("Full").Status;

    public SyncRequestReceipt RequestWhdTechnicianSync()
        => RequestWhdSync("Technicians");

    public SyncStatus LoadWhdStatus()
    {
        using var connection = OpenAdminConnection();
        return LoadStatus(connection, "tb_app.GetWhdSyncStatus", false);
    }

    private SyncRequestReceipt RequestWhdSync(string requestType)
    {
        using var connection = OpenAdminConnection();
        using var command = StoredProcedure(connection, "tb_app.AdminRequestWhdSync");
        command.Parameters.Add("@RequestType", SqlDbType.NVarChar, 40).Value = requestType;
        var requestId = Guid.NewGuid();
        command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = requestId;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("SQL Server did not return the WHD synchronization request.");
        return new SyncRequestReceipt(
            ReadNullableGuid(reader, "RequestId") ?? requestId,
            ReadString(reader, "Status", "Queued"));
    }

    public string RequestSageSync(bool allowLargeRemoval, Guid? confirmedRequestId)
    {
        using var connection = OpenAdminConnection();
        using var command = StoredProcedure(connection, "tb_app.AdminRequestSageSync");
        command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        command.Parameters.Add("@AllowLargeRemoval", SqlDbType.Bit).Value = allowLargeRemoval;
        command.Parameters.Add("@ConfirmedRequestId", SqlDbType.UniqueIdentifier).Value = confirmedRequestId ?? (object)DBNull.Value;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadString(reader, "Status", "Queued") : "Queued";
    }

    public SyncRequestReceipt RequestFireDrillSync()
    {
        using var connection = OpenAdminConnection();
        using var command = StoredProcedure(connection, "tb_app.AdminRequestFireDrillSync");
        command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("SQL Server did not return the Credentials synchronization request.");
        var requestId = ReadNullableGuid(reader, "RequestId")
            ?? throw new InvalidOperationException("SQL Server returned a Credentials synchronization request without an ID.");
        return new SyncRequestReceipt(requestId, ReadString(reader, "Status", "Queued"));
    }

    public SyncStatus LoadFireDrillStatus()
    {
        using var connection = OpenAdminConnection();
        return LoadStatus(connection, "tb_app.GetFireDrillSyncStatus", true);
    }

    public void SaveMappings(IReadOnlyCollection<UserMappingAssignment> mappings)
    {
        using var connection = OpenAdminConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var mapping in mappings.OrderBy(static item => item.LoginName, StringComparer.OrdinalIgnoreCase))
            {
                using var command = StoredProcedure(connection, "tb_app.AdminSaveWhdUserMapping", transaction);
                command.Parameters.Add("@WindowsLoginName", SqlDbType.NVarChar, 256).Value = mapping.LoginName;
                command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 160).Value = mapping.DisplayName;
                command.Parameters.Add("@IsAdmin", SqlDbType.Bit).Value = mapping.IsAdmin;
                command.Parameters.Add("@TechnicianExternalId", SqlDbType.NVarChar, 120).Value =
                    string.IsNullOrWhiteSpace(mapping.TechnicianExternalId)
                        ? DBNull.Value
                        : mapping.TechnicianExternalId;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            try
            {
                if (transaction.Connection is not null)
                    transaction.Rollback();
            }
            catch
            {
                // SQL Server may already have rolled back the outer transaction.
            }
            throw;
        }
    }

    public void SaveAuthPointMappings(
        IReadOnlyCollection<AuthPointMappingAssignment> mappings)
    {
        using var connection = OpenAdminConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var mapping in mappings
                         .Where(static item => !string.IsNullOrWhiteSpace(item.AuthPointLogin))
                         .OrderBy(static item => item.LoginName, StringComparer.OrdinalIgnoreCase))
            {
                using var command = StoredProcedure(
                    connection,
                    "tb_app.AdminSaveAuthPointUserMapping",
                    transaction);
                command.Parameters.Add("@LoginName", SqlDbType.NVarChar, 256).Value =
                    mapping.LoginName;
                command.Parameters.Add("@AuthPointLogin", SqlDbType.NVarChar, 256).Value =
                    mapping.AuthPointLogin.Trim();
                command.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = mapping.IsEnabled;
                command.Parameters.Add("@ExpectedRowVersion", SqlDbType.Binary, 8).Value =
                    mapping.ExpectedRowVersion ?? (object)DBNull.Value;
                command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value =
                    Guid.NewGuid();
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            try
            {
                if (transaction.Connection is not null)
                {
                    transaction.Rollback();
                }
            }
            catch
            {
            }

            throw;
        }
    }

    public int ReconcileAuthorizedUsers(IReadOnlyCollection<DirectoryUser> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (users.Count == 0)
        {
            throw new InvalidOperationException(
                "Active Directory returned no authorized TechBench users. "
                + "No SQL users were changed.");
        }

        var snapshot = users
            .OrderBy(static user => user.LoginName, StringComparer.OrdinalIgnoreCase)
            .Select(static user => new
            {
                loginName = user.LoginName,
                displayName = user.DisplayName,
                isAdmin = user.IsAdmin,
                windowsSid = user.WindowsSidHex
            })
            .ToArray();
        var json = JsonSerializer.Serialize(snapshot);

        using var connection = OpenAdminConnection();
        using var command = StoredProcedure(
            connection,
            "tb_app.AdminReconcileWhdAuthorizedUsers");
        command.Parameters.Add("@AuthorizedUsersJson", SqlDbType.NVarChar, -1).Value = json;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "SQL Server did not return the authorized-user reconciliation result.");
        }

        return ReadInt(reader, "RetiredCount");
    }

    public int VerifyRequiredSchema(int requiredVersion)
    {
        using var connection = OpenAdminConnection(requireExactVersion: false);
        using var command = StoredProcedure(connection, "tb_app.GetCurrentUserContext");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("TechBench SQL Server returned no user context.");
        var installed = ReadInt(reader, "SchemaVersion");
        if (installed != requiredVersion)
        {
            throw new InvalidOperationException($"This package requires database schema {requiredVersion}, but SQL Server reports {installed}. Apply the matching SQL installer first.");
        }
        return installed;
    }

    private SqlConnection OpenAdminConnection(bool requireExactVersion = true)
    {
        var configuration = paths.ReadConfiguration();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = configuration.SqlServer,
            InitialCatalog = configuration.Database,
            IntegratedSecurity = true,
            ApplicationName = "TechBench Server Manager",
            ConnectTimeout = 15,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = configuration.TrustServerCertificate
        };
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            using var command = StoredProcedure(connection, "tb_app.GetCurrentUserContext");
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("TechBench SQL Server returned no user context.");
            var schema = ReadInt(reader, "SchemaVersion");
            var isAdmin = ReadBool(reader, "IsAdmin");
            var login = ReadString(reader, "AuthenticatedLoginName");
            if (requireExactVersion
                && (schema < MinimumSupportedSchemaVersion
                    || schema > MaximumSupportedSchemaVersion))
            {
                throw new InvalidOperationException(
                    $"Server Manager supports database schemas "
                    + $"{MinimumSupportedSchemaVersion} through {MaximumSupportedSchemaVersion}; "
                    + $"SQL Server reports {schema}.");
            }
            if (!isAdmin)
                throw new UnauthorizedAccessException($"'{login}' is not a TechBench Admin. Add this Windows account to CSRI\\TechBench_Admins.");
            return connection;
        }
        catch (SqlException ex) when (IsDatabaseLoginFailure(ex))
        {
            connection.Dispose();
            throw new UnauthorizedAccessException(DescribeDatabaseLoginFailure(
                WindowsIdentity.GetCurrent().Name,
                configuration.Database), ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static string DescribeDatabaseLoginFailure(string windowsLogin, string database) =>
        $"SQL Server rejected Windows account '{windowsLogin}' for database '{database}'. " +
        "This is an AD/SQL authorization issue, not a server-package problem. Confirm the exact account is a member " +
        "of CSRI\\TechBench_Admins, then fully sign out of Windows and sign back in so the new group appears in " +
        "'whoami /groups'. If it already appears, have the DBA verify the CSRI\\TechBench_Admins SQL login and TechBench database user.";

    private static bool IsDatabaseLoginFailure(SqlException exception) =>
        exception.Number is 4060 or 18456 ||
        exception.Message.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("Login failed", StringComparison.OrdinalIgnoreCase);

    private static void LoadSettings(SqlConnection connection, SynchronizationConfiguration configuration)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Whd.BaseUrl", "Whd.AuthenticationMode", "Whd.ServiceUsername", "Whd.AutoSyncEnabled",
            "Whd.AutoSyncMinutes", "Sage.SyncDsn", "Sage.SyncUsername",
            "FireDrill.SourcePath", "FireDrill.DailySyncEnabled", "FireDrill.DailySyncTime",
            "AuthPoint.Enabled", "AuthPoint.BaseApiUrl", "AuthPoint.AccountId",
            "AuthPoint.ResourceId", "AuthPoint.AccessId"
        };
        using var command = StoredProcedure(connection, "tb_app.GetSettings");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = ReadString(reader, "SettingKey");
            if (!wanted.Contains(key) || !ReadString(reader, "ScopeType").Equals("Organization", StringComparison.OrdinalIgnoreCase)) continue;
            configuration.Settings[key] = ReadString(reader, "SettingValue");
            var ordinal = TryOrdinal(reader, "RowVersion");
            if (ordinal >= 0 && !reader.IsDBNull(ordinal)) configuration.RowVersions[key] = (byte[])reader.GetValue(ordinal);
        }
    }

    private static SyncStatus LoadStatus(SqlConnection connection, string procedure, bool sage)
    {
        var status = new SyncStatus();
        using var command = StoredProcedure(connection, procedure);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            status.RequestId = ReadNullableGuid(reader, "RequestId");
            status.Status = ReadString(reader, "Status", "NeverRun");
            status.Message = ReadString(reader, "Message");
            status.QueueDepth = ReadInt(reader, "QueueDepth");
            status.LastAttemptAtUtc = ReadNullableDateTime(reader, "RequestedAtUtc");
            status.LastSuccessfulAtUtc = ReadNullableDateTime(reader, "CompletedAtUtc");
            if (sage)
            {
                status.RequiresLargeRemovalConfirmation = ReadBool(reader, "RequiresLargeRemovalConfirmation");
                status.ExistingCount = ReadInt(reader, "ExistingCount");
                status.ReadCount = ReadInt(reader, "ReadCount");
                status.SavedCount = ReadInt(reader, "SavedCount");
                status.StaleCount = ReadInt(reader, "StaleCount");
            }
        }
        if (reader.NextResult() && reader.Read())
        {
            status.LastAttemptAtUtc = ReadNullableDateTime(reader, "LastAttemptAtUtc") ?? status.LastAttemptAtUtc;
            status.LastSuccessfulAtUtc = ReadNullableDateTime(reader, "LastSuccessfulAtUtc") ?? status.LastSuccessfulAtUtc;
            status.LastError = ReadString(reader, "LastError");
        }
        return status;
    }

    private static void LoadMappings(SqlConnection connection, SynchronizationConfiguration configuration)
    {
        using var command = StoredProcedure(connection, "tb_app.AdminGetWhdUserMappings");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var login = ReadString(reader, "LoginName");
            var display = ReadString(reader, "DisplayName");
            configuration.UserMappings.Add(new(
                login,
                string.IsNullOrWhiteSpace(display) ? login : display,
                ReadBool(reader, "IsAdmin"),
                ReadString(reader, "TechnicianExternalId")));
        }
    }

    private static void LoadTechnicians(SqlConnection connection, SynchronizationConfiguration configuration)
    {
        configuration.Technicians.Add(new(string.Empty, "No WHD technician (remove mapping)"));
        using var command = StoredProcedure(connection, "tb_app.AdminGetWhdTechnicians");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!ReadBool(reader, "IsActive", true)) continue;
            var id = ReadString(reader, "ExternalId");
            var display = ReadString(reader, "DisplayName", id);
            configuration.Technicians.Add(new(id, display, ReadString(reader, "Username")));
        }
    }

    private static void LoadAuthPointMappings(
        SqlConnection connection,
        SynchronizationConfiguration configuration)
    {
        try
        {
            using var command = StoredProcedure(
                connection,
                "tb_app.AdminGetAuthPointUserMappings");
            using var reader = command.ExecuteReader();
            var values = new Dictionary<string, (string Login, bool Enabled, byte[]? Version)>(
                StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var loginName = ReadString(reader, "LoginName");
                var versionOrdinal = TryOrdinal(reader, "RowVersion");
                var version = versionOrdinal >= 0 && !reader.IsDBNull(versionOrdinal)
                    ? (byte[])reader.GetValue(versionOrdinal)
                    : null;
                values[loginName] = (
                    ReadString(reader, "AuthPointLogin"),
                    ReadBool(reader, "IsEnabled"),
                    version);
            }

            for (var index = 0; index < configuration.UserMappings.Count; index++)
            {
                var mapping = configuration.UserMappings[index];
                if (values.TryGetValue(mapping.LoginName, out var authPoint))
                {
                    configuration.UserMappings[index] = mapping with
                    {
                        AuthPointLogin = authPoint.Login,
                        AuthPointEnabled = authPoint.Enabled,
                        AuthPointRowVersion = authPoint.Version
                    };
                }
            }
        }
        catch (SqlException exception) when (exception.Number == 2812)
        {
            // The stable Server Manager remains usable before the additive beta SQL is installed.
        }
    }

    private static SqlCommand StoredProcedure(SqlConnection connection, string name, SqlTransaction? transaction = null) => new(name, connection, transaction)
    {
        CommandType = CommandType.StoredProcedure,
        CommandTimeout = 30
    };

    private static int TryOrdinal(SqlDataReader reader, string name)
    {
        try { return reader.GetOrdinal(name); } catch (IndexOutOfRangeException) { return -1; }
    }

    private static string ReadString(SqlDataReader reader, string name, string fallback = "")
    {
        var ordinal = TryOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? fallback : Convert.ToString(reader.GetValue(ordinal)) ?? fallback;
    }
    private static int ReadInt(SqlDataReader reader, string name, int fallback = 0)
    {
        var ordinal = TryOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? fallback : Convert.ToInt32(reader.GetValue(ordinal));
    }
    private static bool ReadBool(SqlDataReader reader, string name, bool fallback = false)
    {
        var ordinal = TryOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? fallback : Convert.ToBoolean(reader.GetValue(ordinal));
    }
    private static DateTime? ReadNullableDateTime(SqlDataReader reader, string name)
    {
        var ordinal = TryOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? null : Convert.ToDateTime(reader.GetValue(ordinal));
    }
    private static Guid? ReadNullableGuid(SqlDataReader reader, string name)
    {
        var ordinal = TryOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? null : (Guid)reader.GetValue(ordinal);
    }
}
