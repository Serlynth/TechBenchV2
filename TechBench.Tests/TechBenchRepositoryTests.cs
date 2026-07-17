using TechBench.Data;
using TechBench.Models;
using TechBench.Services;
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
            WorkEntryPostingStatusCalculator.Update(entry);
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
    public void FindsTheLatestSuccessfulWhdLogWithAnExactTechNoteId()
    {
        WithRepository((repository, _) =>
        {
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 15),
                ManualClientName = "Test Client",
                DurationMinutes = 15,
                Note = "Current note"
            };
            repository.SaveWorkEntry(entry);
            repository.AddPostingLog(new PostingLog
            {
                WorkEntryId = entry.Id,
                Destination = "WHD",
                Payload = "{\"noteText\":\"Old note\"}",
                Success = true,
                Message = "Posted",
                ExternalReference = "WHD-TECHNOTE-100"
            });
            repository.AddPostingLog(new PostingLog
            {
                WorkEntryId = entry.Id,
                Destination = "WHD",
                Payload = "{\"noteText\":\"Manual marker\"}",
                Success = true,
                Message = "Marked manually"
            });
            repository.AddPostingLog(new PostingLog
            {
                WorkEntryId = entry.Id,
                Destination = "WHD",
                Payload = "{\"noteText\":\"Current note\"}",
                Success = true,
                Message = "Synchronized",
                ExternalReference = "WHD-TECHNOTE-101"
            });
            repository.AddPostingLog(new PostingLog
            {
                WorkEntryId = entry.Id,
                Destination = "WHD",
                Payload = "failed",
                Success = false,
                Message = "Failed",
                ExternalReference = "WHD-TECHNOTE-102"
            });

            var log = repository.GetLatestVerifiedWhdPostingLog(entry.Id);

            Assert.NotNull(log);
            Assert.Equal("WHD-TECHNOTE-101", log.ExternalReference);
            Assert.Contains("Current note", log.Payload);
        });
    }

    [Fact]
    public void SagePostedEntryCannotBeChangedOrDeletedAtTheRepositoryBoundary()
    {
        WithRepository((repository, _) =>
        {
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 15),
                ManualClientName = "Locked Client",
                DurationMinutes = 15,
                Note = "Final billed note"
            };
            repository.SaveWorkEntry(entry);
            entry.SagePosted = true;
            entry.SagePostedAt = DateTime.Now;
            WorkEntryPostingStatusCalculator.Update(entry);
            repository.SaveWorkEntry(entry);

            entry.Note = "Changed after billing";

            Assert.Throws<InvalidOperationException>(() => repository.SaveWorkEntry(entry));
            Assert.Throws<InvalidOperationException>(() => repository.DeleteWorkEntry(entry.Id));
            Assert.Equal("Final billed note", repository.GetWorkEntry(entry.Id)?.Note);
        });
    }

    [Fact]
    public void WhdPostedEntryCanBeChangedButCannotLoseItsTrackingThroughDeletion()
    {
        WithRepository((repository, _) =>
        {
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 15),
                ManualClientName = "WHD Client",
                TicketNumberText = "123",
                DurationMinutes = 15,
                Note = "Original note",
                WhdPosted = true,
                WhdPostedAt = DateTime.Now
            };
            repository.SaveWorkEntry(entry);

            entry.Note = "Edited before Sage";
            repository.SaveWorkEntry(entry);

            Assert.Equal("Edited before Sage", repository.GetWorkEntry(entry.Id)?.Note);
            Assert.Throws<InvalidOperationException>(() => repository.DeleteWorkEntry(entry.Id));
        });
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
            WorkEntryPostingStatusCalculator.Update(entry);
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
    public void PendingQueueIncludesEditedWhdNotesBeforeSage()
    {
        WithRepository((repository, _) =>
        {
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 15),
                ManualClientName = "Internal client",
                TicketNumberText = "123",
                Billable = false,
                DurationMinutes = 15,
                Note = "Edited note",
                WhdPosted = true,
                WhdPostedAt = DateTime.Now.AddMinutes(-5)
            };
            repository.SaveWorkEntry(entry);

            Assert.Contains(
                repository.GetWorkEntries(new WorkEntryQuery { PendingWhdOnly = true }),
                candidate => candidate.Id == entry.Id);
            Assert.Contains(
                repository.GetWorkEntries(new WorkEntryQuery { PendingAnyOnly = true }),
                candidate => candidate.Id == entry.Id);
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
                IncludePersonalNoteInWhd = true,
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
            Assert.True(loaded.IncludePersonalNoteInWhd);
            Assert.Equal(FollowUpState.Waiting, loaded.FollowUpState);
            Assert.Equal(new DateTime(2026, 7, 16), loaded.FollowUpDueDate);
            Assert.Contains(keywordResults, candidate => candidate.Id == entry.Id);
            Assert.Contains("[Zephyr]", keywordResults.Single(candidate => candidate.Id == entry.Id).SearchSnippet);
            Assert.Contains(followUpResults, candidate => candidate.Id == entry.Id);
        });
    }

    [Fact]
    public void ReusesDistinctTagsAndMatchesOnlyWholeRequestedTags()
    {
        WithRepository((repository, _) =>
        {
            var networkEntry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ManualClientName = "Northwind",
                DurationMinutes = 30,
                Note = "Configured the network.",
                Tags = " onsite, Network, onsite "
            };
            var shortTagEntry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ManualClientName = "Contoso",
                DurationMinutes = 15,
                Note = "Reviewed the short tag.",
                Tags = "net, security"
            };
            repository.SaveWorkEntry(networkEntry);
            repository.SaveWorkEntry(shortTagEntry);

            Assert.Equal("onsite, Network", networkEntry.Tags);
            Assert.Equal(["net", "Network", "onsite", "security"], repository.GetDistinctTags());

            var netMatches = repository.GetWorkEntries(new WorkEntryQuery { Tags = "net" });
            Assert.Single(netMatches);
            Assert.Equal(shortTagEntry.Id, netMatches[0].Id);

            var combinedMatches = repository.GetWorkEntries(new WorkEntryQuery { Tags = "security, NET" });
            Assert.Single(combinedMatches);
            Assert.Equal(shortTagEntry.Id, combinedMatches[0].Id);
        });
    }

    [Fact]
    public void WorkEntryQueryCanExcludeCurrentEntryAndLimitRecentResults()
    {
        WithRepository((repository, _) =>
        {
            var client = new Client { Name = "Northwind" };
            repository.SaveClient(client);
            var entries = Enumerable.Range(1, 7)
                .Select(index => new WorkEntry
                {
                    WorkDate = new DateTime(2026, 7, index),
                    ClientId = client.Id,
                    DurationMinutes = 15,
                    Note = $"Work note {index}"
                })
                .ToList();
            foreach (var entry in entries)
            {
                repository.SaveWorkEntry(entry);
            }

            var results = repository.GetWorkEntries(new WorkEntryQuery
            {
                ClientId = client.Id,
                ExcludeId = entries[^1].Id,
                MaxResults = 5
            });

            Assert.Equal(5, results.Count);
            Assert.DoesNotContain(results, entry => entry.Id == entries[^1].Id);
            Assert.Equal(entries[^2].Id, results[0].Id);
        });
    }

    [Fact]
    public void NoteLinksRoundTripWithFollowUpDirectionAndCascadeOnDelete()
    {
        WithRepository((repository, _) =>
        {
            var earlier = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 13),
                ManualClientName = "Northwind",
                DurationMinutes = 30,
                Note = "Started workstation setup."
            };
            var later = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ManualClientName = "Northwind",
                DurationMinutes = 45,
                Note = "Finished workstation setup."
            };
            repository.SaveWorkEntry(earlier);
            repository.SaveWorkEntry(later);

            repository.SaveWorkEntryLink(later.Id, earlier.Id, WorkEntryLinkType.FollowUpTo);

            var laterLink = Assert.Single(repository.GetWorkEntryLinks(later.Id));
            var earlierLink = Assert.Single(repository.GetWorkEntryLinks(earlier.Id));
            Assert.Equal("Follow-up to", laterLink.RelationshipLabel);
            Assert.Equal(earlier.Id, laterLink.RelatedEntry.Id);
            Assert.Equal("Followed by", earlierLink.RelationshipLabel);
            Assert.Equal(later.Id, earlierLink.RelatedEntry.Id);

            repository.SaveWorkEntryLink(earlier.Id, later.Id, WorkEntryLinkType.Related);

            Assert.Equal("Related", Assert.Single(repository.GetWorkEntryLinks(earlier.Id)).RelationshipLabel);
            Assert.Single(repository.GetWorkEntryLinks(later.Id));

            repository.DeleteWorkEntry(earlier.Id);

            Assert.Empty(repository.GetWorkEntryLinks(later.Id));
        });
    }

    [Fact]
    public void WorkEntryQueryCanFilterLinkCandidatesByTicketId()
    {
        WithRepository((repository, _) =>
        {
            var client = new Client { Name = "Northwind" };
            repository.SaveClient(client);
            var firstTicket = new Ticket
            {
                ClientId = client.Id,
                TicketNumber = "101",
                Subject = "First issue"
            };
            var secondTicket = new Ticket
            {
                ClientId = client.Id,
                TicketNumber = "102",
                Subject = "Second issue"
            };
            repository.SaveTicket(firstTicket);
            repository.SaveTicket(secondTicket);
            var first = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 13),
                ClientId = client.Id,
                TicketId = firstTicket.Id,
                DurationMinutes = 15,
                Note = "First ticket note"
            };
            var second = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ClientId = client.Id,
                TicketId = secondTicket.Id,
                DurationMinutes = 15,
                Note = "Second ticket note"
            };
            repository.SaveWorkEntry(first);
            repository.SaveWorkEntry(second);

            var matches = repository.GetWorkEntries(new WorkEntryQuery { TicketId = firstTicket.Id });

            Assert.Single(matches);
            Assert.Equal(first.Id, matches[0].Id);
        });
    }

    [Fact]
    public void CommonLinksProtectBuiltInsAndSupportCustomLinkLifecycle()
    {
        WithRepository((repository, _) =>
        {
            var defaults = repository.GetCommonLinks();
            Assert.Collection(
                defaults,
                link =>
                {
                    Assert.Equal("WatchGuard Cloud", link.Name);
                    Assert.Equal("https://cloud.watchguard.com/", link.Url);
                    Assert.Equal("watchguard-cloud", link.BuiltInKey);
                },
                link =>
                {
                    Assert.Equal("Microsoft 365 Admin Center", link.Name);
                    Assert.Equal("https://admin.microsoft.com/", link.Url);
                    Assert.Equal("microsoft-365-admin", link.BuiltInKey);
                },
                link =>
                {
                    Assert.Equal("Barracuda Cloud Control", link.Name);
                    Assert.Equal("https://login.barracuda.com/", link.Url);
                    Assert.Equal("barracuda-cloud-control", link.BuiltInKey);
                },
                link =>
                {
                    Assert.Equal("ESET PROTECT Console", link.Name);
                    Assert.Equal("https://protect.eset.com/", link.Url);
                    Assert.Equal("eset-protect", link.BuiltInKey);
                    Assert.Equal("Admin Portals", link.SectionName);
                },
                link =>
                {
                    Assert.Equal("Email2Phone", link.Name);
                    Assert.Equal("https://user.email2phone.net/client/#/authentication/signin", link.Url);
                    Assert.Equal("email2phone", link.BuiltInKey);
                    Assert.Equal("Admin Portals", link.SectionName);
                },
                link =>
                {
                    Assert.Equal("GoDaddy", link.Name);
                    Assert.Equal("https://dcc.godaddy.com/control/portfolio", link.Url);
                    Assert.Equal("godaddy-dns", link.BuiltInKey);
                    Assert.Equal("Hosted DNS", link.SectionName);
                },
                link =>
                {
                    Assert.Equal("Network Solutions", link.Name);
                    Assert.Equal("https://www.networksolutions.com/my-account/login", link.Url);
                    Assert.Equal("network-solutions-dns", link.BuiltInKey);
                    Assert.Equal("Hosted DNS", link.SectionName);
                });

            Assert.All(defaults, link => Assert.True(link.IsBuiltIn));
            Assert.Throws<InvalidOperationException>(() => repository.SaveCommonLink(defaults[0]));
            Assert.Throws<InvalidOperationException>(() => repository.DeleteCommonLink(defaults[0].Id));
            var watchGuardUpdatedAt = defaults[0].UpdatedAt;
            repository.Initialize();
            var initializedLinks = repository.GetCommonLinks();
            Assert.Equal(7, initializedLinks.Count(link => link.IsBuiltIn));
            Assert.Equal(
                watchGuardUpdatedAt,
                initializedLinks.Single(link => link.BuiltInKey == "watchguard-cloud").UpdatedAt);

            var custom = new CommonLink
            {
                Name = "Firewall Portal",
                Url = "https://firewall.example.com/",
                BuiltInKey = "not-a-real-built-in"
            };
            var id = repository.SaveCommonLink(custom);
            Assert.True(id > 0);
            Assert.Equal(id, custom.Id);
            Assert.Null(custom.BuiltInKey);
            Assert.Equal("Custom Links", custom.SectionName);

            custom.Name = "Primary Firewall Portal";
            repository.SaveCommonLink(custom);
            Assert.Equal(
                "Primary Firewall Portal",
                repository.GetCommonLinks().Single(link => link.Id == id).Name);

            repository.DeleteCommonLink(id);
            Assert.DoesNotContain(repository.GetCommonLinks(), link => link.Id == id);
        });
    }

    [Fact]
    public void MigratesExistingCommonLinksAndRestoresMissingBuiltIns()
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
                    CREATE TABLE CommonLinks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        Url TEXT NOT NULL,
                        SortOrder INTEGER NOT NULL DEFAULT 0,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    CREATE TABLE Settings (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Key TEXT NOT NULL UNIQUE,
                        Value TEXT NOT NULL DEFAULT ''
                    );
                    INSERT INTO CommonLinks (Name, Url, SortOrder, CreatedAt, UpdatedAt)
                    VALUES
                        ('Old WatchGuard Name', 'https://cloud.watchguard.com/', 0, '2026-07-15', '2026-07-15'),
                        ('Custom Portal', 'https://portal.example.com/', 4, '2026-07-15', '2026-07-15');
                    INSERT INTO Settings (Key, Value)
                    VALUES ('CommonLinks.DefaultsSeededV1', 'true');
                    """;
                command.ExecuteNonQuery();
            }

            var repository = new TechBenchRepository(factory);
            repository.Initialize();

            var links = repository.GetCommonLinks();
            Assert.Equal(8, links.Count);
            Assert.Equal(7, links.Count(link => link.IsBuiltIn));
            Assert.Equal("WatchGuard Cloud", links.Single(link => link.BuiltInKey == "watchguard-cloud").Name);
            Assert.Equal("ESET PROTECT Console", links.Single(link => link.BuiltInKey == "eset-protect").Name);
            Assert.Equal("Email2Phone", links.Single(link => link.BuiltInKey == "email2phone").Name);
            Assert.Equal("GoDaddy", links.Single(link => link.BuiltInKey == "godaddy-dns").Name);
            Assert.Equal("Network Solutions", links.Single(link => link.BuiltInKey == "network-solutions-dns").Name);
            Assert.False(links.Single(link => link.Name == "Custom Portal").IsBuiltIn);
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
            var committedEntry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 14),
                ClientId = client.Id,
                DurationMinutes = 45,
                Note = "Committed note"
            };
            repository.SaveWorkEntry(committedEntry);
            var draft = new EditorDraft
            {
                WorkEntryId = committedEntry.Id,
                WorkDate = new DateTime(2026, 7, 14),
                ClientId = client.Id,
                DurationMinutesText = "45",
                Note = "Recovered note",
                Tags = "project",
                FollowUpState = FollowUpState.FollowUp,
                PendingFollowUpSourceId = committedEntry.Id
            };
            repository.SaveEditorDraft(draft);
            repository.SaveClientAlias("short name", client.Id);

            var loaded = repository.GetEditorDraft();
            Assert.NotNull(loaded);
            Assert.Equal("Recovered note", loaded.Note);
            Assert.Equal(FollowUpState.FollowUp, loaded.FollowUpState);
            Assert.Equal(committedEntry.Id, loaded.PendingFollowUpSourceId);
            Assert.Equal("Committed note", repository.GetWorkEntry(committedEntry.Id)?.Note);
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
    public void MergesWhdAndSageClientsWithoutLeavingDuplicateReferences()
    {
        WithRepository((repository, _) =>
        {
            var whd = new Client
            {
                Name = "Friends Central",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-44",
                WhdLocationName = "Friends Central",
                LastSyncedAt = DateTime.Now
            };
            repository.SaveClient(whd);
            var sage = new Client
            {
                Name = "FRIEND'S CENTRAL SCHOOL",
                Source = "Sage",
                SageCustomerId = "30462",
                SageCustomerName = "FRIEND'S CENTRAL SCHOOL",
                SageContactName = "Dan Crowley",
                LastSyncedAt = DateTime.Now
            };
            repository.SaveClient(sage);
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 16),
                ClientId = sage.Id,
                DurationMinutes = 30,
                Note = "Updated the firewall."
            };
            repository.SaveWorkEntry(entry);
            repository.SaveClientAlias("FCS", sage.Id);

            var merged = repository.MergeClientRecords(whd.Id, sage.Id);

            Assert.Equal("Both", merged.Source);
            Assert.Equal("30462", merged.SageCustomerId);
            Assert.Equal("Friends Central", merged.WhdLocationName);
            Assert.Null(repository.GetClient(sage.Id));
            Assert.Equal(whd.Id, repository.GetWorkEntry(entry.Id)?.ClientId);
            Assert.Equal(whd.Id, repository.GetClientAliases()["FCS"]);
        });
    }

    [Fact]
    public void ReconcilesExactNormalizedWhdAndSageNames()
    {
        WithRepository((repository, _) =>
        {
            repository.SaveClient(new Client
            {
                Name = "Friends Central School",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-44",
                WhdLocationName = "Friends Central School"
            });
            repository.SaveClient(new Client
            {
                Name = "FRIEND'S CENTRAL SCHOOL",
                Source = "Sage",
                SageCustomerId = "30462",
                SageCustomerName = "FRIEND'S CENTRAL SCHOOL"
            });

            Assert.Equal(1, repository.ReconcileExactClientMatches());
            var merged = Assert.Single(repository.GetClients());
            Assert.Equal("Both", merged.Source);
            Assert.Equal("30462", merged.SageCustomerId);
        });
    }

    [Fact]
    public void ReconcilesStrongUniqueLocationAndSageNames()
    {
        WithRepository((repository, _) =>
        {
            repository.SaveClient(new Client
            {
                Name = "Delancy Street Partners, LLC",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-289",
                WhdLocationName = "Delancy Street Partners, LLC"
            });
            repository.SaveClient(new Client
            {
                Name = "Delancey Street Partners, LLC",
                Source = "Sage",
                SageCustomerId = "68710",
                SageCustomerName = "Delancey Street Partners, LLC"
            });
            repository.SaveClient(new Client
            {
                Name = "Devine & Partners",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-63",
                WhdLocationName = "Devine & Partners"
            });
            repository.SaveClient(new Client
            {
                Name = "DEVINE & PARTNERS COMMUNICATIONS GROUP",
                Source = "Sage",
                SageCustomerId = "19104",
                SageCustomerName = "DEVINE & PARTNERS COMMUNICATIONS GROUP"
            });

            Assert.Equal(2, repository.ReconcileStrongClientMatches());
            var clients = repository.GetClients();
            Assert.Equal(2, clients.Count);
            Assert.All(clients, client => Assert.Equal("Both", client.Source));
            Assert.Contains(clients, client => client.SageCustomerId == "68710");
            Assert.Contains(clients, client => client.SageCustomerId == "19104");
        });
    }

    [Fact]
    public void WhdTicketSyncReusesMatchedCompanyLocationInsteadOfCreatingAContactDuplicate()
    {
        WithRepository((repository, _) =>
        {
            var location = new Client
            {
                Name = "Friends Central",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-44",
                WhdLocationName = "Friends Central"
            };
            repository.SaveClient(location);
            var sage = new Client
            {
                Name = "FRIEND'S CENTRAL SCHOOL",
                Source = "Sage",
                SageCustomerId = "30462",
                SageCustomerName = "FRIEND'S CENTRAL SCHOOL"
            };
            repository.SaveClient(sage);
            repository.MergeClientRecords(location.Id, sage.Id);

            repository.SynchronizeWhdTickets(
                [
                    new WhdSyncedTicket
                    {
                        ExternalId = "WHD-123",
                        TicketNumber = "WHD-123",
                        Subject = "Wireless issue",
                        Status = "Open",
                        Client = new WhdSyncedClient
                        {
                            ExternalId = "WHD-77",
                            Name = "Friends Central - Ed",
                            LocationName = "Friends Central",
                            ContactName = "Ed"
                        }
                    }
                ],
                DateTime.Now,
                reconcileMissing: true);

            var client = Assert.Single(repository.GetClients());
            Assert.Equal("Friends Central", client.Name);
            Assert.Equal("Ed", client.WhdContactName);
            Assert.Equal("30462", client.SageCustomerId);
            Assert.True(TechBenchRepository.ContainsExternalId(client.ExternalId, "WHD-LOCATION-44"));
            Assert.True(TechBenchRepository.ContainsExternalId(client.ExternalId, "WHD-77"));
            Assert.Equal(client.Id, Assert.Single(repository.GetTickets()).ClientId);
        });
    }

    [Fact]
    public void CorrectLocationCanAbsorbLegacyMatchedWhdContactRecord()
    {
        WithRepository((repository, _) =>
        {
            var location = new Client
            {
                Name = "Friends Central",
                Source = "WHD",
                ExternalId = "WHD-LOCATION-44",
                WhdLocationName = "Friends Central"
            };
            repository.SaveClient(location);
            var legacy = new Client
            {
                Name = "Friends Central - Ed",
                Source = "Both",
                ExternalId = "WHD-77",
                WhdLocationName = "Friends Central",
                WhdContactName = "Ed",
                SageCustomerId = "30462",
                SageCustomerName = "FRIEND'S CENTRAL SCHOOL"
            };
            repository.SaveClient(legacy);
            var entry = new WorkEntry
            {
                WorkDate = new DateTime(2026, 7, 16),
                ClientId = legacy.Id,
                DurationMinutes = 15,
                Note = "Reviewed the ticket."
            };
            repository.SaveWorkEntry(entry);

            var merged = repository.MergeClientRecords(location.Id, legacy.Id);

            Assert.Equal("Both", merged.Source);
            Assert.Equal("Friends Central", merged.Name);
            Assert.Equal("30462", merged.SageCustomerId);
            Assert.Null(repository.GetClient(legacy.Id));
            Assert.Equal(location.Id, repository.GetWorkEntry(entry.Id)?.ClientId);
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
