using System.IO.Compression;
using System.Security;
using TechBench.SyncService;

namespace TechBench.Tests;

public sealed class CredentialsWorkbookHeaderTests
{
    [Theory]
    [InlineData("*if enabled -Firebox-DB\\csri")]
    [InlineData("*if enabled-Firebox-DB\\csri")]
    [InlineData("  *IF   ENABLED   -Firebox-DB\\csri  ")]
    public void FireboxDatabaseHeaderAcceptsCurrentAndLegacySpellings(string header)
    {
        Assert.True(FireDrillSyncEngine.IsExpectedHeader(5, header));
    }

    [Fact]
    public void ActualWorkbookHeadersAreAcceptedInOrder()
    {
        string[] headers =
        [
            "Client", "Firebox IP", "Status", "Admin", "csriadmin",
            "*if enabled -Firebox-DB\\csri", "Authpoint User", "sslvpnpassword",
            "AD Auth User", "AD Password", "RustPW"
        ];

        Assert.All(headers.Select((header, index) => (header, index)),
            item => Assert.True(FireDrillSyncEngine.IsExpectedHeader(item.index, item.header)));
    }

    [Fact]
    public void WrongColumnHeaderIsRejected()
    {
        Assert.False(FireDrillSyncEngine.IsExpectedHeader(5, "Firebox database password"));
    }

    [Theory]
    [InlineData("  New   Password  ", "new password")]
    [InlineData("Hosted DNS Account", "hosted dns account")]
    [InlineData("SSLVPNPASSWORD", "sslvpnpassword")]
    public void FlexibleHeadersHaveStableCaseInsensitiveKeys(string header, string expected)
    {
        Assert.Equal(expected, FireDrillSyncEngine.NormalizeFieldKey(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RowWithoutClientIsSkippedEvenWhenOtherCellsContainData(string? client)
    {
        Assert.True(FireDrillSyncEngine.ShouldSkipRow(client));
    }

    [Fact]
    public void RowWithClientIsImported()
    {
        Assert.False(FireDrillSyncEngine.ShouldSkipRow("Example Client"));
    }

    [Fact]
    public void OptionalClientUsersWorksheetGroupsOnePersonWithMultipleEncryptedAccounts()
    {
        var workbook = FireDrillSyncEngine.ReadWorkbookContents(
            CreateClientUsersWorkbook(),
            string.Empty);

        var credentials = Assert.Single(workbook.Credentials);
        Assert.Equal("Example Client", credentials.ClientName);

        var person = Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<CredentialsClientUserRow>>(workbook.ClientUsers));
        Assert.Equal("Example Client", person.ClientName);
        Assert.Equal("Dana Brooks", person.DisplayName);
        Assert.Equal("Accounting", person.RoleDepartment);
        Assert.Equal("dana@example.test", person.Email);
        Assert.Equal("Main office", person.LocationName);
        Assert.True(person.IsActive);
        Assert.StartsWith("CU-", person.SourceKey, StringComparison.Ordinal);

        Assert.Collection(
            person.Accounts,
            account =>
            {
                Assert.Equal("Microsoft 365", account.AccountSystem);
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "password"
                             && field.Value == "M365-secret");
            },
            account =>
            {
                Assert.Equal("VPN", account.AccountSystem);
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "password"
                             && field.Value == "VPN-secret");
            });
        Assert.All(
            person.Accounts,
            account => Assert.StartsWith(
                "CA-",
                account.SourceKey,
                StringComparison.Ordinal));
    }

    [Fact]
    public void RedesignedClientUsersWorksheetDiscoversGroupedColumnsDynamically()
    {
        var workbook = FireDrillSyncEngine.ReadWorkbookContents(
            CreateClientUsersWorkbook(
                [
                    "PERSON", "", "", "", "", "",
                    "ACTIVE DIRECTORY", "",
                    "MICROSOFT 365", "",
                    "VPN", "",
                    "SECURITY & NOTES", "",
                    "BACKUP PORTAL", ""
                ],
                [
                    "Client",
                    "Location / Site",
                    "User / Contact",
                    "Role / Department",
                    "Account Status",
                    "Email Address",
                    "AD Username",
                    "AD Password",
                    "Microsoft 365 Username",
                    "Microsoft 365 Password",
                    "VPN Username",
                    "VPN Password",
                    "Notes",
                    "Last Verified",
                    "Login URL",
                    "Password"
                ],
                [
                    "Example Client",
                    "Main office",
                    "Dana Brooks",
                    "Accounting",
                    "Active",
                    "dana@example.test",
                    "EXAMPLE\\dbrooks",
                    "AD-secret",
                    "dana@example.test",
                    "M365-secret",
                    "dbrooks",
                    "VPN-secret",
                    "Primary contact",
                    "2026-07-27",
                    "https://backup.example.test",
                    "Backup-secret"
                ]),
            string.Empty);

        var person = Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<CredentialsClientUserRow>>(workbook.ClientUsers));
        Assert.Equal("dana@example.test", person.Email);
        Assert.Collection(
            person.Accounts,
            account =>
            {
                Assert.Equal("ACTIVE DIRECTORY", account.AccountSystem);
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "ad password"
                             && field.Value == "AD-secret");
            },
            account =>
            {
                Assert.Equal("BACKUP PORTAL", account.AccountSystem);
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "login url"
                             && field.Value == "https://backup.example.test");
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "password"
                             && field.Value == "Backup-secret");
            },
            account =>
            {
                Assert.Equal("MICROSOFT 365", account.AccountSystem);
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "microsoft 365 password"
                             && field.Value == "M365-secret");
            },
            account =>
            {
                Assert.Equal("SECURITY & NOTES", account.AccountSystem);
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "notes"
                             && field.Value == "Primary contact");
                Assert.DoesNotContain(
                    account.Fields,
                    field => field.FieldKey == "pin");
            },
            account =>
            {
                Assert.Equal("VPN", account.AccountSystem);
                Assert.Contains(
                    account.Fields,
                    field => field.FieldKey == "vpn password"
                             && field.Value == "VPN-secret");
            });
    }

    [Fact]
    public void ClientUsersWorksheetAcceptsUserHeaderAndDiscoversEveryOtherColumnDynamically()
    {
        var workbook = FireDrillSyncEngine.ReadWorkbookContents(
            CreateClientUsersWorkbook(
                [
                    "Client",
                    "Location / Site",
                    "User",
                    "Role / Department",
                    "Account Status",
                    "AD Username",
                    "Email Address",
                    "365",
                    "Password",
                    "PIN",
                    "MFA / Recovery",
                    "Notes",
                    "Last Verified",
                    "New Portal Field"
                ],
                [
                    "CSRI",
                    "",
                    "Ryan Skoog",
                    "IT",
                    "Active",
                    "rskoog",
                    "rskoog@csri-qt.com",
                    "yes",
                    "test",
                    "",
                    "",
                    "",
                    "",
                    "dynamic value"
                ]),
            string.Empty);

        var person = Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<CredentialsClientUserRow>>(workbook.ClientUsers));
        Assert.Equal("CSRI", person.ClientName);
        Assert.Equal("Ryan Skoog", person.DisplayName);
        Assert.Equal("IT", person.RoleDepartment);
        Assert.Equal("rskoog@csri-qt.com", person.Email);

        var account = Assert.Single(person.Accounts);
        Assert.Equal("General", account.AccountSystem);
        Assert.DoesNotContain(account.Fields, field => field.FieldKey == "user");
        Assert.Contains(account.Fields, field =>
            field.FieldKey == "ad username" && field.Value == "rskoog");
        Assert.Contains(account.Fields, field =>
            field.FieldKey == "365" && field.Value == "yes");
        Assert.Contains(account.Fields, field =>
            field.FieldKey == "new portal field" && field.Value == "dynamic value");
    }

    [Theory]
    [InlineData("Client", "User")]
    [InlineData("Customer", "User / Contact")]
    [InlineData("Company", "Contact")]
    [InlineData("Organization", "Person")]
    [InlineData("Client", "Name")]
    public void ClientUsersWorksheetAcceptsFlexibleIdentityHeaderNames(
        string clientHeader,
        string personHeader)
    {
        var workbook = FireDrillSyncEngine.ReadWorkbookContents(
            CreateClientUsersWorkbook(
                [clientHeader, personHeader, "Any Added Column"],
                ["Example Client", "Dana Brooks", "dynamic value"]),
            string.Empty);

        var person = Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<CredentialsClientUserRow>>(workbook.ClientUsers));
        Assert.Equal("Example Client", person.ClientName);
        Assert.Equal("Dana Brooks", person.DisplayName);
        var account = Assert.Single(person.Accounts);
        Assert.Single(account.Fields);
        Assert.Equal("any added column", account.Fields[0].FieldKey);
        Assert.Equal("dynamic value", account.Fields[0].Value);
    }

    private static byte[] CreateClientUsersWorkbook(params string[][] clientUsersRows)
    {
        if (clientUsersRows.Length == 0)
        {
            clientUsersRows =
            [
                [
                    "Client",
                    "Location / Site",
                    "User / Contact",
                    "Role / Department",
                    "Account Status",
                    "Account / System",
                    "Username / Email",
                    "Password",
                    "PIN",
                    "MFA / Recovery",
                    "Notes",
                    "Last Verified"
                ],
                [
                    "Example Client",
                    "Main office",
                    "Dana Brooks",
                    "Accounting",
                    "Active",
                    "Microsoft 365",
                    "dana@example.test",
                    "M365-secret",
                    "1234",
                    "Authenticator",
                    "Primary account",
                    "2026-07-27"
                ],
                [
                    "Example Client",
                    "Main office",
                    "Dana Brooks",
                    "Accounting",
                    "Active",
                    "VPN",
                    "dana@example.test",
                    "VPN-secret",
                    "",
                    "Recovery code",
                    "",
                    "2026-07-27"
                ]
            ];
        }

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(
                   memory,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            WriteEntry(
                archive,
                "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            WriteEntry(
                archive,
                "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteEntry(
                archive,
                "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>
                    <sheet name="Credentials" sheetId="1" state="visible" r:id="rId1"/>
                    <sheet name="Client Users" sheetId="2" state="visible" r:id="rId2"/>
                  </sheets>
                </workbook>
                """);
            WriteEntry(
                archive,
                "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                </Relationships>
                """);
            WriteEntry(
                archive,
                "xl/worksheets/sheet1.xml",
                Worksheet(
                    ["Client", "Status"],
                    ["Example Client", "Active"]));
            WriteEntry(
                archive,
                "xl/worksheets/sheet2.xml",
                Worksheet(clientUsersRows));
        }

        return memory.ToArray();
    }

    private static string Worksheet(params string[][] rows)
    {
        var rowXml = rows.Select(
            (row, rowIndex) =>
            {
                var cells = row.Select(
                    (value, columnIndex) =>
                        $"<c r=\"{ColumnName(columnIndex + 1)}{rowIndex + 1}\" t=\"inlineStr\">"
                        + $"<is><t>{SecurityElement.Escape(value)}</t></is></c>");
                return $"<row r=\"{rowIndex + 1}\">{string.Concat(cells)}</row>";
            });
        return
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
            """
            + string.Concat(rowXml)
            + """
              </sheetData>
            </worksheet>
            """;
    }

    private static string ColumnName(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }

        return result;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        string contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contents);
    }
}
