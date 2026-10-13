# Hatz Log Viewer

Collects Hatz.ai compliance logs (invocations **with content**) for MSP clients and scans them for PII
with Azure AI Language.

```
Hatz API ──collect──▶ Blob/local archive (raw, per client) ──scan──▶ masked findings + CSV reports
```

Two stages, run independently:

| Stage | Command | Output |
|---|---|---|
| Collect | `hatzlog collect` | One JSON record per invocation: `hatz-{tenant}/invocations/yyyy/MM/dd/{id}.json` |
| Scan | `hatzlog scan` | `hatz-reports/{tenant}/{runId}/` with `findings.jsonl`, `sessions.csv`, `summary.csv`, `run.json` |

Status: collector and scanner are built and unit-tested (59 tests) and the CLI was exercised end to end against a
local stub of the documented API. **It has not yet been run against live Hatz or Azure Language**; see
[Not yet verified](#not-yet-verified).

## Build and test

Requires the .NET 8 SDK (Rider works as-is).

```bash
dotnet build
dotnet test
```

## Setup

1. **API keys.** Hatz keys are per user. Create one per client tenant (a user with compliance access) and,
   optionally, an MSP-level key. Put each in an environment variable. Keys are never stored in config files.
2. **Config.** Copy `src/HatzLogCollector.Console/appsettings.sample.json` to `appsettings.json` (or
   `appsettings.local.json`, which is git-ignored) and edit it.
   - `Sources[]`: one entry per key. `ApiKeyRef` is the *name* of the environment variable.
     Either list `TenantIds`, or set `DiscoverTenants: true` for an MSP key (`GET /admin/tenants`).
     A source with neither is reported as a failure; the tool does not guess.
   - `Storage.Mode`: `Local` for one-off audits, `AzureBlob` for the real archive.
3. **Check access before collecting anything:**

   ```bash
   hatzlog tenants                       # lists tenants and each tenant's compliance settings
   hatzlog collect --dry-run             # lists and counts invocations, fetches and writes nothing
   ```

   `tenants` shows `content=OFF` for any tenant where Hatz content access is disabled. The collector never turns
   that on: enabling content capture for a client is a consent decision for a person to make.
4. **Collect, then scan:**

   ```bash
   hatzlog collect
   hatzlog collect --tenant tenant_abc123 --from 2026-09-01 --to 2026-09-30   # manual range, never moves the watermark
   hatzlog scan --from 2026-10-01
   ```

Exit codes: `0` ok, `1` one or more tenants/chunks failed, `2` bad arguments or config.

## How collection works

- Per tenant, in ascending windows (default 24 h). After each fully successful window the watermark advances,
  so a crash or failure loses nothing and a re-run resumes where it stopped.
- **The watermark never moves past a failed window.** Each run also re-reads 15 minutes before the watermark
  (`OverlapMinutes`) to catch late-recorded invocations.
- Archive writes are **create-only**. Re-runs and overlapping windows cannot replace an archived record.
- Content costs **one API call per invocation** (the list endpoint returns `content: null`). Parallelism is
  bounded (`MaxParallelDetailRequests`), and 429/5xx responses are retried with `Retry-After` honoured.
- Content denied for an invocation (HTTP 403) or disabled for the tenant is archived as metadata only, with
  `contentStatus` recording why, and counted in the `meta-only` column.

## How scanning works

- Hatz returns the full chat history on every invocation of a session. The scanner groups records by session and
  de-duplicates messages by message id, so a long conversation is scanned once rather than once per turn.
- Scanned text: user and assistant text, tool-call code, tool output (`IncludeToolOutput`), the session name and
  uploaded file names. File *contents* are not downloaded; only references are archived.
- Text is packed into documents of at most 5,000 characters and batched 5 per request, within the documented
  synchronous limits (5 documents/request, 5,120 characters/document). Azure bills each document as at least one
  1,000-character record, so small segments are packed together.
- **Findings contain a masked value only** (`1*********9`) plus a pointer (`BlobPath`, `MessageId`, `Origin`) to
  the original text in the raw archive. The report is not a second copy of the PII.
- Defaults you may want to change (all in `Pii` config):
  - `MinConfidence` 0.8.
  - `IgnoredCategories`: `DateTime`, `PersonType`, `Organization` (noise for business chat). Set `[]` to keep everything.
  - Severity is a heuristic (identifiers, financial accounts and credentials = High; email, phone, address, IP =
    Medium; person name or URL alone = Low). Override per category with `SeverityOverrides`.
    **Treat severity as a triage aid, not a compliance determination.**

## Security notes

The archive holds client prompts and responses, which is exactly the data you are looking for PII in.

- **Local mode** writes plaintext to disk. Use an encrypted volume and delete it when the audit is done.
- **Blob mode** (recommended for anything ongoing):
  - Private endpoint or firewall rules, no public access, Entra auth (`DefaultAzureCredential`); grant the
    runner *Storage Blob Data Contributor*.
  - **Apply a time-based immutability policy to the `hatz-{tenant}` containers.** Watermarks and reports are
    deliberately written to separate containers (`hatz-state`, `hatz-reports`) so they stay writable.
  - Set a lifecycle rule that matches the retention you have agreed with each client (CMMC, HIPAA and your own
    policy may differ). Do not keep logs longer than you need to.
- Azure AI Language: use a custom subdomain and Entra auth (`Cognitive Services User`), or a key held in an
  environment variable / Key Vault.
- Make sure collecting employee prompts is covered by your MSA and the client's acceptable-use policy (and a BAA
  for HIPAA clients) before you run it.

## Not yet verified

These are assumptions to confirm against live data; the first three are the ones that would change behaviour.

1. **Per-tenant visibility of one key.** Whether an MSP key can read every tenant's compliance data, and whether
   a client key can read its own settings (the collector treats a 403 on settings as "unknown" and carries on).
2. **Content for workflow runs.** Only chat content has been seen. Workflow step records are walked generically
   (every string field is scanned); confirm the shape and tighten `ContentTextExtractor` if needed.
3. **Rate limits.** Hatz does not document any. Start with the defaults and watch for 429s in the log.
4. **Entity offsets from Azure.** Used to attribute a hit to its message, and verified against the reported text
   before being trusted; otherwise the text is located by search. Worth spot-checking on a real run.
5. **Retention of deleted chats.** `resource_expires_at` shows soft-deleted chats are purged after a window.
   Collect often enough to capture content before it goes.

## Layout

```
src/HatzLogCollector.Core      API client, collector, storage, PII scan (no UI)
src/HatzLogCollector.Console   `hatzlog` CLI
tests/HatzLogCollector.Tests   xunit; synthetic fixtures shaped like real responses
```

Next (not built): an Azure Function (timer trigger, managed identity, Key Vault) hosting the same
`CollectionRunner` and `PiiScanService`.
