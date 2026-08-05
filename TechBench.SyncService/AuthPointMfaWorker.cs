using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public sealed class AuthPointMfaWorker : BackgroundService
{
    private readonly SyncSqlRepository _repository;
    private readonly AuthPointSecretStore _secretStore;
    private readonly AuthPointApiClient _apiClient;
    private readonly SyncServiceOptions _options;
    private readonly ILogger<AuthPointMfaWorker> _logger;
    private readonly Guid _workerId = Guid.NewGuid();

    public AuthPointMfaWorker(
        SyncSqlRepository repository,
        AuthPointSecretStore secretStore,
        AuthPointApiClient apiClient,
        IOptions<SyncServiceOptions> options,
        ILogger<AuthPointMfaWorker> logger)
    {
        _repository = repository;
        _secretStore = secretStore;
        _apiClient = apiClient;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WatchGuard AuthPoint MFA worker {WorkerId} started.",
            _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var work = await _repository.ClaimAuthPointMfaChallengeAsync(
                        _workerId,
                        stoppingToken)
                    .ConfigureAwait(false);
                if (work is null)
                {
                    await Task.Delay(_options.MfaPollInterval, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await ProcessAsync(work, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The AuthPoint MFA work-queue poll failed.");
                try
                {
                    await Task.Delay(_options.MfaPollInterval, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessAsync(
        AuthPointMfaWork work,
        CancellationToken stoppingToken)
    {
        AuthPointMfaResult result;
        try
        {
            var configuration = await _repository.GetAuthPointConfigurationAsync(stoppingToken)
                .ConfigureAwait(false);
            var credentials = _secretStore.Read();
            result = await _apiClient.AuthenticatePushAsync(
                    configuration,
                    credentials,
                    work.ProviderLogin,
                    work.ClientMachine,
                    stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = new AuthPointMfaResult(
                AuthPointMfaResultKind.Error,
                "SERVER_CONFIGURATION_ERROR",
                "The TechBench server could not start AuthPoint authentication.");
        }

        await _repository.CompleteAuthPointMfaChallengeAsync(
                work,
                _workerId,
                result,
                stoppingToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "AuthPoint MFA challenge {ChallengeId} completed with result {Result} and code {Code}.",
            work.ChallengeId,
            result.Kind,
            result.Code);
    }
}
