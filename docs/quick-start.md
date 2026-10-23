---
id: DO-QS-03
title: Quick Start
subtitle: From a mapping file to organized, reconciled documents in eight steps
audience: Operators (new users)
---

# Quick Start

## 1 Quick start

This card takes you from a document mapping file to an organized, reconciled copy in eight steps. Full details for every screen are in the User Guide (DO-UG-01).

> [!IMPORTANT]
> **Three rules**
>
> **Copy only:** source documents are never moved, changed or deleted. **No guessing:** anything uncertain is flagged for a person. **Everything is accounted for:** every file and every mapping record ends with a status in the reconciliation report.

### 1.1 Before you start

| You need | Example |
|---|---|
| The application running, and a user name with the Operator or Administrator role | http://localhost:5080 |
| Where the documents are today | Azure container `employee-documents`, prefix `hr/`; or S3 bucket `client-docs`; or folder `D:\HRDocs\ClientA` |
| Where the organized copy should go | Azure container `organized-documents`, prefix `client-a/`; or an S3 bucket or folder |
| The mapping file(s): one row per document, as exported from the source system. Several files can be used in one job. | `client-a-hr-log.xlsx`, `client-a-payroll-log.csv` |

### 1.2 The eight steps

| Step | Screen | What you do | Takes |
|---|---|---|---|
| 1 | New job | Pick the storage type (Azure Blob, Amazon S3 or folder) for the source and the destination, click **Check access** on both, enter a label, click **Check access and create job** | 2 min |
| 2 | Mapping files | Choose one or more mapping files and click **Upload**. Check the first rows look right. | 1 min |
| 3 | Column mapping | Columns are matched automatically, per file. Fix any shown as **Missing**. | 1 min |
| 4 | Rules | Keep the defaults unless the client asked for something else. Click **Save and start scan**. | 1 min |
| 5 | Scan | Wait. 60,000 files scan in seconds from a folder; cloud listings take longer. | 1 min+ |
| 6 | Review | Read the counts. Open each exception tab. Resolve ambiguous files. Exclude exceptions that need no follow-up, in bulk, with a note. Download the validation report. Tick the confirmation and click **Approve and start copy** (with maker-checker on: **Submit for approval**, and a different person approves). | 10 min+ |
| 7 | Processing | Watch progress. You can **Pause** and **Resume** at any time. | varies |
| 8 | Reconciliation | Download the reconciliation report (Excel) and attach it to the client's ticket. Retry failed files if any. | 2 min |

![Figure 1: Step 6, the results summary tells you what will happen before anything is copied](images/13-review-summary.jpg)

### 1.3 Default rules (step 4)

| Rule | Default | When to change |
|---|---|---|
| Year source | UploadedOn year | The client wants the year from a Year column or the file name |
| Folder template | `{EmployeeCode}/{DocumentType}/{Year}/{FileName}` | The client asked for a different structure |
| Matching | Exact, case-insensitive and normalized file names | Rarely |
| File matching several records | Flag as ambiguous | The same document belongs to several records and each needs its own copy: **Keep all** |
| Several file names in one cell | Split into one entry per file | The file names really contain commas or semicolons |
| File already at destination | Skip and report | A re-run after a partial copy: use Compare hash |
| Integrity check | SHA-256 | Very large cloud jobs: Content-MD5 is faster |
| Batch size | 5,000 | Rarely |
| Keep object metadata | On (cloud destinations) | The destination must not carry extra metadata |

### 1.4 What the statuses mean

| Status | Meaning | Your action |
|---|---|---|
| READY | One matching record, all checks passed | None, it will be copied |
| PROCESSED | Copied and verified | None |
| UNMAPPED | File with no mapping record | Ask the client or data team |
| MISSING | Mapping record with no file (including repeats of a missing record) | Check the source prefix, bucket or folder |
| AMBIGUOUS | File matches more than one record | Resolve it in step 6, or use the rule **Keep all** |
| DUPLICATE | Repeated record, or file already at destination | Read the reason |
| INVALID | Blank employee code, type or file name (including `NULL`); no year; empty file; file in an archive tier | Fix the mapping file and scan again |
| FAILED | Copy did not succeed | Click **Retry failed** |
| EXCLUDED | Exception set aside by a person, with a note | None; restore it from the Excluded tab if needed |

### 1.5 Try it safely first

1. On the dashboard click **Load demo data**.
2. On **New job**, click **Fill in local demo folders** (or a demo container or bucket button) and create the job.
3. In step 2 click **Use demo mapping file**, then continue through the steps.
4. You should see 458 ready, 7 unmapped, 3 missing, 2 ambiguous, 2 duplicate and 5 validation errors.

> [!TIP]
> **Where to get help**
>
> Installation or access problems: Installation and Administration Guide (DO-IG-02). Error messages and common questions: FAQ and Troubleshooting (DO-FAQ-04).
