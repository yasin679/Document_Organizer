---
id: DO-RN-07
title: Release Notes v2.1
subtitle: What is new, what changed, verified results and known limitations
audience: Everyone installing, upgrading or evaluating Document Organizer
---

# Release Notes v2.1

## 1 Release summary

| Item | Value |
|---|---|
| Product | Document Organizer |
| Version | 2.1 |
| Release date | 22-Oct-2026 |
| Previous version | 2.0 (13-Oct-2026), notes from section 5 |
| Status | Ready for UAT |
| Source | https://github.com/yasin679/Document_Organizer |
| Platform | .NET 8 on Windows, Linux or macOS; Docker image for any container platform |

Document Organizer copies employee documents into a clean, consistent structure, `EmployeeCode / DocumentType / Year / FileName`, using mapping files to identify each file. It works with **Azure Blob Storage**, **Amazon S3** (and S3-compatible storage) and **local or network folders**, in any combination. It is copy-only: source documents are never moved, changed or deleted.

Version 2.1 fixes how MISSING and DUPLICATE are counted for real-world mapping files and adds **several mapping files per job**.

## 2 New and fixed in v2.1

### 2.1 Several file names in one cell (fix)

Some document logs list several uploaded files for one document in a single File name cell, for example `marksheet_17206.jpg,marksheet_17206.pdf`. Earlier versions looked for one file with that whole text as its name, so the record was reported MISSING and the files themselves UNMAPPED.

- Each name now becomes its own entry with its own status and destination. Record `ROW-68` becomes `ROW-68/1`, `ROW-68/2` and so on.
- Commas, semicolons and line breaks separate names; spaces around them are ignored.
- A cell that is exactly the name of a file in the source (a real file name containing a comma) is kept whole.
- The option **Several file names in one cell** in step 4 is on by default.

### 2.2 Placeholder values (fix)

Cells that only contain `NULL`, `N/A`, `NA`, `#N/A`, `None`, `Nil`, `-`, `--` or `(null)` (typical of database exports) are now treated as empty. A File name of `NULL` is therefore INVALID ("Blank FileName") instead of MISSING.

### 2.3 Repeated records of missing files (fix)

A repeated mapping record (same file, employee and type as an earlier record) was always reported DUPLICATE, even when the file did not exist and the earlier record was MISSING. Now:

- DUPLICATE when the earlier record found its file (the file is copied once).
- MISSING when the file is not in the source; the reason names the earlier record.

The DUPLICATE count now means "copied once already", and the MISSING count covers every record that needs follow-up.

### 2.4 Several mapping files per job (new)

- Step 2 accepts **one or more files** at once, and more can be added or removed until the job is approved.
- Records of all files are combined in one scan; a document repeated across files is reported once as DUPLICATE, naming the other file.
- Each file has **its own column mapping** in step 3, so exports with different column names can be combined.
- Every record keeps the **name of its mapping file**: shown in the results when a job has several files, in a new **Mapping File** column of both reports, and in the report summary (each file with rows and SHA-256).
- New folder-template token **`{MappingFile}`** (file name without extension) keeps each file's documents apart.
- New API endpoints: `POST /jobs/{id}/mappings` (add files) and `DELETE /jobs/{id}/mappings/{mappingId}`; `POST /jobs/{id}/mapping` now replaces all files with the uploaded file(s).

### 2.5 Other changes

- Matching uses an internal sequence number, so repeated record IDs (within or across mapping files) no longer affect matching.
- Resolving an ambiguous file identifies the record by ID and mapping file.
- New automated test `tests/e2e-mapping.mjs`, also run in GitHub Actions.

## 3 Verified results of v2.1

### 3.1 Replay of a real job

A real job's reconciliation report (51,300 mapping records, 107,799 source files in a local folder) was rebuilt with placeholder file contents and scanned with both versions. Version 2.0 reproduced the original report exactly.

| Status | v2.0 | v2.1 | Change explained |
|---|---|---|---|
| Processed | 41,296 | 55,948 | 5,840 records listed 14,657 files in one cell; they are now split and matched |
| Unmapped | 66,502 | 51,850 | The same 14,652 files now have a record |
| Missing | 8,351 | 923 | Multi-file cells are no longer one "missing" file; 1,591 `NULL` file names are now INVALID; 3 repeats of missing records added |
| Invalid | 1,639 | 3,231 | 1,592 `NULL` file names (blank) |
| Duplicate | 14 | 15 | 4 repeats of missing records left DUPLICATE; 5 files listed in two records appeared after splitting |
| Ambiguous / Failed | 0 / 0 | 0 / 0 | |

Every one of the 14,657 file names in multi-file cells exists in the source. Scan: 4.8 seconds; copy of 55,948 files: 7.8 seconds (local disk).

### 3.2 Automated tests

| Test | Result |
|---|---|
| `tests/e2e-mapping.mjs`: split cells, real comma in a file name, `NULL`, repeats of missing and found records, two mapping files with different column names, remove and add, `{MappingFile}`, report column | 24 checks pass |
| `tests/e2e.mjs`: 7 jobs across folders, Azure and S3, including cross-provider | 159 checks pass, unchanged from v2.0 |

## 4 Upgrading from v2.0

| Item | What happens |
|---|---|
| Configuration | No changes needed |
| Database | Columns `jobs.mappings_json` and `items.mapping_file` are added automatically at start-up |
| Existing jobs | Keep their single mapping file and results. Scanning one again applies the new rules, so its counts can change as in section 3.1. |
| Reports | Both reports have a new **Mapping File** column after Mapping Record ID. Update anything that reads columns by position. |
| API | `POST /jobs/{id}/mapping` accepts several files and replaces all mapping files; use `POST /jobs/{id}/mappings` to add |

## 5 New in v2.0

### 5.1 Amazon S3 and S3-compatible storage

- **Amazon S3** as a source and destination, alongside Azure Blob Storage and folders.
- Works with **S3-compatible services** (MinIO, Wasabi, Cloudflare R2 and others) through `ServiceUrl`, with path-style addressing.
- **Credentials:** access key, or the standard AWS chain: environment variables, EKS web identity (IRSA), `~/.aws/credentials` profile, ECS / EKS Pod Identity container role, EC2 instance role (IMDSv2).
- **Server-side copy** (CopyObject) between buckets on the same connection, with the source ETag as a condition.
- **Multipart upload** for files over 8 MiB; each part carries an MD5 check; failed uploads are aborted.
- **Conditional writes** (`If-None-Match: *`) so an existing object is never replaced unless Overwrite is allowed.
- Objects in **Glacier Flexible Retrieval** and **Deep Archive** are reported as INVALID (archive tier) and never restored automatically.
- A wrong bucket region is reported with the bucket's real region.
- The destination bucket is created on **Check access** if it does not exist and the credentials allow it.
- **Content-MD5** integrity checks use the S3 ETag where it equals the MD5 (single-part uploads without KMS or customer-key encryption).
- Implemented on the S3 REST API with Signature Version 4; no AWS SDK is required.

### 5.2 Copy between providers

- Source and destination can be **different storage types**: Azure to S3, S3 to Azure, either to or from a folder.
- Files are streamed, verified (SHA-256 or Content-MD5) and tagged with traceability metadata on the destination.
- Source metadata is carried across providers, with names adjusted to each provider's rules.

### 5.3 Hosting and packaging

- **Dockerfile:** small runtime image, non-root user, port 8080, data on a volume.
- **docker-compose.yml:** the application plus Azurite (Azure emulator) and MinIO (S3 emulator) for a trial without any cloud account.
- **`/healthz`** endpoint for container and load-balancer health checks.
- **`BehindReverseProxy`** setting to trust `X-Forwarded-For` / `X-Forwarded-Proto` behind HTTPS proxies and load balancers.
- **`start.sh`** for Linux and macOS, next to `start.cmd` for Windows.
- **GitHub Actions** workflow that builds the application and runs the end-to-end tests against Azurite and moto on every push.
- `.gitignore`, `.gitattributes`, `.env.example` and a sample mapping file (`samples/sample-mapping.csv`).

### 5.4 Configuration

- Storage connections are **typed** with `Provider: AzureBlob` or `Provider: S3` (inferred if omitted).
- Any connection can be marked **`UseForDemo`**; **Load demo data** then also uploads the sample files there.
- Secrets can live in `appsettings.Production.json`, which is excluded from Git, or in environment variables.

### 5.5 User interface

- **New job** offers three storage types. Only connections of the chosen type are listed, and the box is labelled Container or Bucket accordingly.
- **Settings** has a switch for Amazon S3 and shows each connection's type.
- The jobs table shows the **destination** of each job.
- Demo shortcuts are shown for every demo connection that was loaded.
- The rule "Keep blob metadata" is now **Keep object metadata** and applies to every cloud destination.

### 5.6 Documentation

All seven guides were rewritten for v2.0 and are kept in the repository under `docs/`, as Markdown (readable on GitHub) and as PDF.

## 6 Fixed in v2.0

| # | Issue | Fix |
|---|---|---|
| 1 | Copies from Azure Blob Storage to a non-Azure destination (for example a local folder) failed every file with "Source file changed after the scan" | Azure's list and properties calls return the ETag in different forms (unquoted and quoted). ETags are now stored in one form and compared without quotes. Azure-to-Azure copies were not affected. |
| 2 | Source metadata copied to Azure could be rejected if a name contained characters Azure does not allow | Metadata names are adjusted to the destination's rules |

## 7 Verified results of v2.0

| Test | Result |
|---|---|
| End-to-end automated test: 7 jobs covering folder, Azure (Azurite), S3, Azure → folder, S3 → folder, Azure → S3, S3 → Azure | 159 checks pass |
| S3 against an emulator that enforces Signature V4, about 10,000 requests | All accepted; wrong secret rejected with `SignatureDoesNotMatch` |
| 20 MB file to S3; key with spaces, accents and `+&=@#` | Multipart in 3 parts, byte-identical; exact key preserved |
| Second run into the same destination | Every item DUPLICATE; nothing overwritten |
| 66,000-row mapping file with 60,040 files (local folder) | Read, matched and validated in about 3 seconds |
| v1.0 scale test (176,000 rows, 60,000 files; restart during copy and resume) | Exactly 60,000 processed, no duplicates |

## 8 Changes that affect upgrades from v1.0

| Change | Effect | What to do |
|---|---|---|
| **SignUpID removed** | The SignUpID field on New job, the SignUpID column mapping, the `{SignUpId}` template token and the SignUpID report columns are gone. The mapping file no longer needs a tenant column. | Remove `{SignUpId}` from any saved templates. Extra columns in a mapping file are simply ignored. |
| **Metadata keys renamed** | Copied files are tagged `docorgjobid`, `docorgrecordid` and `docorgemployeecode` | Update anything that reads the old keys |
| **Connection `Provider`** | New optional setting; inferred when missing | Add `"Provider": "AzureBlob"` to existing connections for clarity |
| **Product-neutral naming** | No customer-specific branding, names or examples remain in the application or documents | None |
| **Demo data** | New fictional data set (employee codes starting with `E`); demo mapping file `demo-mapping.xlsx` | Load the demo data again |
| **Default connections** | `appsettings.json` now ships `Azurite` and `LocalS3` emulator connections | Remove them in production |

The database is upgraded automatically on start; existing jobs, users and the audit log are kept.

## 9 Known issues and limitations

| # | Item | Workaround or plan |
|---|---|---|
| 1 | Not yet tested with real Azure (Azure CLI, managed identity) or real AWS (IAM roles); automated tests use emulators | Run UAT cases DO-UAT-06 before using real documents |
| 2 | Local user accounts only | Single sign-on (OpenID Connect) on the roadmap |
| 3 | Archived objects (Azure Archive, S3 Glacier Flexible Retrieval and Deep Archive) are not restored | Restore them in the storage service, then scan again |
| 4 | Server-side copy only within one Azure storage account or one S3 connection | Other combinations stream through the application |
| 5 | One application instance per data folder | Do not run two instances against the same data volume |
| 6 | The Docker image has not been built in the release pipeline yet | Build it locally (`docker compose up --build`) and run UAT-01 and UAT-02 |
| 7 | A File name cell is split on commas and semicolons unless the whole value is a source file name. A real file name with a comma that is **not** in the source is still split (then reported MISSING per part) | Turn off **Several file names in one cell** in step 4 for such a mapping file |

## 10 Installation notes

- New installation: follow the Installation and Administration Guide (DO-IG-02).
- Requires the .NET 8 SDK (or ASP.NET Core 8 Runtime for a published build), or Docker.
- Set `EnableDemoData` to `false` and remove the emulator connections before production use.
- Keep secrets out of Git: environment variables or `appsettings.Production.json`.

## 11 Roadmap

| Item | Planned |
|---|---|
| Sign-in | Single sign-on with OpenID Connect (Microsoft Entra ID, Okta, Google) |
| Storage | Google Cloud Storage; optional restore of archived objects with approval |
| Dashboard | Filters by job label, storage type and date |
| Packaging | Published container image and release builds on GitHub |
| Intelligence | OCR and document classification, always with human approval |
