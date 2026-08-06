# TechBench Server 0.6.21

This stable SQL release completes the Client Information workbook review workflow. Validation warnings now identify the exact sheet, row, record type, and record name when available.

Reimporting a revised copy of the same workbook now supersedes its earlier unfinished review instead of leaving two active copies. Existing duplicate unfinished revisions are cleaned up automatically while their audit history remains intact. Authorized migration operators can explicitly accept remaining unverified rows as Keep as-is or discard any unpromoted review, including an approved review that has not yet been added to Client Information.

Apply `TechBenchV2-SQLServer2016-0.6.21.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
