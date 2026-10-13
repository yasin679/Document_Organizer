---
id: DO-FAQ-04
title: FAQ and Troubleshooting
subtitle: Answers to common questions and fixes for common problems
audience: Operators, administrators and support teams
---

# FAQ and Troubleshooting

## 1 Frequently asked questions

### 1.1 General

**Does the application ever move or delete the original documents?**

No. Document Organizer only reads the source and writes a copy to the destination. The source container, bucket or folder needs read and list access only. The source stays exactly as it was, so a job can always be repeated.

**Is this a recovery tool or an organizer?**

It is an organizer. It does not restore deleted files. It takes documents that exist today, uses a mapping file to identify each one, and copies them into a clean structure: `EmployeeCode / DocumentType / Year / FileName`.

**Which storage does it work with?**

Azure Blob Storage, Amazon S3, S3-compatible services (MinIO, Wasabi, Cloudflare R2 and others), and local or network folders. Source and destination can be different types, so a job can also migrate documents, for example from Azure to S3.

**Are documents sent anywhere else?**

No. Documents go only from the source you choose to the destination you choose. When both are in the same Azure storage account, or use the same S3 connection, the storage service copies them server-side and the content never passes through the application server. Nothing is sent to any external service.

**What does the mapping file need to contain?**

One row per document with at least an employee code, a document type and a file name, plus an upload date or a year. Column names are matched automatically and can be changed in step 3. See `samples/sample-mapping.csv` in the repository.

**Who can do what?**

Viewers can see everything and download reports. Operators can also create and run jobs. Administrators can also change settings and manage users.

### 1.2 Running jobs

**How long does a job take?**

In testing, a mapping file of 66,000 rows with 60,000 files in a local folder was read and scanned in about 3 seconds. Copy speed depends on the storage: server-side copies inside one Azure account or one S3 connection are fastest; streaming between providers depends on the network. Cloud listings take longer for large containers (5,000 blobs per page on Azure, 1,000 objects per page on S3).

**Can I close the browser while a job runs?**

Yes. Jobs run inside the application, not in the browser. You can close the tab and come back later. Do not stop the application itself (the console window, service or container).

**What happens if the server restarts in the middle of a copy?**

The job is shown as **Paused**. Open it and click **Resume**. Files already copied are recognised by comparing their contents and are not copied again.

**Can I change the rules after scanning?**

Yes, until the job is approved. Go back to step 3 or 4 and save. The scan results are cleared and you scan again. After approval the rules are locked so each job's report always matches the rules used.

**What if a file is already at the destination?**

It depends on the duplicate rule you chose in step 4. **Skip and report** (default) leaves it alone and marks it DUPLICATE. **Rename with a suffix** writes `name_dup1.pdf`. **Compare hash** skips it if identical, otherwise renames. **Overwrite** replaces it, but only if an administrator has enabled overwrite in Settings.

**How is the year worked out?**

By default, from the UploadedOn date in the mapping file. You can instead use a Year column, a pattern in the file name, or a pattern in the folder, blob or object path. If no valid year is found, the record is INVALID and you decide what to do with it. The application never invents a year.

**Why are some files AMBIGUOUS?**

Two or more mapping records name the same file, for example `offer_letter.pdf` for two different employees. The application cannot know which is correct, so a person must choose in the Resolve panel. The choice and note go to the audit log.

**The Missing count is very large. Are files lost?**

Usually not. A large Missing count most often means the source prefix, bucket or folder was too narrow, so the scan did not see where those files are. Check the source location before reporting anything to the client.

**Can I run two jobs at the same time?**

Yes. Avoid two jobs that write to the same destination prefix.

**Can a job move documents from Azure to S3?**

Yes. Choose Azure Blob Storage as the source and Amazon S3 as the destination. Each file is streamed from Azure to S3 (multipart for files over 8 MiB), verified, and tagged with the job ID, record ID and employee code. The Azure source is not changed.

### 1.3 Reports and audit

**Which report do I send to the client?**

The reconciliation report (Excel), downloaded from step 8. It has a summary sheet and one row per file and record with status, reason, source, destination, hashes and time copied.

**How long is history kept?**

Jobs and reports are kept for 365 days and the audit log for 7 years by default. Administrators can change both under Settings.

**Can audit entries be edited or deleted?**

No. There is no screen or API to change the audit log.

## 2 Troubleshooting

### 2.1 Starting and signing in

| Problem | Likely cause | What to do |
|---|---|---|
| "dotnet is not recognized" or ".NET 8 is not installed" | .NET 8 is not installed, or the window was opened before installing | Install the .NET 8 SDK and open a new terminal |
| "SQLite library not found" (Linux) | The SQLite system library is missing | Install `libsqlite3-0` (Debian/Ubuntu) or `sqlite-libs` (RHEL/Fedora), or use the Docker image |
| Browser shows "This site can't be reached" | The application is not running, or the console window was closed | Start it again and keep it running |
| "address already in use" for port 5080 | Another copy is already running | Stop the other copy, or change `Urls` |
| Admin password not accepted | Password was not set at start, or was shorter than 8 characters | Start again with `DOCORG_ADMIN_PASSWORD` set (or type it at the `start.cmd` prompt) |
| No temporary password appears | A password already exists in the database | Set a new one with `DOCORG_ADMIN_PASSWORD` at start-up |
| "The account is locked for 10 minutes" | 5 wrong passwords | Wait 10 minutes, or ask an administrator to reset the password |
| Signed out unexpectedly | 30 minutes without activity | Sign in again. Running jobs are not affected. |
| Signed out at once behind a load balancer, or every audit entry shows the proxy's address | Reverse proxy headers not trusted | Set `DocumentOrganizer__BehindReverseProxy=true` |

### 2.2 Azure Blob Storage

| Message or problem | Likely cause | What to do |
|---|---|---|
| `AuthorizationPermissionMismatch` | Your account lacks the Azure role on the container | Ask for Storage Blob Data Reader (source) or Contributor (destination). Wait up to 10 minutes after it is granted. |
| `AuthenticationFailed` | SAS token expired or wrong, or account key wrong | Get a new token or key and update the configuration |
| "Please run 'az login'" | Azure CLI sign-in has expired | Run `az login` and click **Check access** again |
| "No managed identity endpoint is reachable" | `ManagedIdentity` used outside Azure | Use `AzureCli` or `Sas` on a laptop |
| `ContainerNotFound` | Container name typed wrong | Check the name in the Azure Portal. Names are lower-case. |
| Azurite connection refused | Azurite is not running | Start `azurite-blob` in a separate terminal |

### 2.3 Amazon S3 and S3-compatible storage

| Message or problem | Likely cause | What to do |
|---|---|---|
| `NoSuchBucket` | Bucket name typed wrong, or the bucket is in another account | Check the name. Bucket names are lower-case. |
| "The bucket is in region 'X', but the connection uses 'Y'" | `Region` on the connection is wrong | Set `Region` to the region shown |
| `InvalidAccessKeyId` | The access key does not exist or was deactivated | Create a new access key and update the configuration |
| `SignatureDoesNotMatch` | The secret access key is wrong, or the server clock is far off | Check `SecretAccessKey`; make sure the server time is correct |
| `AccessDenied` on Check access | The IAM policy does not allow the action | Apply the policy in DO-IG-02 section 4.4 |
| Destination items FAILED with `AccessDenied` / 403 on files that do not exist yet | `s3:ListBucket` missing on the destination, so S3 answers "denied" instead of "not found" | Add `s3:ListBucket` on the destination bucket |
| "No AWS credentials found" | `Auth` is `Default` and none of the credential sources is available | Set `AccessKeyId` and `SecretAccessKey`, set `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`, add a profile to `~/.aws/credentials`, or attach an IAM role |
| Connection refused to `localhost:9000` | The MinIO or moto emulator is not running | Start it, or remove the `LocalS3` connection |
| Wrong host or TLS error with an S3-compatible service | Virtual-hosted addresses not supported by that service | Set `ForcePathStyle` to `true` (the default when `ServiceUrl` is set) |
| INVALID "archive tier" for S3 objects | Objects are in Glacier Flexible Retrieval or Deep Archive | Restore them (S3 console or `aws s3api restore-object`), then scan again |

### 2.4 Jobs and locations

| Message or problem | Likely cause | What to do |
|---|---|---|
| "Destination must be different from the source", "Destination folder cannot be inside the source folder" or "Destination prefix cannot be inside the source prefix" | Destination is the same as, or inside, the source | Choose a different container, bucket, prefix or folder |
| "Connection 'X' is not an Amazon S3 connection" (or "…not an Azure Blob Storage connection") | The connection's `Provider` does not match the storage type chosen | Pick a connection of the right type, or correct `Provider` in the configuration |
| Path refused for local folder | Folder is outside `AllowedLocalRoots` | Ask an administrator to add the root, or use an allowed folder |
| A storage type is greyed out on the New job page | Turned off in Settings, or no connection of that type is configured | Ask an administrator |
| Many FAILED with HTTP 503 or `SlowDown` | The storage service is throttling | Lower **Parallel copies** in Settings, then **Retry failed** |

### 2.5 Mapping file and matching

| Problem | Likely cause | What to do |
|---|---|---|
| "No header row found in the file" or "could not be read as Excel" | The file has no column names in its first 20 rows, or is not a real `.xlsx`/`.csv` | Export the file again without changes |
| Required field shows **Missing** in column mapping | The column has a different name in this file | Pick the right column from the list |
| Almost everything UNMAPPED | Wrong mapping file for this source, or the File name column is mapped wrongly | Check the file and the File name mapping |
| Almost everything MISSING | Source prefix, bucket or folder is wrong | Check the source location in step 1 |
| Almost everything DUPLICATE | Destination already holds an earlier copy | Use a new destination prefix, or the Compare hash rule |
| Many INVALID "year could not be determined" | UploadedOn is blank or not a date | Use the Year column or a file-name pattern in step 4 |
| INVALID "archive tier" | Azure Archive tier or S3 Glacier / Deep Archive | Rehydrate or restore the files, then scan again |

> [!TIP]
> **Still stuck?**
>
> Note the Job ID (for example `JOB-20261013-0001`), the step, and the exact message. The audit log for the job shows every action taken on it. Bugs and questions can be raised as issues on the GitHub repository.
