# Arrowgene.DJMaxOnline

A private server for the Korean build of DJMAX Online, plus the launcher players use to log
in and stay patched.

The server ships with no game content. Everything it serves (the song list, the shop, the
courses, the charts) is read out of your own client. Importing that data is the first thing
you do, and the rest of this file is mostly about it.

Nothing derived from a client is supplied via the repo: no keys, no `.pak` archives, no
`.pt` charts, no catalogs. You provide all of it from your own copy of the game.

## Requirements

- .NET 8 SDK
- A Korean DJMAX Online client

```bash
dotnet build Arrowgene.DJMaxOnline.sln
```

## Projects

| project | what it is |
|---|---|
| `Arrowgene.DJMaxOnline.Server` | the game server: protocol, rooms, database, web and login endpoints |
| `Arrowgene.DJMaxOnline.CLI` | how you run the server, and the import and diagnostic commands |
| `Arrowgene.DJMaxOnline.Launcher` | what players run: logs in, patches the game, starts it |
| `Arrowgene.DJMaxOnline.Updater` | the patch download and verify half of the launcher |
| `Arrowgene.DJMaxOnline.Test` | tests |

## Runtime folders

None of these are supplied via the repo. Create them as you go; all are gitignored.

| folder | holds |
|---|---|
| `DATA/` | the imported catalogs, the key files, and the player database |
| `Patterns/` | plaintext charts (`.pt`, `.sr`) |
| `songs/` | packed song archives served to the client |
| `patch/` | what the launcher installs, plus the checksum list and news file |

---

## Importing data from a client

### 1. The pak keys

Client paks are encrypted, and none of the keys are supplied via the repo. They are client
data, so you dump them from your own copy of the game.

Name them and drop them in `DATA/xipkeys`:

| file | what it is |
|---|---|
| `1.key` | modulus table of the block cipher, 256 x uint64 (2048 bytes) |
| `2.key` | exponent table, 256 x uint64 (2048 bytes) |
| `xor.key` | rolling mask over each file header (256 bytes) |

All three are needed. `DATA/xipkeys` is found automatically, or pass `--keys DIR` to point
somewhere else. Keys are only needed while importing; the running server never touches
them. `*.key` is gitignored so they cannot be committed by accident.

### 2. Import

Point the importer at the client's `system.pak`:

```bash
dotnet run --project Arrowgene.DJMaxOnline.CLI -- --import-data "C:\DJMax\system.pak"
```

Options:

| flag | meaning |
|---|---|
| `--data DIR` | where to write (default `DATA`) |
| `--keys DIR` | where the key files live |
| `--dry-run` | list what would be written, write nothing |

Patch volumes are layered exactly the way the client layers them: `system.pak`, then
`system_0001.pak`, `system_0002.pak` and so on in numeric order, each overriding the last.
So you always point at the base pak and get the newest copy of every file. The pak chain
used is printed at the top of the report.

What comes out:

- `DiscStock.csv`, the song catalog
- `ItemStock.csv`, `ItemSetInfo.csv` and the `Goods_*.lst` lists, the shop and equipment
  catalogs
- `IconSet*.csv`, the icons
- `CourseSection.ini`, `CourseGeneral.ini`, the course scripts

The player database (`djmax.sqlite3`) is never overwritten by an import.

### 3. Check the song catalog

```bash
dotnet run --project Arrowgene.DJMaxOnline.CLI -- --verify-song-catalog DATA/DiscStock.csv
```

`DATA/DiscStock.csv` must agree with the client's `Song\DiscStock.csv`. The chart scramble
key is derived from the disc id and difficulty, so if the two lists disagree the server
hands out a chart encrypted with the wrong key and the client crashes decoding it.
Re-import whenever you change the client's copy.

### 4. Charts and songs

Charts and song archives are not in `system.pak`. They live in the per-song paks in the
game folder, and like everything else here they are not supplied via the repo.

- plaintext charts (`.pt`, `.sr`) go in `Patterns/`, and the server builds game-info
  payloads from them on the fly
- packed song archives go in `songs/` and are served as `/song/<tag>.pak`

A pak's checksums are recorded in `crc.pak` next to the client, not next to the server, so
that manifest has to be rebuilt whenever a pak changes or the client rejects it.

---

## Running the server

```bash
dotnet run --project Arrowgene.DJMaxOnline.CLI
```

Settings are read from `settings.ini` beside the executable, which is written with
commented defaults on first run. CLI flags and environment variables override it. The main
knobs:

| setting | default | what it does |
|---|---|---|
| `ListenIpAddress` / `ServerPort` | `127.0.0.1` / `3000` | the game server (7-key channel on `SevenKeyServerPort`) |
| `AdvertisedIpAddress` | `127.0.0.1` | the address handed to clients, so set this to what players can reach |
| `HttpContentPort` | `8080` | serves `/song/` and `/patch/` |
| `LoginApiPort` | `8091` | HTTP login for remote launchers |
| `StatusApiPort` | `8090` | read-only status feed. Local only, do not expose it |

The server console takes commands while it runs. `/help` lists them; the useful ones:

```
/account create <account-id> <nickname> [male|female]
/account password <account-id|nickname|user-id>
/accounts          /online
/say /notice /alert /bignews <message>
/stop
```

Passwords are typed at a prompt, never on the command line, and are stored salted and
hashed per account.

## Launcher

`Arrowgene.DJMaxOnline.Launcher` reads `launcher.cfg` beside itself. It logs in over HTTP
(`loginUrl`), so the same build works against a server on this machine and a remote one and
only the host changes. It then patches from `updateUrl`, verifying every file against the
`md5list.txt` the web server publishes. `news.txt` at the same address is shown on the
launcher.

The client reads its text in CP949, so on non-Korean Windows it needs a locale emulator.
`launcher.cfg` documents how to point at one.

## Content delivery

One web server, two routes:

- `/song/` for song archives the client downloads in-game, not checksummed by the launcher
- `/patch/` for game updates the launcher installs, covered by `md5list.txt`

## Tests

```bash
dotnet test Arrowgene.DJMaxOnline.sln
```

Tests that need client data skip themselves when it is absent, so a checkout without an
imported client still runs green.
