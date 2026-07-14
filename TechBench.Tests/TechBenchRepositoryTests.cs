using TechBench.Data;
using TechBench.Models;
using Microsoft.Data.Sqlite;

namespace TechBench.Tests;

public sealed class TechBenchRepositoryTests
{
    [Fact]
    public void MigratesPreNoteSchemaWithoutLosingEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TechBenchTests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "techbench.db");
        try
        {
            var factory = new SqliteConnectionFactory(databasePath);
            using (var connection = factory.CreateConnection())
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE WorkEntries (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkDate TEXT NOT NULL,
                        ClientId INTEGER NULL,
                        ManualClientName TEXT NULL,
                        TicketId INTEGER NULL,
                        TicketNumberText TEXT NULL,
                        HasTimeRange INTEGER NOT NULL DEFAULT 1,
                        StartTime TEXT NOT NULL DEFAULT '00:00',
                        EndTime TEXT NOT NULL DEFAULT '00:00',
                        DurationMinutes INTEGER NOT NULL DEFAULT 0,
                        Billable INTEGER NOT NULL DEFAULT 1,
                        Note TEXT NOT NULL DEFAULT '',
                        InternalNote TEXT NULL,
                        WhdPosted INTEGER NOT NULL DEFAULT 0,
                        WhdPostedAt TEXT NULL,
                        SagePosted INTEGER NOT NULL DEFAULT 0,
                        SagePostedAt TEXT NULL,
                        SageTicketNumber TEXT NULL,
                        PostingStatus TEXT NOT NULL DEFAULT 'Draft',
                        LastError TEXT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    INSERT INTO WorkEntries
                        (WorkDate, ManualClientName, DurationMinutes, Billable, Note, CreatedAt, UpdatedAt)
                    VALUES
                        ('2026-07-10', 'Legacy Client', 30, 1, 'Legacy migration note.',
                         '2026-07-10T09:00:00.0000000', '2026-07-10T09:00:00.0000000');
                    """;
                command.ExecuteNonQuery();
            }

            var repository = new TechBenchRepository(factory);
            repository.Initialize();

            var entry = Assert.Single(repository.GetWorkEntries(new WorkEntryQuery { Keyword = "legacy" }));
            Assert.Equal("Legacy migration note.", entry.Note);
            Assert.Equal(string.Empty, entry.Tags);
            Assert.Equal(FollowUpState.None, entry.FollowUpState);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("WHD-12", "WHD-12", true)]
    [InlineData("WHD-12 / WHD-34", "whd-34", true)]
    [InlineData("WHD-123", "WHD-12", false)]
    [InlineData("WHD-12", "WHD-123", false)]
    [InlineData("WHD-12 / WHD-34", "WHD-3", false)]
    [InlineData(null, "WHD-12", false)]
    public void ExternalIdsMatchOnlyWholeTokens(string? storedIds, string candidateId, bool expected)
    {
        Assert.Equal(expected, TechBenchRepository.ContainsExternalId(storedIds, candidateId));
    }

    [Theory]
    [InlineData("Filled Sage ticket #147773 and left it unsaved for review.", "147773")]
    [InlineData("Saved Sage time ticket #001190 and opened a fresh blank ticket.", "001190")]
    public void ExtractsSageTicketNumberFromExistingPostingMessages(string message, string expected)
    {
        Assert.True(TechBenchRepository.TryExtractSageTicketNumber(message, out var ticketNumber));
        Assert.Equal(expected, ticketNumber);
    }

    [Fact]
    public void DoesNotTreatFollowingMessageWordAsTicketNumber()
    {
        Assert.False(TechBenchRepository.TryExtractSageTicketNumber(
            "Filled Sage ticket and left it unsaved for review.",
            out var ticketNumber));
        Assert.Empty(ticketNumber);
    }

    [Fact]
    public void PersistsSageDraftNumberAndPostingExternalReference()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TechBenchTests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "techbench.db");
        try
        {
            var repository = new TechBenchRepository(new SqliteConnectionFactory(databasePath));
            repository.Initialize();
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 10),
                ManualClientName = "Test Client",
                HasTimeRange = false,
                DurationMinutes = 15,
                Note = "Test note",
                SageTicketNumber = "147773",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            TechBenchRepository.UpdatePostingStatus(entry);
            repository.SaveWorkEntry(entry);
            repository.AddPostingLog(new PostingLog
            {
                WorkEntryId = entry.Id,
                Destination = "Sage",
                Success = true,
                Message = "Filled Sage ticket #147773 and left it unsaved for review.",
                ExternalReference = "SAGE-147773"
            });

            var loaded = repository.GetWorkEntry(entry.Id);
            var log = Assert.Single(repository.GetPostingLogs());

            Assert.NotNull(loaded);
            Assert.Equal("147773", loaded.SageTicketNumber);
            Assert.False(loaded.SagePosted);
            Assert.Equal("SAGE-147773", log.ExternalReference);

            loaded.SageTicketNumber = null;
            repository.SaveWorkEntry(loaded);
            repository.Initialize();
            Assert.Equal("147773", repository.GetWorkEntry(entry.Id)!.SageTicketNumber);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RemovesLegacyMockSettingsAndRestoresMockOnlyPostingStateToLivePending()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TechBenchTests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "techbench.db");
        try
        {
            var factory = new SqliteConnectionFactory(databasePath);
            var repository = new TechBenchRepository(factory);
            repository.Initialize();
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 13),
                ManualClientName = "Test Client",
                DurationMinutes = 15,
                Note = "Test note",
                WhdPosted = true,
                WhdPostedAt = DateTime.Now,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            TechBenchRepository.UpdatePostingStatus(entry);
            repository.SaveWorkEntry(entry);
            repository.AddPostingLog(new PostingLog
            {
                WorkEntryId = entry.Id,
                Destination = "WHD",
                Success = true,
                Message = "Mock Web Help Desk post recorded.",
                ExternalReference = $"MOCK-WHD-{entry.Id}"
            });

            using (var connection = factory.CreateConnection())
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM Settings WHERE Key = 'Posting.MockModesRemovedV2';
                    INSERT INTO Settings (Key, Value) VALUES ('Whd.MockMode', 'true');
                    INSERT INTO Settings (Key, Value) VALUES ('Sage.MockMode', 'true');
                    """;
                command.ExecuteNonQuery();
            }

            repository.Initialize();

            var loaded = repository.GetWorkEntry(entry.Id);
            var settings = repository.GetSettings();
            Assert.NotNull(loaded);
            Assert.False(loaded.WhdPosted);
            Assert.Null(loaded.WhdPostedAt);
            Assert.Equal(PostingStatus.Ready, loaded.PostingStatus);
            Assert.DoesNotContain("Whd.MockMode", settings.Keys);
            Assert.DoesNotContain("Sage.MockMode", settings.Keys);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DurablePostingAttemptBlocksConcurrentAndInterruptedRetries()
    {
        WithRepository((repository, _) =>
        {
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 13),
                ManualClientName = "Client",
                DurationMinutes = 15,
                Note = "Work note"
            };
            repository.SaveWorkEntry(entry);

            var first = repository.TryBeginPostingAttempt(entry.Id, "WHD", "attempt-1", "hash-1");
            var blocked = repository.TryBeginPostingAttempt(entry.Id, "WHD", "attempt-2", "hash-2");
            Assert.True(first.Started);
            Assert.False(blocked.Started);
            Assert.Equal(PostingAttemptStatus.Started, blocked.OutstandingAttempt?.Status);

            repository.Initialize();
            var interrupted = repository.GetOutstandingPostingAttempt(entry.Id, "WHD");
            Assert.NotNull(interrupted);
            Assert.Equal(PostingAttemptStatus.Unknown, interrupted.Status);

            repository.AbandonOutstandingPostingAttempts(entry.Id, "WHD", "Confirmed safe retry.");
            var retry = repository.TryBeginPostingAttempt(entry.Id, "WHD", "attempt-3", "hash-3");
            Assert.True(retry.Started);
            repository.CompletePostingAttempt(retry.Attempt!.Id, PostingAttemptStatus.Succeeded, "Posted", "WHD-TECHNOTE-1");
            Assert.Null(repository.GetOutstandingPostingAttempt(entry.Id, "WHD"));
        });
    }

    [Fact]
    public void PendingQueueFilterRunsInSqlAndExcludesNonBillableSageOnlyEntries()
    {
        WithRepository((repository, _) =>
        {
            var sagePending = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 13),
                ManualClientName = "Billable",
                Billable = true,
                DurationMinutes = 15,
                Note = "Billable work"
            };
            var noDestination = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 13),
                ManualClientName = "Non-billable",
                Billable = false,
                DurationMinutes = 15,
                Note = "Internal work"
            };
            repository.SaveWorkEntry(sagePending);
            repository.SaveWorkEntry(noDestination);

            var pending = repository.GetWorkEntries(new WorkEntryQuery { PendingAnyOnly = true });

            Assert.Contains(pending, entry => entry.Id == sagePending.Id);
            Assert.DoesNotContain(pending, entry => entry.Id == noDestination.Id);
            Assert.Equal(sagePending.Id, repository.GetWorkEntry(sagePending.Id)?.Id);
        });
    }

    [Fact]
    public void PersistsNoteMetadataAndUsesFullTextSearch()
    {
        WithRepository((repository, _) =>
        {
            Assert.True(repository.FullTextSearchAvailable);
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ManualClientName = "Northwind",
                DurationMinutes = 30,
                Note = "Configured the Zephyr firewall and verified traffic.",
                InternalNote = "Waiting for customer confirmation.",
                Tags = "network, onsite",
                FollowUpState = FollowUpState.Waiting,
                FollowUpDueDate = new DateTime(2026, 7, 16)
            };
            repository.SaveWorkEntry(entry);

            var loaded = repository.GetWorkEntry(entry.Id);
            var keywordResults = repository.GetWorkEntries(new WorkEntryQuery { Keyword = "zeph" });
            var followUpResults = repository.GetWorkEntries(new WorkEntryQuery
            {
                Tags = "onsite",
                OpenFollowUpsOnly = true
            });

            Assert.NotNull(loaded);
            Assert.Equal("network, onsite", loaded.Tags);
            Assert.Equal(FollowUpState.Waiting, loaded.FollowUpState);
            Assert.Equal(new DateTime(2026, 7, 16), loaded.FollowUpDueDate);
            Assert.Contains(keywordResults, candidate => candidate.Id == entry.Id);
            Assert.Contains("[Zephyr]", keywordResults.Single(candidate => candidate.Id == entry.Id).SearchSnippet);
            Assert.Contains(followUpResults, candidate => candidate.Id == entry.Id);
        });
    }

    [Fact]
    public void EditorDraftAndClientAliasesRoundTrip()
    {
        WithRepository((repository, _) =>
        {
            var clientId = repository.UpsertSageCustomer(new SageCustomer
            {
                CustomerId = "80000",
                CustomerName = "CSRI"
            });
            var client = Assert.Single(repository.GetClients(), candidate => candidate.Id == clientId);
            var draft = new EditorDraft
            {
                WorkDate = new DateTime(2026, 7, 14),
                ClientId = client.Id,
                DurationMinutesText = "45",
                Note = "Recovered note",
                Tags = "project",
                FollowUpState = FollowUpState.FollowUp
            };
            repository.SaveEditorDraft(draft);
            repository.SaveClientAlias("short name", client.Id);

            var loaded = repository.GetEditorDraft();
            Assert.NotNull(loaded);
            Assert.Equal("Recovered note", loaded.Note);
            Assert.Equal(FollowUpState.FollowUp, loaded.FollowUpState);
            Assert.Equal(client.Id, repository.GetClientAliases()["SHORT NAME"]);

            repository.ClearEditorDraft();
            Assert.Null(repository.GetEditorDraft());
        });
    }

    [Fact]
    public void ImportsEntriesAndAliasesTransactionallyAndManagesTemplates()
    {
        WithRepository((repository, _) =>
        {
            var clientId = repository.UpsertSageCustomer(new SageCustomer
            {
                CustomerId = "20659",
                CustomerName = "Impact Marketing"
            });
            var entries = new[]
            {
                new WorkEntry
                {
                    WorkDate = new DateTime(2026, 7, 12),
                    ClientId = clientId,
                    DurationMinutes = 45,
                    Note = "Migrated network note."
                },
                new WorkEntry
                {
                    WorkDate = new DateTime(2026, 7, 13),
                    ManualClientName = "Walk-in",
                    DurationMinutes = 15,
                    Note = "Migrated workstation note."
                }
            };

            var count = repository.ImportWorkEntries(
                entries,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["impact"] = clientId
                });

            Assert.Equal(2, count);
            Assert.Equal(2, repository.GetWorkEntries(new WorkEntryQuery { Keyword = "migrated" }).Count);
            Assert.Equal(clientId, repository.GetClientAliases()["IMPACT"]);

            var template = new NoteTemplate
            {
                Name = "Custom setup",
                Category = "Test",
                TemplateText = "Configured {client}."
            };
            var templateId = repository.SaveTemplate(template);
            template.TemplateText = "Configured and verified {client}.";
            repository.SaveTemplate(template);

            Assert.Equal(
                "Configured and verified {client}.",
                Assert.Single(repository.GetTemplates(), candidate => candidate.Id == templateId).TemplateText);

            repository.DeleteTemplate(templateId);
            Assert.DoesNotContain(repository.GetTemplates(), candidate => candidate.Id == templateId);
        });
    }

    [Fact]
    public void ReconcilesMissingWhdTicketsOnlyAfterCompleteSync()
    {
        WithRepository((repository, _) =>
        {
            var first = BuildWhdTicket("101");
            var second = BuildWhdTicket("102");
            repository.SynchronizeWhdTickets([first, second], DateTime.Now, reconcileMissing: true);

            repository.SynchronizeWhdTickets([first], DateTime.Now, reconcileMissing: false);
            Assert.False(repository.GetTickets(includeClosed: true).Single(ticket => ticket.TicketNumber == "102").IsClosed);

            repository.SynchronizeWhdTickets([first], DateTime.Now, reconcileMissing: true);
            var missing = repository.GetTickets(includeClosed: true).Single(ticket => ticket.TicketNumber == "102");
            Assert.True(missing.IsClosed);
            Assert.Equal("No longer assigned in WHD", missing.Status);
        });
    }

    private static WhdSyncedTicket BuildWhdTicket(string id) => new()
    {
        ExternalId = $"WHD-{id}",
        TicketNumber = id,
        Subject = $"Ticket {id}",
        Status = "Open",
        Client = new WhdSyncedClient
        {
            ExternalId = "WHD-CLIENT-1",
            Name = "Example Client",
            LocationName = "Example Client"
        }
    };

    private static void WithRepository(Action<TechBenchRepository, SqliteConnectionFactory> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TechBenchTests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "techbench.db");
        try
        {
            var factory = new SqliteConnectionFactory(databasePath);
            var repository = new TechBenchRepository(factory);
            repository.Initialize();
            action(repository, factory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
