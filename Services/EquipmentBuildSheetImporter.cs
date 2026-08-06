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
    string SourceFileName,
    string AssetTag = "",
    string Manufacturer = "",
    string IpAddress = "",
    string AnyDeskNumber = "",
    string AnyDeskPassword = "");

public sealed class EquipmentBuildSheetImporter
{
    private static readonly HashSet<string> LegalOrganizationSuffixes =
    [
        "CO", "COMPANY", "CORP", "CORPORATION", "INC", "INCORPORATED",
        "LLC", "LLP", "LTD", "LIMITED", "PC", "PLLC"
    ];

    private static readonly HashSet<string> OrganizationConnectorWords =
    [
        "AND"
    ];

    private static readonly HashSet<string> PersonNameNoiseWords =
    [
        "DR", "JR", "MISS", "MR", "MRS", "MS", "SR", "II", "III", "IV"
    ];

    private static readonly Regex FieldPattern = new(
        @"^\s*(?<label>customer\s*/\s*client|customer(?:\s+name)?|client(?:\s+name)?|machine\s+name|pc\s+name|machine|device\s+type|manufacturer|model|part\s+number|asset\s+tag|ip(?:\s+address)?|anydesk\s+(?:number|id|address|password)|s\s*/?\s*n|serial\s+number|end\s+user|user|email(?:\s+address)?)\s*(?:(?:[:\-–—])\s*(?<value>.*))?\s*$",
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

        var materializedRows = rows.ToList();
        var isCurrentInventoryTemplate = materializedRows.Any(
            static row => row.Any(
                static cell => string.Equals(
                    Clean(cell),
                    "TechBench Inventory Build Sheet",
                    StringComparison.OrdinalIgnoreCase)));
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in materializedRows)
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
                    value = isCurrentInventoryTemplate
                        ? FindImmediateValue(row, column + 1)
                        : FindNextValue(row, column + 1);
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
                "No supported TechBench Inventory or PC Configuration fields were found.");
        }

        var customer = Get(values, "customer");
        var machine = Get(values, "machine");
        var machineName = Get(values, "machinename");
        var serialNumber = Get(values, "serialnumber");
        var endUser = Get(values, "enduser");
        var emailAddress = Get(values, "emailaddress");
        var (legacyPartNumber, legacyModel) = SplitMachine(machine);
        var partNumber = FirstValue(Get(values, "partnumber"), legacyPartNumber);
        var model = FirstValue(Get(values, "model"), legacyModel);
        var deviceType = NormalizeDeviceType(
            Get(values, "devicetype"),
            $"{machine} {model}");

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
            Clean(sourceFileName),
            Get(values, "assettag"),
            Get(values, "manufacturer"),
            Get(values, "ipaddress"),
            Get(values, "anydesknumber"),
            Get(values, "anydeskpassword"));
    }

    internal static InventoryClient? FindClient(
        string customer,
        IEnumerable<InventoryClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var clientList = clients.ToList();
        var normalizedCustomer = NormalizeIdentity(customer);
        if (normalizedCustomer.Length == 0)
        {
            return null;
        }

        var matches = clientList
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
        var organizationMatches = clientList
            .Where(client =>
                NormalizeOrganization(client.Name) == organization)
            .Take(2)
            .ToList();
        return organization.Length > 0 && organizationMatches.Count == 1
            ? organizationMatches[0]
            : FindFuzzyClient(customer, clientList);
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

        var activeUsers = client.Users
            .Where(static user => user.IsActive)
            .ToList();
        var email = import.EmailAddress.Trim();
        if (email.Length > 0)
        {
            var emailMatches = activeUsers
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

        var nameMatches = activeUsers
            .Where(user =>
                NormalizeIdentity(user.DisplayName) == normalizedName)
            .Take(2)
            .ToList();
        if (nameMatches.Count == 1)
        {
            return nameMatches[0];
        }

        return nameMatches.Count > 1
            ? null
            : FindFuzzyClientUser(import.EndUser, activeUsers);
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

    private static string FindImmediateValue(
        IReadOnlyList<string> row,
        int valueColumn)
    {
        if (valueColumn >= row.Count)
        {
            return string.Empty;
        }

        var candidate = Clean(row[valueColumn]);
        return FieldPattern.IsMatch(candidate)
            ? string.Empty
            : candidate;
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

    private static string NormalizeDeviceType(
        string value,
        string inferenceSource)
    {
        var normalized = NormalizeIdentity(value);
        return normalized switch
        {
            "desktop" or "pc" or "workstation" => "Desktop",
            "laptop" or "notebook" => "Laptop",
            "server" => "Server",
            "switch" => "Switch",
            "firewall" => "Firewall",
            "accesspoint" or "ap" => "Access Point",
            "printer" or "mfp" => "Printer",
            "ups" => "UPS",
            "phone" => "Phone",
            "other" => "Other",
            _ => InferDeviceType(inferenceSource)
        };
    }

    private static string NormalizeLabel(string value)
    {
        var normalized = NormalizeIdentity(value);
        return normalized switch
        {
            "customer" or "customername" or "customerclient"
                or "client" or "clientname" => "customer",
            "machinename" or "pcname" => "machinename",
            "machine" => "machine",
            "devicetype" => "devicetype",
            "manufacturer" => "manufacturer",
            "model" => "model",
            "partnumber" => "partnumber",
            "assettag" => "assettag",
            "ip" or "ipaddress" => "ipaddress",
            "anydesknumber" or "anydeskid" or "anydeskaddress"
                => "anydesknumber",
            "anydeskpassword" => "anydeskpassword",
            "sn" or "serial" or "serialnumber" => "serialnumber",
            "enduser" or "user" => "enduser",
            "email" or "emailaddress" => "emailaddress",
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
        return string.Join(' ', GetOrganizationWords(value));
    }

    private static IReadOnlyList<string> GetOrganizationWords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
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

        words.RemoveAll(OrganizationConnectorWords.Contains);
        return words;
    }

    private static InventoryClient? FindFuzzyClient(
        string customer,
        IEnumerable<InventoryClient> clients)
    {
        var customerWords = GetOrganizationWords(customer)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (customerWords.Count < 2)
        {
            return null;
        }

        var candidates = clients
            .Select(client =>
            {
                var clientWords = GetOrganizationWords(client.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var sharedWordCount = customerWords.Count(
                    clientWords.Contains);
                var customerCoverage =
                    (double)sharedWordCount / customerWords.Count;
                var clientCoverage = clientWords.Count == 0
                    ? 0d
                    : (double)sharedWordCount / clientWords.Count;
                return new
                {
                    Client = client,
                    SharedWordCount = sharedWordCount,
                    CustomerCoverage = customerCoverage,
                    Score = (customerCoverage * 0.70d)
                        + (clientCoverage * 0.30d)
                };
            })
            .Where(candidate =>
                candidate.SharedWordCount >= 2
                && candidate.CustomerCoverage >= 0.75d
                && candidate.Score >= 0.78d)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.SharedWordCount)
            .Take(2)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.Count == 1
               || candidates[0].Score - candidates[1].Score >= 0.05d
            ? candidates[0].Client
            : null;
    }

    private static InventoryClientUser? FindFuzzyClientUser(
        string endUser,
        IEnumerable<InventoryClientUser> users)
    {
        var importedWords = GetPersonNameWords(endUser)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (importedWords.Count < 2)
        {
            return null;
        }

        var candidates = users
            .Select(user =>
            {
                var userWords = GetPersonNameWords(user.DisplayName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var sharedWordCount = importedWords.Count(
                    userWords.Contains);
                var importedCoverage =
                    (double)sharedWordCount / importedWords.Count;
                var userCoverage = userWords.Count == 0
                    ? 0d
                    : (double)sharedWordCount / userWords.Count;
                return new
                {
                    User = user,
                    SharedWordCount = sharedWordCount,
                    ImportedCoverage = importedCoverage,
                    Score = (importedCoverage * 0.70d)
                        + (userCoverage * 0.30d)
                };
            })
            .Where(candidate =>
                candidate.SharedWordCount >= 2
                && candidate.ImportedCoverage >= 0.65d
                && candidate.Score >= 0.75d)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.SharedWordCount)
            .Take(2)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.Count == 1
               || candidates[0].Score - candidates[1].Score >= 0.05d
            ? candidates[0].User
            : null;
    }

    private static IReadOnlyList<string> GetPersonNameWords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToUpperInvariant())
        {
            if (character is '\'' or '\u2019')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return builder.ToString()
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(word => !PersonNameNoiseWords.Contains(word))
            .ToList();
    }

    private static string DisplayLabel(string key) =>
        key switch
        {
            "customer" => "Customer",
            "machine" => "Machine",
            "machinename" => "Machine Name",
            "devicetype" => "Device Type",
            "manufacturer" => "Manufacturer",
            "model" => "Model",
            "partnumber" => "Part Number",
            "assettag" => "Asset Tag",
            "ipaddress" => "IP Address",
            "anydesknumber" => "AnyDesk Number",
            "anydeskpassword" => "AnyDesk Password",
            "serialnumber" => "S/N",
            "enduser" => "End User",
            "emailaddress" => "Email Address",
            _ => key
        };

    private static string Get(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string FirstValue(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
        ?? string.Empty;

    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim(), @"\s+", " ");
}
