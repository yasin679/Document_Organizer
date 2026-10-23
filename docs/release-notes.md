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

Version 2.1 fixes how MISSING and DUPLICATE are counted for real-world mapping files, adds **several mapping files per job**, and lets operators **exclude exceptions in bulk** with a note.

## 2 New and fixed in v2.1

### 2.1 Several file names in one cell (fix)

Some document logs list several uploaded files for one document in a single File name cell, for example `marksheet_17206.jpg,marksheet_17206.pdf`. Earlier versions looked for one file with that whole text as its name, so the record was reported MISSING and the files themselves UNMAPPED.

- Each name now becomes its own entry with its own status and destination. Record `ROW-68` becomes `ROW-68/1`, `ROW-68/2` and so on.
- Commas, semicolons and line breaks separate names; spaces around them are ignored.
- A name that is exactly the name of a file in the source (a real file name containing a comma) is kept whole, also when it sits next to other names in the cell (`report, final.pdf,x.pdf`). Folder paths in the cell work.
- A cell that only holds placeholders (`NULL,NULL`) is blank, so INVALID.
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
- Each file has **its own column mapping** in step 3, so exports with different column names, column order, extra columns, title rows, CSV delimiters and file types (`.xlsx` and `.csv`) can be combined. The scan is refused, naming the file, until every file has its required columns.
- A file without the year rule's column uses its other date column: a Year-only file under the **UploadedOn year** rule uses its Year column, and the reverse.
- Upload checks: the same content twice is refused; two files with the same name become `name (2).csv`; a failed file cancels the whole upload without leftovers.
- Every record keeps the **name of its mapping file**: shown in the results when a job has several files, in a new **Mapping File** column of both reports, and in the report summary (each file with rows and SHA-256).
- New folder-template token **`{MappingFile}`** (file name without extension) keeps each file's documents apart.
- New API endpoints: `POST /jobs/{id}/mappings` (add files) and `DELETE /jobs/{id}/mappings/{mappingId}`; `POST /jobs/{id}/mapping` now replaces all files with the uploaded file(s).

### 2.5 Excluding exceptions in bulk (new)

Exceptions that need no follow-up can be set aside in bulk, with a required note, instead of staying in the exception lists.

- Works for every exception type: unmapped, missing, ambiguous, duplicate, validation errors and failed.
- Three ways to choose: **tick rows** (with a select-page box), **everything in the current view** (tab and search), or a **whole reason** from the new *Exceptions by reason* list (for example 3,230 *Blank file name* at once).
- Excluded items get the new status **EXCLUDED**. They are never copied, no longer count as exceptions, and keep their original status and reason. Both reports have four new columns at the end: Excluded From Status, Excluded By, Excluded At (UTC), Exclusion Note.
- Exclusions are remembered: a new scan excludes the same file or record again while its status is unchanged.
- **Restore** from the Excluded tab in the same three ways.
- Allowed while a job is awaiting approval, paused or completed. Excluding the last exceptions of a completed job makes it **Completed**.
- Every exclusion and restore is in the audit log with the count, statuses, filter and note. Viewers can see but not change exclusions.
- On the real-job replay, excluding all 56,019 exceptions took 1.8 seconds, restoring them 1.5 seconds.
- New API: `GET /jobs/{id}/reasons`, `POST /jobs/{id}/items/exclude`, `POST /jobs/{id}/items/restore`; `GET /jobs/{id}/items` accepts `group`.

### 2.6 Rule for a file that matches several records (new)

Step 4 has a new rule, **When a file matches several mapping records**:

- **Flag as ambiguous** (default, as before): the file is AMBIGUOUS until a person picks the record.
- **Keep all**: the file is copied once for every record that names it, each to its own destination, and each copy is its own row in the validation and reconciliation reports (so the final Excel lists every record). If two of those records would give the same destination path, one copy is kept and the other is DUPLICATE. The source file is still counted once in *Files discovered*.

The rule is shown in the approval panel, the report summary and the audit entry for the rules.

### 2.7 Time left and copy speed (new)

- **Time left** on the scan (per step: *Step 1 of 4 Reading mapping file*, and so on) and on the copy, with the expected finishing time, speed (files/s, MB/s, rows/s) and time taken. Estimates use the pace of the last two minutes and, for copies, the size of the files still to copy. Tested on 3,000 files (2.2 GB) copied to S3: the estimate shown after 3 seconds (37 seconds left) was within about 6 seconds of the actual finish.
- **Steady progress.** Results are saved every 2 seconds instead of every 250 files, so the screen no longer stands still and then jumps by 250.
- **Fewer reads.** Folder-to-folder copies with an integrity check hash the source while copying it, so each source file is read once instead of twice. Hashing reads 1 MB at a time instead of 4 KB.
- **Clearer counts** in step 7: Copied, Already at destination (skipped), Failed and Still to copy now count only what the copy step did. Before, *Skipped as duplicate* also included duplicates found during the scan.
- The running version is shown at the bottom of the side bar, and `start.cmd` / `start.sh` stop with a message when port 5080 is already in use by another copy.

### 2.8 Maker-checker (new)

- New setting **Maker-checker (second approver)**, off by default. When on, the people who prepared a job (created it, uploaded mapping files, saved rules, scanned, resolved or excluded) cannot approve it.
- A maker **submits** the job, with an optional note; it is then locked until a different operator or administrator **approves** it or **rejects** it with a reason, or the submitter (or an administrator) **withdraws** it.
- After approval, excluding or restoring items creates a **change request** that a different person approves or rejects.
- Every step is in the audit log; the report summary shows who prepared, submitted and approved the job.
- New API: `POST /jobs/{id}/submit`, `/withdraw`, `/reject`; `GET /jobs/{id}/requests`, `POST /jobs/{id}/requests/{rid}/approve`, `/reject`.

### 2.9 Fixes from live-scenario testing

- Copies interrupted by a crash or power cut left half-written `.partial-` files at the destination. They are now removed when the copy resumes.
- Folder names derived from mapping values now also avoid the Windows device names COM5–COM9 and LPT4–LPT9, and names such as `CON.tar` (the part before the first dot counts).
- Reports with more items than an Excel sheet holds (1,048,575 rows) continue on further sheets (*Reconciliation 2*, …) instead of producing a file Excel cannot open.
- Deleting old jobs by retention now also removes their exclusions and change requests.

Found by the independent checker and fixed:

- **Changes racing a submission.** A mapping upload or scan sent at the same moment as *Submit* could leave a changed job marked as submitted. Every change to a job is now handled one request at a time, and any return to Draft or a new scan clears the submission.
- **Approving from an old page.** Approval now carries the version of the job the approver looked at; if it was rescanned, resubmitted or its exceptions changed in the meantime, approval is refused with "The job has changed since you opened it". The confirmation also says *1 file* instead of *1 files*.
- **Two approvals or decisions at once.** Only one wins; a second approval is no longer recorded in the audit log, and a change request can no longer be both approved and rejected.
- **Source inside the destination** (for example source `D:\Export\Docs`, destination `D:\Export`) is now refused, as the guide always said; before, copies could be written into the source folder. The same applies to cloud prefixes.
- **Employee code or type of only dots** (`.`, `..`) is INVALID instead of silently putting files one folder higher.
- **Duplicate change requests** for items already in a pending request are refused.
- **Saving one setting** through the API no longer resets the others to their defaults.
- **Search** treats `%` and `_` literally.
- **Sessions** end on the server when you sign out, change your password or an administrator resets it.
- **Sign-in lockout** is per user and computer, so someone typing wrong passwords elsewhere cannot lock a real user out.
- **Job creators** always count as makers, also for jobs created before this version.

Found by the independent checker in its second round and fixed:

- **Two jobs writing the same destination file at the same moment** (same destination folder, same file names) could, on Linux and Docker, both report the file as copied while one copy replaced the other. The final step of each copy is now protected per destination path: one job copies the file and the other reports it as already at the destination.
- **Approval through the API** must include the job version when maker-checker is on (the screen always sends it).
- **A slow upload** no longer holds up other people working on the same job; an upload is received completely before the job is changed, and a change that would wait more than a minute is refused with "This job is busy".
- **A blank value for a folder-template token** (for example `{ModuleCode}` with an empty or `-` module) is INVALID instead of silently removing a folder level.
- **Unknown setting names** are refused instead of ignored.
- **Source and destination checks** use each folder's real location (folder links are followed) and respect upper/lower case where the file system does (Linux); cloud connection names are compared without regard to case.
- **Restarting with the same `DOCORG_ADMIN_PASSWORD`** no longer signs the administrator out or undoes a password changed in the application; the variable is applied only when its value changes (the console says when it was not applied).

Found by the independent checker in its third round and fixed:

- **Two jobs copying the same keys into the same S3 destination at the same moment** could overwrite each other (the earlier fix covered folders only). Copying to any one destination file is now done one at a time across all jobs, for every storage type.
- **Rename and Compare-hash rules with two jobs at once** could skip a document instead of renaming it; the next free name is now used.
- **Items with a blank template value** each had their own reason in *Exceptions by reason*; they now share one reason (*Blank value used in the folder template*) so they can be excluded together.
- **A template token whose column is not mapped** (for example `{EmployeeName}`) is refused when the rules are saved, instead of making every item INVALID after the scan.
- **Source and destination checks** now follow folder links at any level of the path.
- **Two jobs created at the same moment** could get the same job number and the second failed with an error (found by the maker's tests this round).

Found by the independent checker in its fourth round (nothing of medium or higher severity remained) and fixed:

- **Overlapping S3 destinations** written by two jobs at once (for example prefix `docs/` with employee folders and prefix `docs/E001/`) could still overwrite each other when the integrity check was off. The copy lock now uses the final object path, so overlapping destinations share it.

### 2.10 Other changes

- Matching uses an internal sequence number, so repeated record IDs (within or across mapping files) no longer affect matching.
- Resolving an ambiguous file identifies the record by ID and mapping file.
- CSV files saved by Excel on Windows (Windows-1252) are read correctly; earlier, accented file names in them were not found.
- In an Excel workbook, the first visible sheet with a header row is used, so a cover or summary sheet in front is fine.
- Adding a second file to a job created in v2.0 keeps that job's hand-edited column mapping.
- Saving the rules without per-file columns (v2.0 clients and scripts) no longer resets other files' columns.
- Resolving an ambiguous file to a record whose ID also exists in another mapping file no longer marks it DUPLICATE by mistake.
- A browser icon was added (removes a 404 on every page load).
- New automated tests `tests/e2e-mapping.mjs` and `tests/e2e-scenarios.mjs`, both run in GitHub Actions.

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
| `tests/e2e-exclusions.mjs`: exclude ticked rows, a whole reason and a searched view; note required; ready items refused; roles; remembered after a new scan (and not when the status changed); restore; completed job becomes Completed; report columns; audit | 28 checks pass |
| Browser test of bulk exclusion: tick boxes, select count, note required, exclude a reason, exclude a view, Excluded tab, restore all | 14 checks pass, no browser errors |
| `tests/e2e-scenarios.mjs`: 20 kinds of File name cell; four mapping files with different columns, structure, encoding and date columns in one job; hand-mapping, per-file year rule, ambiguity across files with colliding IDs; upload checks, roles and locked jobs; workbook with a summary sheet; the same mapping through folder → Azure → S3 → folder; S3 jobs writing the same and overlapping destinations at once; rule for files matching several records (scenario F) | 90 checks pass |
| `tests/e2e-makerchecker.mjs`: submit, lock, withdraw, reject, approve by a non-maker, version of the reviewed job, races (submit vs upload, two approvals, approve vs reject of a request), change requests after approval, setting off | 45 checks pass |
| `tests/e2e-security.mjs`: sessions ended by sign-out, password change and reset; disabled users; partial and unknown settings; sign-in lockout per computer; CSRF header; anonymous access | 12 checks pass |
| `tests/e2e-live.mjs`: concurrent job creation; unsafe and reserved folder names, path traversal, dots-only and blank template values; Unicode (NFD/NFC), case, spaces, nested folders, empty files; source changed or removed after the scan and retry; every duplicate rule; overlapping and empty folders; UTF-16 and LF-only CSV, odd headers; two jobs at once, including the same destination with Skip and Rename; slow upload; search text; very long names; formula injection and control characters in reports; report split over several sheets | 56 checks pass |
| Crash test: the application is killed during a copy of 3,000 files (2.2 GB), restarted and resumed | 5 checks pass: Paused with a message, sessions kept, all 3,000 copied once, no half-written files, every copy identical |
| Browser test of maker-checker: setting, submit, lock, reject with a reason, approve, change request approved by a second person | 14 checks pass, no browser errors |
| **Independent checker** (maker-checker testing): a separate agent that did not write the code or the tests re-ran every suite on its own instance in each of 5 rounds and ran almost 300 checks of its own (races, crashes, disk full, 300,000-record files, upgrade of a v2.0 database, browser demo flow, security) | 30 problems found across the rounds (maker and checker), all fixed; nothing of medium or higher severity remained after round 4, confirmed in round 5. See `docs/test-evidence/DO-TE-08_Test_Evidence_v2.1.xlsx` |
| `tests/e2e.mjs`: 7 jobs across folders, Azure and S3, including cross-provider | 159 checks pass, unchanged from v2.0 |
| Upgrade: job with hand-edited columns made on v2.0, opened, rescanned and given a second file on v2.1 | 6 checks pass |
| Browser test: upload four files, tabs, hand-mapping, save, resolve, remove, same file twice | 15 checks pass, no browser errors |

## 4 Upgrading from v2.0

| Item | What happens |
|---|---|
| Configuration | No changes needed |
| Database | Columns `jobs.mappings_json` and `items.mapping_file` are added automatically at start-up |
| Existing jobs | Keep their single mapping file and results. Scanning one again applies the new rules, so its counts can change as in section 3.1. |
| Reports | Both reports have a new **Mapping File** column after Mapping Record ID, and four exclusion columns at the end. Update anything that reads columns by position. |
| Database | Also adds the `exclusions` table and four `excluded_*` / `exclusion_note` columns to `items` |
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
| 7 | A File name cell is split on commas and semicolons unless the text is a source file name. A real file name with a comma that is **not** in the source is still split (then reported MISSING per part); so is a name with more than five commas | Turn off **Several file names in one cell** in step 4 for such a mapping file |
| 8 | The year rule is chosen for the whole job; only the fallback between Uploaded on and Year differs per file | Use one job per year rule if files need different rules (for example one by file name pattern) |

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
