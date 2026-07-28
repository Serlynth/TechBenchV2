using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using TechBench.Models;

namespace TechBench.Services;

public sealed record EquipmentBuildSheetImport(
    string Customer,
    string Machine,
    string MachineName,
    string SerialNumber,
    string EndUser,
    string EmailAddress,
    string PartNumber,
    string Model,
    string DeviceType,
    string SourceFileName);

public sealed class EquipmentBuildSheetImporter
{
    private static readonly HashSet<string> LegalOrganizationSuffixes =
    [
        "CO", "COMPANY", "CORP", "CORPORATION", "INC", "INCORPORATED",
        "LLC", "LLP", "LTD", "LIMITED", "PC", "PLLC"
    ];

    private static readonly Regex FieldPattern = new(
        @"^\s*(?<label>customer(?:\s+name)?|machine\s+name|machine|s\s*/?\s*n|serial\s+number|end\s+user|email\s+address)\s*(?:(?:[:\-–—])\s*(?<value>.*))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    private static readonly Regex MachineSeparatorPattern = new(
        @"\s+[–—-]\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public EquipmentBuildSheetImport Read(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new FileStream(
            fileName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var rows = new List<IReadOnlyList<string>>();
        do
        {
            while (reader.Read())
            {
                var row = new string[reader.FieldCount];
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    row[column] = FormatCell(reader.GetValue(column));
                }

                rows.Add(row);
            }
        }
        while (reader.NextResult());

        return ParseRows(rows, Path.GetFileName(fileName));
    }

    internal static EquipmentBuildSheetImport ParseRows(
        IEnumerable<IReadOnlyList<string>> rows,
        string sourceFileName = "build-sheet.xlsx")
    {
        ArgumentNullException.ThrowIfNull(rows);

        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Count; column++)
            {
                var cell = Clean(row[column]);
                if (cell.Length == 0)
                {
                    continue;
                }

                var match = FieldPattern.Match(cell);
                if (!match.Success)
                {
                    continue;
                }

                var key = NormalizeLabel(match.Groups["label"].Value);
                var value = Clean(match.Groups["value"].Value);
                if (value.Length == 0)
                {
                    value = FindNextValue(row, column + 1);
                }

                if (value.Length > 0)
                {
                    AddField(values, key, value);
                }
            }
        }

        if (values.Count == 0)
        {
            throw new InvalidDataException(
                "No Customer, Machine, Machine Name, S/N, End User, or Email Address fields were found.");
        }

        var customer = Get(values, "customer");
        var machine = Get(values, "machine");
        var machineName = Get(values, "machinename");
        var serialNumber = Get(values, "serialnumber");
        var endUser = Get(values, "enduser");
        var emailAddress = Get(values, "emailaddress");
        var (partNumber, model) = SplitMachine(machine);
        var deviceType = InferDeviceType(machine);

        return new EquipmentBuildSheetImport(
            customer,
            machine,
            machineName,
            serialNumber,
            endUser,
            emailAddress,
            partNumber,
            model,
            deviceType,
            Clean(sourceFileName));
    }

    internal static InventoryClient? FindClient(
        string customer,
        IEnumerable<InventoryClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var normalizedCustomer = NormalizeIdentity(customer);
        if (normalizedCustomer.Length == 0)
        {
            return null;
        }

        var matches = clients
            .Where(client =>
                NormalizeIdentity(client.Name) == normalizedCustomer)
            .Take(2)
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            return null;
        }

        var organization = NormalizeOrganization(customer);
        var organizationMatches = clients
            .Where(client =>
                NormalizeOrganization(client.Name) == organization)
            .Take(2)
            .ToList();
        return organization.Length > 0 && organizationMatches.Count == 1
            ? organizationMatches[0]
            : null;
    }

    internal static InventoryClientUser? FindClientUser(
        EquipmentBuildSheetImport import,
        InventoryClient? client)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (client is null)
        {
            return null;
        }

        var email = import.EmailAddress.Trim();
        if (email.Length > 0)
        {
            var emailMatches = client.Users
                .Where(static user => user.IsActive)
                .Where(user => string.Equals(
                    user.Email.Trim(),
                    email,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            if (emailMatches.Count == 1)
            {
                return emailMatches[0];
            }
        }

        var normalizedName = NormalizeIdentity(import.EndUser);
        if (normalizedName.Length == 0)
        {
            return null;
        }

        var nameMatches = client.Users
            .Where(static user => user.IsActive)
            .Where(user =>
                NormalizeIdentity(user.DisplayName) == normalizedName)
            .Take(2)
            .ToList();
        return nameMatches.Count == 1 ? nameMatches[0] : null;
    }

    private static string FormatCell(object? value) =>
        value switch
        {
            null => string.Empty,
            DateTime date => date.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(
                null,
                CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };

    private static void AddField(
        IDictionary<string, string> values,
        string key,
        string value)
    {
        if (values.TryGetValue(key, out var existing)
            && !string.Equals(
                Clean(existing),
                Clean(value),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The workbook contains conflicting values for {DisplayLabel(key)}.");
        }

        values[key] = value;
    }

    private static string FindNextValue(
        IReadOnlyList<string> row,
        int startColumn)
    {
        for (var column = startColumn; column < row.Count; column++)
        {
            var candidate = Clean(row[column]);
            if (candidate.Length == 0)
            {
                continue;
            }

            return FieldPattern.IsMatch(candidate)
                ? string.Empty
                : candidate;
        }

        return string.Empty;
    }

    private static (string PartNumber, string Model) SplitMachine(
        string machine)
    {
        if (string.IsNullOrWhiteSpace(machine))
        {
            return (string.Empty, string.Empty);
        }

        var parts = MachineSeparatorPattern.Split(machine, 2);
        return parts.Length == 2
            ? (Clean(parts[0]), Clean(parts[1]))
            : (string.Empty, Clean(machine));
    }

    private static string InferDeviceType(string machine)
    {
        var normalized = machine.ToLowerInvariant();
        var laptopTerms = new[]
        {
            "elitebook",
            "probook",
            "latitude",
            "thinkpad",
            "laptop",
            "notebook",
            "chromebook",
            "macbook"
        };
        return laptopTerms.Any(normalized.Contains)
            ? "Laptop"
            : "Desktop";
    }

    private static string NormalizeLabel(string value)
    {
        var normalized = NormalizeIdentity(value);
        return normalized switch
        {
            "customer" or "customername" => "customer",
            "machinename" => "machinename",
            "machine" => "machine",
            "sn" or "serial" or "serialnumber" => "serialnumber",
            "enduser" => "enduser",
            "emailaddress" => "emailaddress",
            _ => normalized
        };
    }

    private static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }

    private static string NormalizeOrganization(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToUpperInvariant())
        {
            if (character is '\'' or '\u2019')
            {
                continue;
            }

            if (character == '&')
            {
                builder.Append(" AND ");
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        var words = builder.ToString()
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .ToList();
        if (words.Count > 1 && words[0] == "THE")
        {
            words.RemoveAt(0);
        }

        while (words.Count > 0
               && LegalOrganizationSuffixes.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }

    private static string DisplayLabel(string key) =>
        key switch
        {
            "customer" => "Customer",
            "machine" => "Machine",
            "machinename" => "Machine Name",
            "serialnumber" => "S/N",
            "enduser" => "End User",
            "emailaddress" => "Email Address",
            _ => key
        };

    private static string Get(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim(), @"\s+", " ");
}
