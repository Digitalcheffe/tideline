# Truist fixtures

Made-up exports for tests. Nothing here is real. Expected numbers are worked out by hand in
`tests/Tideline.Import.Tests/ImportPipelineTests.cs`; never copy real rows into this folder.

- `acct_1111_*` checking, opening balance $1,000.00 the day before the first row
- `acct_3333_*` savings, opening balance $5,000.00
- `acct_2222_*` credit card (purchases positive, payments in parentheses)
- `acct_1111_03_09_*` overlaps the checking export by one day and adds one row
- `acct_1111_missing_row.csv` the checking export with the CITY POWER row removed, so the balance check fails
