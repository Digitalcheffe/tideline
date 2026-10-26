# Tideline: notes for Claude Code

Tideline is a self-hosted household ledger. Read `README.md`, then `docs/ARCHITECTURE.md` for how it fits together. The tests are the
detailed spec: if code and the architecture disagree, stop and ask the owner rather than quietly picking one.

## Non-negotiables

1. **Bank rows are never edited or deleted.** `posted_on`, `amount_cents`, `original_description`, `bank_balance_cents`, `check_number`,
   `external_id`, `source` and `account_id` are written once at import. The repository layer has no method that updates them and the database
   triggers in `docs/db/schema.sqlite.sql` refuse it. Do not remove the triggers. A person can leave a line out of the totals instead.
2. **No double entry.** One row per bank line. Money between our own accounts is a *move* (two rows sharing a `move_id`), not a type.
3. **Exactly one type per effective line**: `income | bill | required | extra | one_time | savings`. Free tags never change totals.
4. **Money is integer cents** (`long` in C#, `_cents` columns). Never `double` or `float` for money. Rates are basis points. Dates are
   `DateOnly`; instants are UTC `DateTimeOffset`.
5. **Every number on every screen drills down** to the lines behind it, using the one drill contract (the `/lines` query). No screen invents
   its own filter shape.
6. **MFA is mandatory** for every user. No session is fully authenticated until the second factor passes, and no setting turns it off. The one
   exception is the fenced development switch: Development environment only, localhost only, with a red banner.
7. **Everything a person changes is in the audit log**, with before and after. `audit_log` is append-only. A new route that changes data needs
   a case in `GateTests`.
8. **Keep SQL portable** (no engine-specific features beyond what is already used) so MariaDB can be added later. SQLite is what is built and tested.
9. **Secrets**: never print, log, commit or paste a token, a password, the SimpleFIN access URL or the Data Protection keys.

## Private data

The repository holds **no household data**: no real exports, balances, account numbers or names. The app ships empty. Tests use made-up
files written for the purpose, with expected numbers worked out by hand in the test. Never copy real rows into fixtures, screenshots or docs.

## How to work

- Every change ships with tests, and the schema contract test must still pass (`docs/db/schema.sqlite.sql` is the contract the migrations are compared to).
- Decisions not covered by the architecture: list them for the owner with a recommendation; do not take liberties.
- Writing for people (screen text, messages, docs): plain words, short sentences, no em-dashes.
- Commit and push when a piece is done; confirm before anything outward-facing or hard to undo.

## Stack

ASP.NET Core (.NET 10) minimal APIs, Dapper (no EF Core), FluentMigrator, SQLite; React and TypeScript (Vite) served by the same container.
Tests: xUnit and FluentAssertions, Vitest, Playwright. On Windows run `dotnet` and `node` from PowerShell. See `docs/DEVELOPMENT.md`.
