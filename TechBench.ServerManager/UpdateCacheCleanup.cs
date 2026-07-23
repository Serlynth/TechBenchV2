namespace TechBench.ServerManager;

internal readonly record struct UpdateCacheCleanupResult(
    int RemovedOperations,
    long ReclaimedBytes);

internal static class UpdateCacheCleanup
{
    private static readonly string[] CacheDirectoryNames = ["updates", "setup"];

    public static async Task<UpdateCacheCleanupResult> CleanupAfterStartupAsync(
        AppPaths paths,
        CancellationToken cancellationToken = default)
    {
        // The previous update helper may still be exiting from the downloaded
        // package. Give Windows time to release that executable, then retry.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var total = new UpdateCacheCleanupResult();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pass = await Task.Run(() => CleanupNow(paths), cancellationToken);
            total = new UpdateCacheCleanupResult(
                total.RemovedOperations + pass.RemovedOperations,
                total.ReclaimedBytes + pass.ReclaimedBytes);
            if (!HasCachedOperations(paths))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return total;
    }

    internal static UpdateCacheCleanupResult CleanupNow(AppPaths paths)
    {
        var removed = 0;
        long reclaimed = 0;
        foreach (var directoryName in CacheDirectoryNames)
        {
            var cacheRoot = Path.Combine(paths.ManagerDataDirectory, directoryName);
            if (!Directory.Exists(cacheRoot))
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         cacheRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var bytes = MeasureBytes(entry);
                if (!TryDelete(entry))
                {
                    continue;
                }

                removed++;
                reclaimed += bytes;
            }
        }

        return new UpdateCacheCleanupResult(removed, reclaimed);
    }

    internal static void CleanupFailedOperation(AppPaths paths, string operationRoot)
    {
        var updatesRoot = Path.GetFullPath(
            Path.Combine(paths.ManagerDataDirectory, "updates"))
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(operationRoot);
        if (!candidate.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The failed update operation is outside the protected update cache.");
        }

        _ = TryDelete(candidate);
    }

    private static bool HasCachedOperations(AppPaths paths)
    {
        foreach (var directoryName in CacheDirectoryNames)
        {
            var cacheRoot = Path.Combine(paths.ManagerDataDirectory, directoryName);
            try
            {
                if (Directory.Exists(cacheRoot)
                    && Directory.EnumerateFileSystemEntries(cacheRoot).Any())
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static long MeasureBytes(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return 0;
            }

            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                        return 0L;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return 0L;
                    }
                });
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    var attributes = File.GetAttributes(path);
                    Directory.Delete(
                        path,
                        recursive: !attributes.HasFlag(FileAttributes.ReparsePoint));
                }

                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(250 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(250 * (attempt + 1));
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }
}
