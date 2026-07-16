using Microsoft.Data.SqlClient;
using TechBench.Data;

namespace TechBench.Tests;

public sealed class SqlServerConnectionOptionsTests
{
    [Fact]
    public void BuildsIntegratedSecurityConnectionString()
    {
        var options = new SqlServerConnectionOptions(
            @"SQL01\TECHBENCH",
            "TechBenchV2");

        var builder = new SqlConnectionStringBuilder(options.BuildConnectionString());

        Assert.Equal(@"SQL01\TECHBENCH", builder.DataSource);
        Assert.Equal("TechBenchV2", builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
        Assert.False(builder.PersistSecurityInfo);
        Assert.Empty(builder.UserID);
        Assert.Empty(builder.Password);
        Assert.Equal(
            SqlServerConnectionOptions.DefaultApplicationName,
            builder.ApplicationName);
        Assert.False(builder.MultipleActiveResultSets);
        Assert.NotEqual(
            "False",
            Convert.ToString(builder["Encrypt"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesExplicitTrustServerCertificateChoice(bool trustServerCertificate)
    {
        var options = new SqlServerConnectionOptions(
            "SQL01",
            "TechBenchV2",
            trustServerCertificate);

        var builder = new SqlConnectionStringBuilder(options.BuildConnectionString());

        Assert.Equal(trustServerCertificate, builder.TrustServerCertificate);
    }

    [Fact]
    public void NormalizesNamesAndPreservesTimeouts()
    {
        var options = new SqlServerConnectionOptions(
            "  SQL01  ",
            "  TechBenchV2  ")
        {
            ConnectTimeoutSeconds = 20,
            CommandTimeoutSeconds = 45
        };

        var normalized = options.NormalizeAndValidate();
        var builder = new SqlConnectionStringBuilder(normalized.BuildConnectionString());

        Assert.Equal("SQL01", normalized.Server);
        Assert.Equal("TechBenchV2", normalized.Database);
        Assert.Equal(20, builder.ConnectTimeout);
        Assert.Equal(45, normalized.CommandTimeoutSeconds);
    }

    [Theory]
    [InlineData("", "TechBenchV2")]
    [InlineData("   ", "TechBenchV2")]
    [InlineData("SQL01", "")]
    [InlineData("SQL01", "   ")]
    public void RejectsMissingServerOrDatabase(string server, string database)
    {
        var options = new SqlServerConnectionOptions(server, database);

        Assert.Throws<ArgumentException>(() => options.BuildConnectionString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void RejectsInvalidConnectionTimeout(int timeout)
    {
        var options = new SqlServerConnectionOptions("SQL01", "TechBenchV2")
        {
            ConnectTimeoutSeconds = timeout
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.BuildConnectionString());
    }

    [Fact]
    public void TreatsConnectionStringSyntaxInServerNameAsData()
    {
        const string serverName = "SQL01;User ID=attacker;Password=not-used";
        var options = new SqlServerConnectionOptions(serverName, "TechBenchV2");

        var builder = new SqlConnectionStringBuilder(options.BuildConnectionString());

        Assert.Equal(serverName, builder.DataSource);
        Assert.True(builder.IntegratedSecurity);
        Assert.Empty(builder.UserID);
        Assert.Empty(builder.Password);
    }
}
