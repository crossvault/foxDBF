# Synthetic VFP9 import schemas

These fixtures contain only generated test values (`1`, `2`, `one`, `two`, `nil`, default `42`).
They were created by VFP9 using the literal setup programs in `DataImportTests.Cases`.
No customer data was used. Each directory is named after its scenario. DBC files provide
field defaults/rules/primary keys; DBF descriptors provide autoincrement seeds and nullability;
CDX files provide persisted candidate/primary tags. Tests copy these seeds before writing.

`DataImportVfp9OracleTests` recreates each scenario with the same setup in VFP9, then
compares command outcomes and complete DBF record bytes with the library. These schemas
avoid depending on microVFP's separate CREATE DATABASE / ALTER TABLE / AUTOINC DDL coverage.
