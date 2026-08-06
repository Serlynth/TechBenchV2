# TechBench Server 0.6.19

This stable SQL release completes the Client Information workbook password-import fix. After comparing imported passwords with FireDrill, the import now returns its results without re-evaluating the signed-in technician under the database-owner encryption context.

The normal workbook results procedure still validates the real Windows user's TechBench access. Only its internal result query is reused by the protected comparison procedure, so no additional database permissions are granted.

Apply `TechBenchV2-SQLServer2016-0.6.19.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled, then retry the same completed workbook. The workbook does not need to be recreated. The schema version remains 15.
