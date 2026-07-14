namespace TechBench.Models;

public sealed class SageCustomer
{
    public string CustomerId { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string? ContactName { get; init; }
    public string? Telephone { get; init; }
    public bool IsActive { get; init; } = true;
}
