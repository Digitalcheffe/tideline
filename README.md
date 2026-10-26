<h1 align="center">
  <img src="docs/logo/tideline-logo.webp" alt="Tideline" width="460">
</h1>

<p align="center"><strong>A private household money ledger</strong></p>

> Know where the money went, and what needs you, without it becoming a hobby.

Tideline is a self-hosted ledger for a household. You give it your bank exports, it sorts every transaction, and it shows where the month's money went: what came in, what the bills were, what you had to spend, what you chose to spend, what you saved, and what was left. It keeps quiet until something needs a person, such as a bill that did not get paid or a card balance that was carried over. One Docker image, one data folder, nothing sent anywhere.

---

## A Note on How This Is Built

Tideline is built with **[Claude Code](https://claude.ai/code)**. The design is written down in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), work was broken into phases tracked as GitHub issues, and every change ships with tests (xUnit for the server, Vitest and Playwright for the web app, and a CI run on Linux). The issue history and the commit log show how each piece got here.

---

## What it does

Tideline is three things in one app:

- **A ledger you can trust.** Every bank line is kept exactly as the bank gave it and can never be edited or deleted. You change what a line *means* (its category, type, tags, splits), never the amount or the date. Money moving between your own accounts is a transfer, not spending, so nothing is counted twice.
- **A summary you can check.** Every month adds up the same way: money in equals bills, plus required, plus extra, plus one-time, plus savings, plus what is left over. Every number on every screen is a link to the exact lines behind it.
- **A watcher that stays quiet.** It looks for the few things that need a person and tells you only about those, in the app and, if you want, by webhook or email.

---

## Screenshot

![Tideline Overview](docs/screenshots/01-overview.png)

<table>
  <tr>
    <td align="center"><a href="docs/screenshots/02-bills.png"><img src="docs/screenshots/02-bills.png" alt="Bills grid" width="150"></a><br><sub>Bills grid</sub></td>
    <td align="center"><a href="docs/screenshots/05-ledger-line.png"><img src="docs/screenshots/05-ledger-line.png" alt="A line" width="150"></a><br><sub>A line</sub></td>
    <td align="center"><a href="docs/screenshots/10-assets.png"><img src="docs/screenshots/10-assets.png" alt="Assets" width="150"></a><br><sub>Assets</sub></td>
    <td align="center"><a href="docs/screenshots/08-retirement.png"><img src="docs/screenshots/08-retirement.png" alt="Retirement" width="150"></a><br><sub>Retirement</sub></td>
    <td align="center"><a href="docs/screenshots/11-recommendations.png"><img src="docs/screenshots/11-recommendations.png" alt="Suggestions" width="150"></a><br><sub>Suggestions</sub></td>
    <td align="center"><a href="docs/screenshots/15-overview-dark.png"><img src="docs/screenshots/15-overview-dark.png" alt="Dark mode" width="150"></a><br><sub>Dark mode</sub></td>
  </tr>
</table>

Click a picture to see it full size, or see [all the screenshots](docs/screenshots/README.md).

---

## Features

- **Ledger**: every bank line, never edited; filter, split, tag, leave out
- **Accounts**: checking, savings, cards, loans, assets, retirement
- **Imports**: CSV upload, inbox folder, SimpleFIN
- **Rules**: sort lines automatically, try them on past months first
- **Transfers**: paired between your accounts, not counted as spending
- **Bills**: a 12-month grid of paid, due and missing
- **Cards**: paid in full, or balance carried
- **Budget and plans**: guidelines, saving goals with a date
- **Savings**: balances and interest
- **Net worth**: what you own and owe, loan payoff dates
- **Assets**: house and car equity and upkeep
- **Retirement**: the gap, and what closes it
- **Alerts**: webhook or email, and a monthly summary
- **MFA**: required for everyone, with recovery codes
- **Roles**: owner, member, viewer
- **Audit log**: who changed what, before and after
- **Backups**: checked copies, and export to CSV
- **Docker**: one container, one SQLite file
- **Light and dark**, and works on a phone
- **Help** built in

The longer tour, screen by screen, is in [docs/FEATURES.md](docs/FEATURES.md).

---

## Quick Start

You need Docker. From a clone of this repository:

```bash
docker compose -f deploy/compose.local.yaml up -d --build
```

The first build runs the whole test suite inside the build, so give it a few minutes. Then open **http://localhost:8080**. An empty Tideline asks you to create the first account (you become the owner), and then to set up an authenticator app and save your recovery codes.

Want something to look at before using your own data? This loads a made-up household with 5,000 lines:

```bash
docker exec tideline_app tideline demo /tmp/demo --lines 5000 --load
```

Start over with an empty app at any time:

```bash
docker compose -f deploy/compose.local.yaml down -v
```

The local compose file binds to this computer only. The inbox folder and backups are set in the app (Settings), and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) to run it from source.

---

## Configuration

### Environment variables

Settings use the `Section__Name` form, which is how .NET reads them from the environment.

| Variable | Description | Default |
|---|---|---|
| `Tideline__PublicUrl` | The address people use. Used in setup links and notifications | `http://localhost:5173` (the compose file sets `http://localhost:8080`) |
| `Tideline__TrustedProxies` | Your reverse proxy's address or range, comma separated (`172.18.0.5`, `172.18.0.0/16`). Only these are believed about who the visitor is. Leave empty with no proxy | empty |
| `Tideline__Timezone` | IANA time zone for "today" and month boundaries | `America/New_York` |
| `Database__ConnectionString` | The SQLite file | `Data Source=tideline.db` (`/data/db/tideline.db` in the container) |
| `Tideline__DataProtectionKeys` | Where the encryption keys live. Back these up with the database | beside the database (`/data/keys` in the container) |
| `Tideline__BackupPath` | Where backups are written | `backups` beside the database (`/backups` in the container) |
| `Tideline__InboxPath` * | A folder to watch for CSV exports. Empty turns the watcher off | off |
| `Tideline__InboxIntervalSeconds` * | How often the inbox is looked at | `300` |
| `Tideline__SecureCookie` | Mark the sign-in cookie Secure. Keep on, including behind HTTPS | `true` |
| `Tideline__IdleHours` * / `Tideline__AbsoluteDays` * | Sign out after this long idle, or this long in total | `12` / `14` |
| `Tideline__AuthRequestsPerMinute` * | Limit on sign-in attempts per address | `10` |
| `Tideline__MaxFailedLogins` * / `Tideline__LockoutMinutes` * | Wrong tries before a lock, and how long it lasts | `5` / `15` |
| `Notify__Enabled` | Run the nightly alert check, notifications and monthly summary | `true` |
| `SimpleFin__Enabled` | Run the automatic bank sync | `true` |
| `TIDELINE_URL` | What the app listens on (comma separated if more than one). The framework's own `ASPNETCORE_URLS` is used when this is not set | `http://0.0.0.0:8080` in the container |

* Starts from this value but can be changed in the app (Settings, Sessions and Imports). A value saved there wins until you press **Use the compose value**, and applies at once with no restart. The compose value is always shown beside it.

### In-app settings

Everything about your household is set in **Settings** inside the app, not in files: accounts and card terms, categories and tags, recurring bills, rules, imports, SimpleFIN, notification channels and what each alert does, people, sessions, API tokens, logs and backups. The app ships empty: no household data is built in.

---

## Stack

| Layer | Choice |
|---|---|
| Backend | ASP.NET Core on .NET 10, minimal APIs, Dapper, FluentMigrator |
| Database | SQLite, one file. MariaDB is planned, not built |
| Frontend | React 19 and TypeScript, built with Vite, served by the same container |
| Tests | xUnit and FluentAssertions, Vitest, Playwright, CodeQL |
| Deployment | One Docker image, built in three stages: web, server, runtime |

For the layout, data model and design decisions, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

---

## Principles

- **Bank rows are never edited or deleted.** Imported fields are written once; database triggers refuse changes.
- **No double entry.** One row per bank line. Money between your own accounts is a *move*, not a type.
- **One type per line**, so the spread always adds up.
- **Money is integer cents.** No floating point anywhere near a balance.
- **Every number drills down** to the lines behind it.
- **MFA is mandatory**, and every change is recorded in an append-only audit log.
- **Internal only.** Self-hosted, no public exposure.

---

## Documentation

| Doc | What it covers |
|---|---|
| [ARCHITECTURE](docs/ARCHITECTURE.md) | How it fits together: principles, the import pipeline, data, security, running it |
| [FEATURES](docs/FEATURES.md) | A tour of every screen and what it does |
| [DEVELOPMENT](docs/DEVELOPMENT.md) | Run from source, the command line, tests |

---

## Status

Built and tested: the ledger, every screen, security, alerts, SimpleFIN, assets and recommendations. What is still to do is checking it against real households and devices, and the production deployment. Progress is tracked in the [GitHub issues](https://github.com/Digitalcheffe/tideline/issues).

The SimpleFIN client and the webhook and email senders have been tested against stand-ins, not the live services yet.

---

## No household data in this repository

The app ships empty. A household is set up inside the app and its data arrives through imports. Tests use made-up files written for the purpose, and every screenshot here is made-up data. Real bank exports, balances, account numbers, names, and secrets (passwords, tokens, API keys, encryption keys) never go in the repository.

---

## Built On

Tideline stands on these open-source projects:

| Project | Role |
|---|---|
| [.NET](https://dotnet.microsoft.com/) and [ASP.NET Core](https://learn.microsoft.com/aspnet/core) | The server |
| [Dapper](https://github.com/DapperLib/Dapper) | Data access |
| [FluentMigrator](https://github.com/fluentmigrator/fluentmigrator) | Database migrations |
| [SQLite](https://sqlite.org/) via [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | The database |
| [QRCoder](https://github.com/codebude/QRCoder) | The authenticator QR code |
| [React](https://react.dev/) and [Vite](https://vite.dev/) | The web app and its build |
| [TanStack Query](https://tanstack.com/query) | Loading and caching data in the web app |
| [React Router](https://reactrouter.com/) | Navigation |
| [Recharts](https://recharts.org/) | Charts |
| [xUnit](https://xunit.net/), [FluentAssertions](https://fluentassertions.com/), [Vitest](https://vitest.dev/), [Playwright](https://playwright.dev/) | Tests |

---

## License

MIT. See [LICENSE](LICENSE).

Tideline is a tool for organising your own household's records. It is not financial, tax or legal advice, and the retirement and planning screens are projections to think with.
