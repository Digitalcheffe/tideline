# Tideline

A self-hosted household ledger. One simple in/out ledger, every number drillable, quiet unless
something needs attention.

Tideline imports bank exports, pairs transfers between your own accounts so nothing is counted twice,
gives each line one meaning (income, bill, required, extra, one-time or savings), and watches for the
few things that need a person: a bill that did not get paid, a card balance carried over, a plan that
is slipping.

> **Status:** Phases 1 and 2 are built: import and the screens (Overview, Accounts, Cards, Bills, Required, Extra,
> Income, Lines with editing and splits). Sign-in with MFA is Phase 3, so for now the app only answers the computer it runs
> on. Progress is tracked in the [GitHub issues](https://github.com/Digitalcheffe/tideline/issues).

## Try it (Phase 1)

Needs the .NET 10 SDK. Keep your own household file and bank exports outside this repository.

```bash
dotnet run --project src/Tideline.Cli -- migrate --db "Data Source=/path/to/tideline.db"
dotnet run --project src/Tideline.Cli -- setup my-household.json --db "Data Source=/path/to/tideline.db"
dotnet run --project src/Tideline.Cli -- import /path/to/exports --db "Data Source=/path/to/tideline.db"
dotnet run --project src/Tideline.Cli -- status --db "Data Source=/path/to/tideline.db"
```

Run the app locally with one command (needs the .NET 10 SDK and Node 24):

```bash
npm --prefix web install
npm --prefix web run dev:demo   # first run fills data/dev/tideline.db with 5,000 made-up lines
npm --prefix web run dev:all    # the same app on whatever is in that database
```

Then open http://localhost:5173. The API restarts when C# changes and the web app reloads when TypeScript changes. The database lives in `data/dev/` (ignored by git); set `TIDELINE_DB` to use another file. Stop with Ctrl+C.

The period picker is at the top of the menu on the left; every number is a link to the lines behind it. Tests: `dotnet test`, `npm --prefix web test`, and `npm --prefix web run e2e` (set `PW_CHANNEL=msedge` or `chrome` to use an installed browser).

To try it with a realistic amount of data, generate made-up exports (5,000 lines, six accounts, 21 months) and load them in one go:

```bash
dotnet run --project src/Tideline.Cli -- demo ./demo-data --load --db "Data Source=/path/to/demo.db"
```

The numbers come from a seed, so the same options always give the same files. Keep the folder outside the repository.

`tests/fixtures/household.json` shows the household file format with made-up data. `status` lists each
account's ledger balance next to the bank's last balance; they should match to the cent.

## Principles

- **Bank rows are never edited or deleted.** Imported fields are written once; database triggers refuse changes.
- **No double entry.** One row per bank line. Money between your own accounts is a *move*, not a type.
- **One type per line**, so the spread always adds up. Free tags never change totals.
- **Money is integer cents.** No floating point anywhere near a balance.
- **Every number drills down** to the lines behind it, through one shared drill-down contract.
- **MFA is mandatory** for every user, and every change is recorded in an append-only audit log.
- **Internal only.** Self-hosted, no public exposure.

## Planned stack

| Layer | Choice |
|---|---|
| Backend | ASP.NET Core (.NET 10 LTS) minimal APIs, Dapper, FluentMigrator |
| Frontend | React + TypeScript (Vite) SPA, served by the same container |
| Database | SQLite by default; MariaDB supported from day one |
| Logging | Serilog |
| Deploy | Single container in its own Docker/Compose stack, bind mounts only |

## Build phases

1. **Ledger and ingestion:** database, accounts, CSV import and inbox, rules, moves, balance check.
2. **Core screens:** overview, accounts by month, credit cards, bills grid, required, extra, income.
3. **Settings and security:** users with mandatory MFA, categories, rules, imports, audit and system logs, backups.
4. **Budget, planning, savings, net worth, retirement.**
5. **Notifications and SimpleFIN:** alerts, monthly summary, automatic sync.
6. **Later:** asset ledgers and recommendations.

Each phase ends with a gate that is checked against real bank numbers before the next one starts.

## Documentation

Everything lives in [`docs/`](docs/README.md). Suggested reading order:

| Doc | What it covers |
|---|---|
| [00-architecture](docs/00-architecture.md) | The big picture: principles, diagram, data model, screens, phases |
| [01-overview](docs/01-overview.md) | Principles, vocabulary, the spread equation |
| [02-solution-layout](docs/02-solution-layout.md) | Stack, projects, packages, configuration, conventions |
| [03-database](docs/03-database.md) | Tables, portability rules, migrations, moving SQLite to MariaDB |
| [04-domain-rules](docs/04-domain-rules.md) | Every calculation |
| [05-import](docs/05-import.md) | Truist CSV formats, external ids, balance check, inbox, SimpleFIN |
| [06-rules-engine](docs/06-rules-engine.md) | Conditions, actions, ordering, testing rules |
| [07-api](docs/07-api.md) | REST endpoints, drill-down contract, errors, paging |
| [08-screens](docs/08-screens.md) | Every screen: route, data, drill links, acceptance |
| [09-security](docs/09-security.md) | Users, MFA, sessions, roles, API tokens, encryption |
| [10-notifications-logging](docs/10-notifications-logging.md) | Alerts, monthly summary, audit and system logs |
| [11-deploy](docs/11-deploy.md) | Container, stack, backups, MariaDB switch |
| [12-phases-and-tests](docs/12-phases-and-tests.md) | Build phases with gates, test plan, open decisions |

Also in `docs/`:

- [`db/schema.sqlite.sql`](docs/db/schema.sqlite.sql): the schema contract
- [`db/queries.sql`](docs/db/queries.sql): reference report SQL, portable across SQLite and MariaDB
- [`reference/python/`](docs/reference/python): working reference for the Truist CSV parser and payee cleaning
- [`CLAUDE.md`](docs/CLAUDE.md): working instructions for AI-assisted development

## No household data in this repository

The app ships empty. A household is set up inside the app and its data arrives through imports. Tests
use made-up files written for the purpose. Real bank exports, balances, account numbers, names, and
secrets (passwords, tokens, API keys, encryption keys) never go in the repository.
