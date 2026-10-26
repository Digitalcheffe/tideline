# Development

How to run Tideline from source, use the command line, and run the tests. For the container, see the [README](../README.md#quick-start).

## Run it from source

Needs the .NET 10 SDK and Node 24.

```bash
npm --prefix web install
npm --prefix web run dev:demo   # first run fills data/dev/tideline.db with 5,000 made-up lines
npm --prefix web run dev:all    # the same app on whatever is in that database
```

Then open http://localhost:5173. The API restarts when C# changes and the web app reloads when TypeScript changes. The database lives in
`data/dev/` (ignored by git); set `TIDELINE_DB` to use another file. Stop with Ctrl+C.

An empty database asks for the first account on the first visit, and then for an authenticator app. To look around without signing in
(your own computer only), run `npm --prefix web run dev:test`: demo data, sign-in switched off, and a red banner so you never forget.

The period picker is at the top of the menu on the left; every number is a link to the lines behind it.

## The command line

`tideline` does the things that are easier from a terminal. In the container it is `docker exec tideline_app tideline ...`; from source it is
`dotnet run --project src/Tideline.Cli -- ...`. Run it with no arguments for the full list.

```bash
dotnet run --project src/Tideline.Cli -- migrate --db "Data Source=/path/to/tideline.db"
dotnet run --project src/Tideline.Cli -- setup my-household.json --db "Data Source=/path/to/tideline.db"
dotnet run --project src/Tideline.Cli -- import /path/to/exports --db "Data Source=/path/to/tideline.db"
dotnet run --project src/Tideline.Cli -- status --db "Data Source=/path/to/tideline.db"
```

`status` lists each account's ledger balance next to the bank's last balance; they should match to the cent. `tests/fixtures/household.json`
shows the household file format, with made-up data. Keep your own household file and exports outside the repository.

Users, when you want to add one without the web app (it prints a one-time setup link):

```bash
dotnet run --project src/Tideline.Cli -- user add --email someone@example.com --name Someone --role member --db "Data Source=data/dev/tideline.db"
dotnet run --project src/Tideline.Cli -- user link --email someone@example.com --db "Data Source=data/dev/tideline.db"        # a new link, also the way to reset a forgotten password
dotnet run --project src/Tideline.Cli -- user reset-mfa --email someone@example.com --db "Data Source=data/dev/tideline.db"   # they set up the authenticator again
```

## Made-up data

To try it with a realistic amount of data, generate made-up exports (5,000 lines, six accounts, 21 months) and load them in one go:

```bash
dotnet run --project src/Tideline.Cli -- demo ./demo-data --load --db "Data Source=/path/to/demo.db"
```

The numbers come from a seed, so the same options always give the same files. Keep the folder outside the repository.

## Backups and restore

```bash
dotnet run --project src/Tideline.Cli -- backup --db "Data Source=data/dev/tideline.db"            # dated copy plus the keys, 30 days kept, then checked
dotnet run --project src/Tideline.Cli -- backup --verify data/dev/backups/tideline-20261025.db --db "Data Source=data/dev/tideline.db"
dotnet run --project src/Tideline.Cli -- restore data/dev/backups/tideline-20261025.db --db "Data Source=restored/tideline.db"
```

A restore refuses to overwrite a database unless you add `--force`, and refuses a damaged copy. A backup holds the database and the keys that
protect secrets such as authenticator codes; keep them together.

## Tests

```bash
dotnet test                       # Core, Data, Import, Cli and Api tests
npm --prefix web test             # web unit tests
npm --prefix web run e2e          # browser tests against a throwaway database
```

Set `PW_CHANNEL=msedge` or `chrome` to use a browser you already have installed for the browser tests. CI runs all of this on Linux, and the
Docker build runs the .NET and web tests again inside the image.
