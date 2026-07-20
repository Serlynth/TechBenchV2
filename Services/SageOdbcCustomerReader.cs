using System.Data;
using System.Data.Odbc;
using System.Globalization;
using System.Text.RegularExpressions;
using TechBench.Models;

namespace TechBench.Services;

public sealed class SageOdbcCustomerReader
{
    private static readonly string[] CustomerIdColumns =
    [
        "CustomerID",
        "Customer_ID",
        "CustomerId"
    ];

    private static readonly string[] CustomerNameColumns =
    [
        "CustomerName",
        "Customer_Name",
        "Customer_Bill_Name",
        "BillToName",
        "Bill_To_Name",
        "CustomerLongName",
        "Customer_Name_1",
        "Name"
    ];

    private static readonly string[] ContactColumns =
    [
        "Contact",
        "ContactName",
        "Contact_Name"
    ];

    private static readonly string[] TelephoneColumns =
    [
        "Phone_Number",
        "Telephone",
        "Telephone1",
        "Phone",
        "PhoneNumber"
    ];

    private static readonly string[] InactiveColumns =
    [
        "CustomerIsInactive",
        "Customer_IsInactive",
        "Customer_Is_Inactive",
        "CustomerInactive",
        "Customer_Inactive",
        "Inactive",
        "IsInactive",
        "Is_Inactive",
        "InActive"
    ];

    public IReadOnlyList<SageCustomer> ReadCustomers(
        string dsn,
        string username,
        string password,
        int maxRows = 0,
        bool includeInactive = false,
        bool preserveInvalidRows = false)
    {
        if (string.IsNullOrWhiteSpace(dsn))
        {
            throw new InvalidOperationException("Enter the Sage ODBC DSN before syncing customers.");
        }

        using var connection = new OdbcConnection(BuildConnectionString(dsn, username, password));
        connection.Open();

        var columns = GetCustomerColumns(connection);
        var customerIdColumn = FindColumn(columns, CustomerIdColumns)
            ?? throw new InvalidOperationException("The Sage Customers table was found, but no CustomerID column was found.");
        var customerNameColumn = FindColumn(columns, CustomerNameColumns)
            ?? FindNameLikeColumn(columns)
            ?? customerIdColumn;
        var contactColumn = FindColumn(columns, ContactColumns);
        var telephoneColumn = FindColumn(columns, TelephoneColumns);
        var inactiveColumn = FindColumn(columns, InactiveColumns) ?? FindInactiveLikeColumn(columns);

        var selectedColumns = new[]
            {
                customerIdColumn,
                customerNameColumn,
                contactColumn,
                telephoneColumn,
                inactiveColumn
            }
            .Where(static column => !string.IsNullOrWhiteSpace(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = $"SELECT {string.Join(", ", selectedColumns)} FROM Customers ORDER BY {customerIdColumn}";

        var customers = new List<SageCustomer>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var customerId = ReadText(reader, customerIdColumn);
            if (!preserveInvalidRows && string.IsNullOrWhiteSpace(customerId))
            {
                continue;
            }

            var customerName = ReadText(reader, customerNameColumn);
            var contactName = ReadText(reader, contactColumn);
            var telephone = ReadText(reader, telephoneColumn);
            var isActive = !ReadInactiveFlag(reader, inactiveColumn);
            if (!includeInactive && !isActive)
            {
                continue;
            }

            customers.Add(new SageCustomer
            {
                CustomerId = customerId.Trim(),
                CustomerName = preserveInvalidRows
                    ? customerName.Trim()
                    : string.IsNullOrWhiteSpace(customerName) ? customerId.Trim() : customerName.Trim(),
                ContactName = string.IsNullOrWhiteSpace(contactName) ? null : contactName.Trim(),
                Telephone = string.IsNullOrWhiteSpace(telephone) ? null : telephone.Trim(),
                IsActive = isActive
            });

            if (maxRows > 0 && customers.Count >= maxRows)
            {
                break;
            }
        }

        return customers;
    }

    private static string BuildConnectionString(string dsn, string username, string password)
    {
        var builder = new OdbcConnectionStringBuilder
        {
            ["DSN"] = dsn.Trim()
        };

        if (!string.IsNullOrWhiteSpace(username))
        {
            builder["UID"] = username.Trim();
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder["PWD"] = password;
        }

        return builder.ConnectionString;
    }

    private static IReadOnlyList<string> GetCustomerColumns(OdbcConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Customers";

        using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly);
        var columns = new List<string>();
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var columnName = reader.GetName(index);
            if (IsSafeIdentifier(columnName))
            {
                columns.Add(columnName);
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException("The Sage Customers table was found, but TechBench could not read its columns.");
        }

        return columns;
    }

    private static string? FindColumn(IReadOnlyCollection<string> columns, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = columns.FirstOrDefault(column => column.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? FindNameLikeColumn(IReadOnlyCollection<string> columns)
    {
        return columns.FirstOrDefault(column =>
            column.Contains("Name", StringComparison.OrdinalIgnoreCase)
            && !column.Contains("Contact", StringComparison.OrdinalIgnoreCase)
            && !column.Contains("User", StringComparison.OrdinalIgnoreCase)
            && !column.Contains("Sales", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindInactiveLikeColumn(IReadOnlyCollection<string> columns)
    {
        return columns.FirstOrDefault(column =>
            column.Contains("Inactive", StringComparison.OrdinalIgnoreCase)
            || column.Contains("In_Active", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadText(IDataRecord reader, string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return string.Empty;
        }

        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool ReadInactiveFlag(IDataRecord reader, string? columnName)
    {
        var value = ReadText(reader, columnName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("-1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeIdentifier(string value)
    {
        return Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$");
    }
}
