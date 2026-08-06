# TechBench Server 0.6.20

This stable SQL release adds Phone and Email as supported Client Information field types. Generated workbook columns such as ISP Support Phone and vendor Support Email now pass staging validation and can be promoted into Client Information without being flattened to generic text.

The update repairs the field-type constraints on existing schema-15 databases, keeps manual Client Information editing consistent with workbook imports, and makes future invalid-field messages identify the workbook sheet, row, and field label.

Apply `TechBenchV2-SQLServer2016-0.6.20.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. Then select the already staged workbook and click **Refresh Checks**; the workbook does not need to be recreated or imported again. Unverified-row warnings remain until those rows are reviewed or explicitly accepted. The schema version remains 15.
