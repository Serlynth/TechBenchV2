using TechBench.Models;

namespace TechBench.Services;

public static class ClientSearchMatcher
{
    public static bool Matches(Client client, string? searchTerm)
    {
        ArgumentNullException.ThrowIfNull(client);

        var query = searchTerm?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return Contains(client.Name, query)
            || Contains(client.WhdLocationName, query)
            || Contains(client.WhdContactName, query)
            || Contains(client.WhdContactEmail, query)
            || Contains(client.WhdPhone, query)
            || Contains(client.WhdAddress, query)
            || Contains(client.SageCustomerId, query)
            || Contains(client.SageCustomerName, query)
            || Contains(client.SageContactName, query);
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
