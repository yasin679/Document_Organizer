---
id: DO-UAT-06
title: UAT Test Cases
subtitle: User acceptance tests for local folders, Azure Blob Storage and Amazon S3
audience: Testers, product owner, operations, cloud and security teams
landscape: true
---

# UAT Test Cases

## 1 Introduction

### 1.1 Purpose

These test cases confirm that Document Organizer v2.1 works on your own machines, in your own hosting, and against your own Azure and AWS accounts before it is used with real documents. They cover every screen, every status, all three storage types, copying between providers, security controls and recovery after interruption.

### 1.2 How to use this document

1. Set up the test environment in section 1.3.
2. Run the cases in order. Later cases reuse jobs created earlier.
3. Write P (pass) or F (fail) in the last column. For a fail, note the Job ID and what you saw in section 12 (Defect log).
4. Sign off in section 13 when all mandatory cases pass.

### 1.3 Test environment

| Item | Value |
|---|---|
| Machine | The machine or hosting platform you will use in production |
| Application | Document Organizer v2.1, for example http://localhost:5080 or your HTTPS address |
| Users | `admin` (Administrator), `uat.operator` (Operator), `uat.viewer` (Viewer) |
| Local data | Demo data (Dashboard > **Load demo data**) |
| Azure test account | A non-production storage account with containers `uat-source` and `uat-organized` |
| Azure access | Tester's account or managed identity: Reader on `uat-source`, Contributor on `uat-organized` |
| AWS test account | A non-production account with buckets `uat-source-<suffix>` and `uat-organized-<suffix>` in your usual region |
| AWS access | An IAM user or role with the least-privilege policy from DO-IG-02 section 4.4 |
| Storage connections | One `AzureBlob` and one `S3` connection pointing at the test accounts, secrets outside Git |

> [!NOTE]
> **Expected demo counts**
>
> The demo mapping file and files are built to give exactly: 472 files discovered, 471 mapping records, 458 READY, 7 UNMAPPED, 3 MISSING, 2 AMBIGUOUS, 2 DUPLICATE, 5 INVALID (validation errors). Many cases below check these numbers.

### 1.4 Priority

| Priority | Meaning |
|---|---|
| M | Mandatory for go-live |
| H | High: fix before wide roll-out |
| L | Low: can be accepted with a workaround |

## 2 Installation and sign-in

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-01 | M | Start with admin password | Start the application with `DOCORG_ADMIN_PASSWORD` set to a 12-character password (`start.cmd` prompt, `start.sh`, or container environment) | Console or container log shows "Now listening on …"; sign-in page opens | |
| UAT-02 | M | Health check | Open `/healthz` without signing in | `{"status":"ok"}` | |
| UAT-03 | M | Admin sign-in | Sign in as `admin` with that password | Dashboard opens; audit log shows Login | |
| UAT-04 | M | Wrong password lockout | Sign in as `uat.viewer` with a wrong password 5 times | 5th attempt says the account is locked for 10 minutes | |
| UAT-05 | H | Admin password reset by restart | Stop, start again with a new `DOCORG_ADMIN_PASSWORD` | New password works; old one fails | |
| UAT-06 | H | Session timeout | Leave the app idle for 31 minutes, then click a menu | Sign-in page shown | |
| UAT-07 | M | Change own password | **Settings > My password**, enter a weak then a strong password | Weak is refused with the policy message; strong is accepted | |

## 3 Users and roles

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-08 | M | Add users | As admin, add `uat.operator` (Operator) and `uat.viewer` (Viewer) | Both listed as Active; audit shows User created | |
| UAT-09 | M | Viewer is read-only | Sign in as `uat.viewer` | No **New processing job**, **Load demo data**, **Approve** or Settings changes available | |
| UAT-10 | M | Operator cannot manage users | Sign in as `uat.operator`, open Settings | Users and roles section is not available | |
| UAT-11 | H | Disable user | As admin, disable `uat.viewer`, try to sign in | Sign-in refused; re-enable restores access | |

## 4 Job creation

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-12 | M | Demo data | As operator, Dashboard > **Load demo data** | Message confirms demo files and mapping file created | |
| UAT-13 | M | Local job | **New job** > **Fill in local demo folders**, label `UAT local`, **Check access and create job** | Job created in Draft with an ID `JOB-YYYYMMDD-NNNN`; step 2 opens | |
| UAT-14 | M | Destination inside source refused | New job with the destination folder inside the source folder | Error "Destination folder cannot be inside the source folder"; no job created | |
| UAT-15 | M | Azure check access (read) | New job, source **Azure Blob Storage**, your connection, container `uat-source`, **Check access** | Green message: list and read access confirmed | |
| UAT-16 | M | Azure check access (no rights) | Choose a container you have no role on, **Check access** | Red message explains the authorization problem | |
| UAT-17 | M | S3 check access (read) | New job, source **Amazon S3**, your connection, bucket `uat-source-<suffix>`, **Check access** | Green message: list and read access confirmed | |
| UAT-18 | M | S3 check access (write) | Destination **Amazon S3**, bucket `uat-organized-<suffix>`, prefix `uat/`, **Check access** | Green message: write access confirmed; no test file left in the bucket | |
| UAT-19 | H | S3 wrong region | Temporarily set the connection's `Region` to another region, restart, **Check access** | Red message names the bucket's real region | |
| UAT-20 | H | Connection type filter | On **New job**, switch between Azure Blob Storage and Amazon S3 | Each shows only connections of its own type; labels read Container and Bucket | |

## 5 Mapping file and column mapping

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-21 | M | Upload mapping file | In job `UAT local`, step 2 > **Use demo mapping file** | Summary shows file name, rows and header row; first rows displayed | |
| UAT-22 | H | Wrong file type | Upload a `.pdf` as the mapping file | Error "Mapping file must be .xlsx or .csv" | |
| UAT-23 | M | Auto column mapping | Continue to step 3 | All required fields mapped with a tick; sample values look right | |
| UAT-24 | H | Change a column | Set Document type to another column, then back | Sample value changes; required field shows **Missing** when unmapped | |
| UAT-25 | M | Your own mapping file | Upload a real export from your source system (or `samples/sample-mapping.csv`) in a separate draft job | Header row and columns detected; required fields mapped or easy to map | |
| UAT-25a | M | Several mapping files | In a draft job, select two exports at once (for example HR documents and payroll) and **Upload** | Both listed with their rows and fingerprints, plus a Total row | |
| UAT-25b | H | Different column names | Use a second file whose columns are named differently (for example `EmpCode`, `StoredFileName`); open step 3 and click its name | Each file has its own column mapping; required fields mapped for both | |
| UAT-25d | H | Different structure | Add an `.xlsx` with title rows above the header and extra columns, plus a semicolon `.csv` that has a Year column but no Uploaded on column | Both read; header row found below the titles; step 3 shows the note that the Year column gives the year | |
| UAT-25e | H | Same file twice | Upload a mapping file that is already in the job | Refused with "… has the same content as …"; list unchanged | |
| UAT-25c | H | Remove and add | **Remove** one file, then add it again with **Add more mapping files** | List updates; after a scan, changing the files clears the results | |

## 6 Rules and scan

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-26 | M | Default rules | Step 4: keep defaults, **Save and start scan** | Scan runs and finishes with Awaiting approval | |
| UAT-27 | M | Demo counts | Read results summary | 472 discovered, 458 ready, 7 unmapped, 3 missing, 2 ambiguous, 2 duplicate, 5 validation errors | |
| UAT-28 | M | Reference example path | Search tab **Everything** for `e00879_final` | Destination `E00879/Medical Certificate/2021/e00879_final_certificate.pdf` | |
| UAT-29 | H | Template example | In step 4, change template to `{DocumentType}/{EmployeeCode}/{FileName}` | Example path updates; revert before saving | |
| UAT-30 | H | Unknown token | Enter template `{Employee}/{FileName}` and save | Error naming the unknown token | |
| UAT-30a | M | Several files in one cell | Use a mapping file where one File name cell lists two existing files separated by a comma (for example `a.pdf,b.pdf`); scan | Two entries `ROW-n/1` and `ROW-n/2`, both READY; both copied | |
| UAT-30b | M | Placeholder file name | Put `NULL` in a File name cell; scan | That record is INVALID "Blank FileName", not MISSING | |
| UAT-30c | M | Repeat of a missing record | Two identical rows (same file, employee, type) naming a file that is not in the source; scan | Both MISSING; the second names the first; neither is DUPLICATE | |
| UAT-30d | H | `{MappingFile}` token | With two mapping files, template `{MappingFile}/{EmployeeCode}/{FileName}` | Example path and destinations start with the mapping file name | |
| UAT-31 | M | Overwrite needs admin | As operator, open duplicate rule options | Overwrite is greyed out with "Not permitted by the administrator" unless an admin enabled it | |

## 7 Review, resolve and approve

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-32 | M | Exception tabs | Click each tab: Unmapped, Missing, Ambiguous, Duplicate, Validation errors | Counts match tab labels; each row has a reason | |
| UAT-33 | M | Validation report | Download validation report (Excel and CSV) | Files open; one row per item; summary sheet present | |
| UAT-34 | M | Resolve ambiguous | Ambiguous tab > **Resolve** on one file, choose a record, add a note, **Assign** | Item becomes READY (459 ready); audit shows the resolution with the note | |
| UAT-35 | H | Leave flagged | Resolve the other ambiguous file, click **Leave flagged** | Item stays AMBIGUOUS | |
| UAT-36 | M | Approval needs confirmation | Click **Approve and start copy** without ticking the checkbox | Message "Tick the confirmation box first"; nothing starts | |
| UAT-37 | M | Approve | Tick, approve, confirm | Status Processing; audit shows Processing approved with user and time | |

## 8 Processing and recovery

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-38 | M | Copy completes | Wait for the job to finish | Completed with exceptions; 459 processed, 0 failed | |
| UAT-39 | M | Source untouched | Compare the demo source folder before and after | Same file count, names and dates | |
| UAT-40 | M | Output structure | Open the destination folder | `EmployeeCode/DocumentType/Year/FileName` folders as previewed | |
| UAT-41 | M | Pause and resume | Create a job on a larger source (thousands of files); **Pause** mid-copy, then **Resume** | Copy continues; final processed count equals ready count | |
| UAT-42 | M | Restart mid-copy | During a copy, stop the application; start it again | Job shows Paused; **Resume** completes with no duplicates at the destination | |
| UAT-43 | H | Retry failed | Remove write access to the destination mid-job, then **Retry failed** after restoring it | Only failed items are copied again | |
| UAT-44 | H | Re-run same destination | Run a new job with the same source and destination, rule Skip and report | All items DUPLICATE "Destination already exists"; nothing overwritten | |

## 9 Reconciliation and audit

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-45 | M | Reconciliation report | Step 8 > **Reconciliation Excel** | Summary lists every mapping file; item sheet has status, reason, mapping file, paths, hashes, times | |
| UAT-46 | M | Integrity evidence | In the report, check the hash columns for processed items | Source and destination hashes equal | |
| UAT-47 | M | Audit trail | Audit log, search by Job ID | Creation, mapping upload, rules, scan, resolution, approval, processing and export all listed | |

## 10 Cloud storage

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-48 | M | Azure end-to-end | Upload 50 test files to `uat-source`, prepare a matching mapping file, run a job to `uat-organized` | Same results as local; source blobs unchanged | |
| UAT-49 | M | Azure server-side copy | Check the Reason column of processed items in the reconciliation report | No "Copied by streaming" note (server-side copy used) | |
| UAT-50 | H | Azure metadata | In the Azure Portal, open a copied blob's metadata | `docorgjobid`, `docorgrecordid`, `docorgemployeecode` present | |
| UAT-51 | M | S3 end-to-end | Upload the same 50 files to `uat-source-<suffix>`, run a job to `uat-organized-<suffix>` | Same results as Azure; source objects unchanged | |
| UAT-52 | M | S3 server-side copy | Check the Reason column in the reconciliation report | No "Copied by streaming" note (CopyObject used) | |
| UAT-53 | H | S3 metadata | In the S3 console, open a copied object's metadata | `x-amz-meta-docorgjobid`, `-docorgrecordid`, `-docorgemployeecode` present | |
| UAT-54 | M | Azure to S3 | Run a job with source `uat-source` (Azure) and destination `uat-organized-<suffix>` (S3), prefix `from-azure/` | All ready files processed with "Copied by streaming"; hashes match; Azure source unchanged | |
| UAT-55 | H | S3 to Azure | Run the reverse job, prefix `from-s3/` | Same as UAT-54 in the other direction | |
| UAT-56 | H | Large file to S3 | Include one file over 20 MB in UAT-54 | Processed; S3 shows a multipart ETag (ending in `-N`); hashes match | |
| UAT-57 | M | IAM role or managed identity | On the hosting platform, use a connection without keys (`Auth: Default` on AWS, `ManagedIdentity` on Azure) and repeat UAT-15 and UAT-17 | Access confirmed; the sign-in method shown is the role or identity | |
| UAT-58 | H | Changed source | After a scan, overwrite one source object, then approve | That item FAILED with a reason saying the source changed since the scan | |
| UAT-59 | L | Archived objects | Set one Azure blob to Archive tier, or one S3 object to Glacier Flexible Retrieval, then scan | Item INVALID "archive tier"; not restored | |

## 11 Security and hosting

| ID | Pri | Test | Steps | Expected result | P/F |
|---|---|---|---|---|---|
| UAT-60 | M | No secrets on screen | Open New job, Settings and reports for cloud jobs | No account key, SAS token or secret access key visible anywhere | |
| UAT-61 | M | No secrets in Git | Run `git status` and `git diff` in the installation folder | No secrets in tracked files; they are only in environment variables or `appsettings.Production.json` | |
| UAT-62 | H | CSV formula escape | Put `=1+1` in a mapping file Description, run a job, open the CSV report in Excel | Shown as text, not calculated | |
| UAT-63 | M | Demo off in production | Set `EnableDemoData` to `false`, restart | **Load demo data** not shown | |
| UAT-64 | H | Allowed local roots | Set `AllowedLocalRoots` to one folder; create a job outside it | Path refused | |
| UAT-65 | M | HTTPS behind proxy | Hosted only: open the application through its HTTPS address with `BehindReverseProxy` true | Sign-in works; audit log shows the client's real IP address | |

## 12 Defect log

| # | UAT ID | Job ID | Description | Severity | Status |
|---|---|---|---|---|---|
| 1 | | | | | |
| 2 | | | | | |
| 3 | | | | | |
| 4 | | | | | |
| 5 | | | | | |

## 13 Sign-off

| Summary | Count |
|---|---|
| Cases run | |
| Passed | |
| Failed | |
| Mandatory cases failed | |

| Role | Name | Decision | Signature and date |
|---|---|---|---|
| Product owner | | Accept / Reject | |
| Operations lead | | Accept / Reject | |
| Cloud / Infrastructure | | Accept / Reject | |
| Information security | | Accept / Reject | |
