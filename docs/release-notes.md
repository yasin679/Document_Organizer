---
id: DO-RN-07
title: Release Notes v2.0
subtitle: What is new, what changed, verified results and known limitations
audience: Everyone installing, upgrading or evaluating Document Organizer
---

# Release Notes v2.0

## 1 Release summary

| Item | Value |
|---|---|
| Product | Document Organizer |
| Version | 2.0 |
| Release date | 13-Oct-2026 |
| Status | Ready for UAT |
| Source | https://github.com/yasin679/Document_Organizer |
| Platform | .NET 8 on Windows, Linux or macOS; Docker image for any container platform |

Document Organizer copies employee documents into a clean, consistent structure, `EmployeeCode / DocumentType / Year / FileName`, using a mapping file to identify each file. Version 2.0 is a general-purpose release: it works with **Azure Blob Storage**, **Amazon S3** (and S3-compatible storage) and **local or network folders**, in any combination, and can be hosted anywhere. It is copy-only: source documents are never moved, changed or deleted.

## 2 New in v2.0

### 2.1 Amazon S3 and S3-compatible storage

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

### 2.2 Copy between providers

- Source and destination can be **different storage types**: Azure to S3, S3 to Azure, either to or from a folder.
- Files are streamed, verified (SHA-256 or Content-MD5) and tagged with traceability metadata on the destination.
- Source metadata is carried across providers, with names adjusted to each provider's rules.

### 2.3 Hosting and packaging

- **Dockerfile:** small runtime image, non-root user, port 8080, data on a volume.
- **docker-compose.yml:** the application plus Azurite (Azure emulator) and MinIO (S3 emulator) for a trial without any cloud account.
- **`/healthz`** endpoint for container and load-balancer health checks.
- **`BehindReverseProxy`** setting to trust `X-Forwarded-For` / `X-Forwarded-Proto` behind HTTPS proxies and load balancers.
- **`start.sh`** for Linux and macOS, next to `start.cmd` for Windows.
- **GitHub Actions** workflow that builds the application and runs the end-to-end tests against Azurite and moto on every push.
- `.gitignore`, `.gitattributes`, `.env.example` and a sample mapping file (`samples/sample-mapping.csv`).

### 2.4 Configuration

- Storage connections are **typed** with `Provider: AzureBlob` or `Provider: S3` (inferred if omitted).
- Any connection can be marked **`UseForDemo`**; **Load demo data** then also uploads the sample files there.
- Secrets can live in `appsettings.Production.json`, which is excluded from Git, or in environment variables.

### 2.5 User interface

- **New job** offers three storage types. Only connections of the chosen type are listed, and the box is labelled Container or Bucket accordingly.
- **Settings** has a switch for Amazon S3 and shows each connection's type.
- The jobs table shows the **destination** of each job.
- Demo shortcuts are shown for every demo connection that was loaded.
- The rule "Keep blob metadata" is now **Keep object metadata** and applies to every cloud destination.

### 2.6 Documentation

All seven guides were rewritten for v2.0 and are kept in the repository under `docs/`, as Markdown (readable on GitHub) and as PDF.

## 3 Fixed in v2.0

| # | Issue | Fix |
|---|---|---|
| 1 | Copies from Azure Blob Storage to a non-Azure destination (for example a local folder) failed every file with "Source file changed after the scan" | Azure's list and properties calls return the ETag in different forms (unquoted and quoted). ETags are now stored in one form and compared without quotes. Azure-to-Azure copies were not affected. |
| 2 | Source metadata copied to Azure could be rejected if a name contained characters Azure does not allow | Metadata names are adjusted to the destination's rules |

## 4 Verified results

| Test | Result |
|---|---|
| End-to-end automated test: 7 jobs covering folder, Azure (Azurite), S3, Azure → folder, S3 → folder, Azure → S3, S3 → Azure | 159 checks pass |
| S3 against an emulator that enforces Signature V4, about 10,000 requests | All accepted; wrong secret rejected with `SignatureDoesNotMatch` |
| 20 MB file to S3; key with spaces, accents and `+&=@#` | Multipart in 3 parts, byte-identical; exact key preserved |
| Second run into the same destination | Every item DUPLICATE; nothing overwritten |
| 66,000-row mapping file with 60,040 files (local folder) | Read, matched and validated in about 3 seconds |
| v1.0 scale test (176,000 rows, 60,000 files; restart during copy and resume) | Exactly 60,000 processed, no duplicates |

## 5 Changes that affect upgrades from v1.0

| Change | Effect | What to do |
|---|---|---|
| **SignUpID removed** | The SignUpID field on New job, the SignUpID column mapping, the `{SignUpId}` template token and the SignUpID report columns are gone. The mapping file no longer needs a tenant column. | Remove `{SignUpId}` from any saved templates. Extra columns in a mapping file are simply ignored. |
| **Metadata keys renamed** | Copied files are tagged `docorgjobid`, `docorgrecordid` and `docorgemployeecode` | Update anything that reads the old keys |
| **Connection `Provider`** | New optional setting; inferred when missing | Add `"Provider": "AzureBlob"` to existing connections for clarity |
| **Product-neutral naming** | No customer-specific branding, names or examples remain in the application or documents | None |
| **Demo data** | New fictional data set (employee codes starting with `E`); demo mapping file `demo-mapping.xlsx` | Load the demo data again |
| **Default connections** | `appsettings.json` now ships `Azurite` and `LocalS3` emulator connections | Remove them in production |

The database is upgraded automatically on start; existing jobs, users and the audit log are kept.

## 6 Known issues and limitations

| # | Item | Workaround or plan |
|---|---|---|
| 1 | Not yet tested with real Azure (Azure CLI, managed identity) or real AWS (IAM roles); automated tests use emulators | Run UAT cases DO-UAT-06 before using real documents |
| 2 | Local user accounts only | Single sign-on (OpenID Connect) on the roadmap |
| 3 | Archived objects (Azure Archive, S3 Glacier Flexible Retrieval and Deep Archive) are not restored | Restore them in the storage service, then scan again |
| 4 | Server-side copy only within one Azure storage account or one S3 connection | Other combinations stream through the application |
| 5 | One application instance per data folder | Do not run two instances against the same data volume |
| 6 | The Docker image has not been built in the release pipeline yet | Build it locally (`docker compose up --build`) and run UAT-01 and UAT-02 |

## 7 Installation notes

- New installation: follow the Installation and Administration Guide (DO-IG-02).
- Requires the .NET 8 SDK (or ASP.NET Core 8 Runtime for a published build), or Docker.
- Set `EnableDemoData` to `false` and remove the emulator connections before production use.
- Keep secrets out of Git: environment variables or `appsettings.Production.json`.

## 8 Roadmap

| Item | Planned |
|---|---|
| Sign-in | Single sign-on with OpenID Connect (Microsoft Entra ID, Okta, Google) |
| Storage | Google Cloud Storage; optional restore of archived objects with approval |
| Dashboard | Filters by job label, storage type and date |
| Packaging | Published container image and release builds on GitHub |
| Intelligence | OCR and document classification, always with human approval |
