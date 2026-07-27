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

    private static byte[] CreateClientUsersWorkbook()
    {
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
                Worksheet(
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
                    ]));
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
