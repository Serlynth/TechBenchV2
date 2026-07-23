using System.Reflection;
using TechBench.Data;
using TechBench.Providers;

namespace TechBench.Tests;

public sealed class SqlServerTechBenchRepositoryContractTests
{
    [Fact]
    public void ClientTargetsV0010Schema()
    {
        Assert.Equal(10, SqlServerConnectionFactory.SupportedSchemaVersion);
    }

    [Fact]
    public void ImplementsDropInRepositoryContract()
    {
        Assert.True(
            typeof(ITechBenchRepository)
                .IsAssignableFrom(typeof(SqlServerTechBenchRepository)));
    }

    [Fact]
    public void ConstructorDoesNotOpenConnectionAndPreservesExplicitDeviceId()
    {
        var options = new SqlServerConnectionOptions("SQL01", "TechBenchV2");
        var factory = new SqlServerConnectionFactory(options);
        var deviceId = Guid.NewGuid();

        var repository = new SqlServerTechBenchRepository(factory, deviceId);

        Assert.Equal(deviceId, repository.DeviceId);
        Assert.Contains("SQL01", repository.DatabasePath, StringComparison.Ordinal);
        Assert.Contains("TechBenchV2", repository.DatabasePath, StringComparison.Ordinal);
        Assert.False(repository.FullTextSearchAvailable);
    }

    [Fact]
    public void StoredProcedureContractUsesOnlyQualifiedApplicationProcedures()
    {
        var procedureFields = typeof(SqlServerTechBenchRepository.Procedures)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToArray();

        Assert.NotEmpty(procedureFields);
        foreach (var field in procedureFields)
        {
            var procedure = Assert.IsType<string>(field.GetRawConstantValue());
            Assert.StartsWith("[tb_app].[", procedure, StringComparison.Ordinal);
            Assert.EndsWith("]", procedure, StringComparison.Ordinal);
        }

        Assert.Equal(
            procedureFields.Length,
            procedureFields
                .Select(field => (string)field.GetRawConstantValue()!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void PublicAsyncRepositoryOperationsAcceptCancellation()
    {
        var asyncMethods = typeof(SqlServerTechBenchRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method =>
                method.DeclaringType == typeof(SqlServerTechBenchRepository)
                && method.Name.EndsWith("Async", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(asyncMethods);
        foreach (var method in asyncMethods)
        {
            Assert.Contains(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(CancellationToken));
        }
    }

    [Fact]
    public void PostingCompletionCarriesExplicitMarkPostedDecision()
    {
        var method = typeof(SqlServerTechBenchRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate =>
                candidate.Name == nameof(
                    SqlServerTechBenchRepository.CompletePostingAttemptAsync));

        var markPosted = Assert.Single(
            method.GetParameters(),
            parameter => parameter.Name == "markPosted");
        Assert.Equal(typeof(bool), markPosted.ParameterType);
        Assert.True(markPosted.HasDefaultValue);
        Assert.Equal(true, markPosted.DefaultValue);
    }

    [Fact]
    public void ClientSearchProviderUsesRepositoryRowVersionTracker()
    {
        var constructor = Assert.Single(
            typeof(SqlServerClientProvider).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());

        Assert.Equal(
            typeof(SqlServerTechBenchRepository),
            parameter.ParameterType);
    }

    [Fact]
    public void SharedPersistenceContractUsesOrganizationScopeAndAdminProcedures()
    {
        Assert.Equal("Organization", SqlServerTechBenchRepository.OrganizationScope);
        Assert.Equal(
            "[tb_app].[AdminSaveOrganizationSetting]",
            SqlServerTechBenchRepository.Procedures.SaveOrganizationSetting);
        Assert.Equal(
            "[tb_app].[AdminDeleteOrganizationSetting]",
            SqlServerTechBenchRepository.Procedures.DeleteOrganizationSetting);

        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.SaveOrganizationSetting)));
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.DeleteOrganizationSetting)));
        Assert.Equal("Organization", new TechBench.Models.NoteTemplate().ScopeType);
        Assert.Equal("Organization", new TechBench.Models.CommonLink().ScopeType);
        Assert.Equal(
            "[tb_app].[AdminGetOrganizationTags]",
            SqlServerTechBenchRepository.Procedures.GetOrganizationTags);
        Assert.Equal(
            "[tb_app].[AdminSaveOrganizationTag]",
            SqlServerTechBenchRepository.Procedures.SaveOrganizationTag);
        Assert.Equal(
            "[tb_app].[AdminDeleteOrganizationTag]",
            SqlServerTechBenchRepository.Procedures.DeleteOrganizationTag);
    }

    [Fact]
    public void WhdServerSyncUsesOnlyTheAdminQueueStatusAndMappingProcedures()
    {
        Assert.Equal("[tb_app].[AdminRequestWhdSync]", SqlServerTechBenchRepository.Procedures.RequestWhdSync);
        Assert.Equal("[tb_app].[GetWhdSyncStatus]", SqlServerTechBenchRepository.Procedures.GetWhdSyncStatus);
        Assert.Equal("[tb_app].[AdminGetWhdUserMappings]", SqlServerTechBenchRepository.Procedures.GetWhdUserMappings);
        Assert.Equal("[tb_app].[AdminSaveWhdUserMapping]", SqlServerTechBenchRepository.Procedures.SaveWhdUserMapping);
        Assert.Equal("[tb_app].[AdminGetWhdTechnicians]", SqlServerTechBenchRepository.Procedures.GetWhdTechnicians);
    }

    [Fact]
    public void SageCustomerSyncUsesOnlyTheAdminServerQueueContract()
    {
        Assert.Equal(
            "[tb_app].[AdminRequestSageSync]",
            SqlServerTechBenchRepository.Procedures.RequestSageSync);
        Assert.Equal(
            "[tb_app].[GetSageSyncStatus]",
            SqlServerTechBenchRepository.Procedures.GetSageSyncStatus);
        var requestMethod = typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.RequestSageSync));
        Assert.NotNull(requestMethod);
        var requestParameters = requestMethod!.GetParameters();
        Assert.Equal(2, requestParameters.Length);
        Assert.Equal(typeof(bool), requestParameters[0].ParameterType);
        Assert.Equal(typeof(Guid?), requestParameters[1].ParameterType);
        Assert.NotNull(typeof(ITechBenchRepository).GetMethod(
            nameof(ITechBenchRepository.GetSageSyncStatus)));

        var repositorySource = File.ReadAllText(FindRepositoryFile(
            "Data",
            "SqlServerTechBenchRepository.WhdSync.cs"));
        Assert.Contains(
            "alreadyQueued && !approvalNotQueued",
            repositorySource,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TechBenchV2 repository root.");
    }
}
