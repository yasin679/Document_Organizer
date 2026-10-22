using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentOrganizer.Domain;
using DocumentOrganizer.Engine;
using DocumentOrganizer.Infrastructure;
using DocumentOrganizer.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DocumentOrganizer.Api;

public static class Endpoints
{
    private sealed class ApiError(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }

    private static (string user, string role) Who(HttpContext c) => (c.User.Identity?.Name ?? "?", c.User.FindFirstValue(ClaimTypes.Role) ?? "?");
    private static string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();

    public static void UseApiErrors(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex) when (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Response.HasStarted)
            {
                int status = ex switch
                {
                    ApiError ae => ae.Status,
                    ArgumentException or InvalidOperationException or FormatException or InvalidDataException or JsonException => 400,
                    UnauthorizedAccessException => 403,
                    DirectoryNotFoundException or FileNotFoundException => 404,
                    StorageRequestException se => se.Status is 403 or 404 ? se.Status : 502,
                    _ => 500
                };
                if (status == 500) ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Api").LogError(ex, "Unhandled API error");
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteAsJsonAsync(new { error = status == 500 ? "Unexpected error: " + ex.Message : ex.Message });
            }
        });
    }

    public static void MapOrganizerApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---------------- auth ----------------
        api.MapPost("/auth/login", async (HttpContext ctx, LoginBody body, UserStore users, AppDb db) =>
        {
            var (u, err) = users.Authenticate(body.UserName ?? "", body.Password ?? "");
            if (u == null)
            {
                db.Audit(body.UserName, null, "Login failed", null, err, Ip(ctx));
                return Results.Json(new { error = err }, statusCode: 401);
            }
            var claims = new List<Claim> { new(ClaimTypes.Name, u.Str("user_name")!), new(ClaimTypes.Role, u.Str("role")!), new("display", u.Str("display_name") ?? "") };
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie")));
            db.Audit(u.Str("user_name"), u.Str("role"), "Login", null, null, Ip(ctx));
            return Results.Ok(new { userName = u.Str("user_name"), role = u.Str("role") });
        });

        api.MapPost("/auth/logout", async (HttpContext ctx, AppDb db) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true) { var (u, r) = Who(ctx); db.Audit(u, r, "Logout", null, null, Ip(ctx)); }
            await ctx.SignOutAsync();
            return Results.Ok();
        });

        api.MapGet("/auth/me", (HttpContext ctx, OrganizerOptions opt) =>
        {
            var (u, r) = Who(ctx);
            return Results.Ok(new { userName = u, role = r, displayName = ctx.User.FindFirstValue("display"), sessionTimeoutMinutes = opt.SessionTimeoutMinutes });
        }).RequireAuthorization("View");

        api.MapPost("/account/password", (HttpContext ctx, PasswordBody body, UserStore users, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            var row = users.Get(u)!;
            if (!Passwords.Verify(body.CurrentPassword ?? "", row.Str("password_hash")!)) throw new ApiError("Current password is incorrect.");
            if (Passwords.Weakness(body.NewPassword) is string w) throw new ApiError(w);
            users.SetPassword(u, body.NewPassword!);
            db.Audit(u, r, "Password changed", null, null, Ip(ctx));
            return Results.Ok(new { message = "Password changed." });
        }).RequireAuthorization("View");

        // ---------------- meta & dashboard ----------------
        api.MapGet("/meta", (StorageFactory sf, AppDb db, OrganizerOptions opt) =>
        {
            var s = db.LoadSettings();
            return Results.Ok(new
            {
                connections = sf.DescribeConnections(),
                localEnabled = s.LocalSourceEnabled, azureEnabled = s.AzureBlobEnabled, s3Enabled = s.S3Enabled, allowOverwrite = s.AllowOverwrite,
                allowedLocalRoots = opt.AllowedLocalRoots, demoEnabled = opt.EnableDemoData,
                fields = MappingFile.Fields.Select(f => new { field = f.Field, label = f.Label }),
                tokens = Template.Tokens
            });
        }).RequireAuthorization("View");

        api.MapGet("/dashboard", (AppDb db) =>
        {
            var totals = ItemStatus.All.ToDictionary(s => s, _ => 0L);
            long documents = 0;
            var jobs = db.Db.Query("SELECT * FROM jobs ORDER BY created_at DESC");
            foreach (var j in jobs)
            {
                documents += j.Long("discovered");
                if (j.Str("counts_json") is string cj)
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, long>>(cj)!) if (totals.ContainsKey(k)) totals[k] += v;
            }
            return Results.Ok(new { totalJobs = jobs.Count, totalDocuments = documents, totals, recent = jobs.Take(8).Select(j => JobSummary(j)) });
        }).RequireAuthorization("View");

        // ---------------- connections ----------------
        api.MapPost("/connections/test", async (TestBody body, StorageFactory sf, CancellationToken ct) =>
        {
            var store = sf.Create(body.Location ?? throw new ApiError("Location is required."));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(60));
            var msg = await store.TestAsync(body.NeedWrite, cts.Token);
            return Results.Ok(new { ok = true, message = msg });
        }).RequireAuthorization("Operate");

        // ---------------- jobs ----------------
        api.MapGet("/jobs", (AppDb db, string? status) =>
        {
            var rows = db.Db.Query("SELECT * FROM jobs ORDER BY created_at DESC");
            return Results.Ok(rows.Select(j => JobSummary(j)));
        }).RequireAuthorization("View");

        api.MapPost("/jobs", async (HttpContext ctx, CreateJobBody body, AppDb db, StorageFactory sf, CancellationToken ct) =>
        {
            var (u, r) = Who(ctx);
            var s = db.LoadSettings();
            if (body.Source == null || body.Destination == null) throw new ApiError("Source and destination are required.");
            foreach (var l in new[] { body.Source, body.Destination })
            {
                if (l.Type == "Local" && !s.LocalSourceEnabled) throw new ApiError("Local folders are disabled by the administrator.");
                if (l.Type == "AzureBlob" && !s.AzureBlobEnabled) throw new ApiError("Azure Blob Storage is disabled by the administrator.");
                if (l.Type == "S3" && !s.S3Enabled) throw new ApiError("Amazon S3 is disabled by the administrator.");
            }
            var src = sf.Create(body.Source); var dst = sf.Create(body.Destination);
            if (body.Source.Type == body.Destination.Type && src.Describe.TrimEnd('/', '\\').Equals(dst.Describe.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
                throw new ApiError("Destination must be different from the source.");
            if (body.Source.Type == "Local" && body.Destination.Type == "Local")
            {
                var a = Path.GetFullPath(body.Source.Path!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var b = Path.GetFullPath(body.Destination.Path!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (b.StartsWith(a, StringComparison.OrdinalIgnoreCase)) throw new ApiError("Destination folder cannot be inside the source folder.");
            }
            if (body.Source.Type != "Local" && body.Source.Type == body.Destination.Type && body.Source.Connection == body.Destination.Connection
                && body.Source.Container == body.Destination.Container && (body.Destination.Prefix ?? "").StartsWith(body.Source.Prefix ?? "\u0000"))
                throw new ApiError("Destination prefix cannot be inside the source prefix.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(90));
            string srcMsg, dstMsg;
            try { srcMsg = await src.TestAsync(false, cts.Token); } catch (Exception ex) { throw new ApiError("Source check failed: " + ex.Message); }
            try { dstMsg = await dst.TestAsync(true, cts.Token); } catch (Exception ex) { throw new ApiError("Destination check failed: " + ex.Message); }
            var id = db.NextJobId();
            db.Db.Execute(@"INSERT INTO jobs(id,name,source_json,dest_json,config_json,status,created_by,created_at)
                VALUES(@id,@n,@src,@dst,@cfg,@st,@by,@at)",
                ("id", id), ("n", string.IsNullOrWhiteSpace(body.Name) ? id : body.Name!.Trim()),
                ("src", AppDb.ToJson(body.Source)), ("dst", AppDb.ToJson(body.Destination)), ("cfg", AppDb.ToJson(new JobConfig())),
                ("st", JobStatus.Draft), ("by", u), ("at", DateTime.UtcNow.ToString("o")));
            db.Audit(u, r, "Job created", id, $"Source {body.Source.Describe()} · Destination {body.Destination.Describe()}", Ip(ctx));
            db.Audit(u, r, "Source connected", id, srcMsg + " " + dstMsg, Ip(ctx));
            return Results.Ok(new { id, sourceCheck = srcMsg, destinationCheck = dstMsg });
        }).RequireAuthorization("Operate");

        api.MapGet("/jobs/{id}", (string id, AppDb db, JobService js) => Results.Ok(JobDetail(Need(db, id), db, js))).RequireAuthorization("View");

        api.MapDelete("/jobs/{id}", (HttpContext ctx, string id, AppDb db, JobService js, OrganizerOptions opt) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (js.IsRunning(id)) throw new ApiError("Pause the job before deleting it.");
            if (r != Roles.Administrator && (j.Str("status") != JobStatus.Draft || j.Str("created_by") != u))
                throw new ApiError("Only administrators can delete jobs that have been scanned. Operators can delete their own draft jobs.", 403);
            db.Db.Execute("DELETE FROM items WHERE job_id=@j", ("j", id));
            db.Db.Execute("DELETE FROM jobs WHERE id=@j", ("j", id));
            try { Directory.Delete(Path.Combine(opt.DataPath, "jobs", id), true); } catch { }
            db.Audit(u, r, "Job deleted", id, null, Ip(ctx));
            return Results.Ok();
        }).RequireAuthorization("Operate");

        // Mapping files. A job can have several; their records are combined in one scan.
        // POST /mapping replaces all mapping files with the uploaded one(s); POST /mappings adds files; DELETE removes one.
        api.MapPost("/jobs/{id}/mapping", async (HttpContext ctx, string id, AppDb db, OrganizerOptions opt, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id); EnsureEditable(j, js);
            // New files get new IDs, so a refused upload never touches the files the job already has.
            var old = AppDb.Mappings(j);
            var added = await SaveUploads(ctx, j, old, opt, u, replace: true);
            var res = ApplyMappings(db, j, added, added, u, r, Ip(ctx), "Mapping uploaded");
            DeleteFiles(old);
            return Results.Ok(res);
        }).RequireAuthorization("Operate").DisableAntiforgery();

        api.MapPost("/jobs/{id}/mappings", async (HttpContext ctx, string id, AppDb db, OrganizerOptions opt, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id); EnsureEditable(j, js);
            var list = AppDb.Mappings(j);
            var added = await SaveUploads(ctx, j, list, opt, u);
            list.AddRange(added);
            return Results.Ok(ApplyMappings(db, j, list, added, u, r, Ip(ctx), added.Count == 1 ? "Mapping file added" : "Mapping files added"));
        }).RequireAuthorization("Operate").DisableAntiforgery();

        api.MapDelete("/jobs/{id}/mappings/{mid}", (HttpContext ctx, string id, string mid, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id); EnsureEditable(j, js);
            var list = AppDb.Mappings(j);
            var m = list.FirstOrDefault(x => x.Id == mid) ?? throw new ApiError("Mapping file not found.", 404);
            list.Remove(m);
            var res = ApplyMappings(db, j, list, new List<MappingSource>(), u, r, Ip(ctx), "Mapping file removed", m.OriginalName);
            try { File.Delete(m.Path); } catch { }
            return Results.Ok(res);
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/mapping/demo", (HttpContext ctx, string id, AppDb db, OrganizerOptions opt, JobService js) =>
        {
            var (u, r) = Who(ctx);
            if (!opt.EnableDemoData) throw new ApiError("Demo data is disabled.", 403);
            var j = Need(db, id); EnsureEditable(j, js);
            var demo = Path.Combine(opt.DataPath, "demo", DemoData.MappingFileName);
            if (!File.Exists(demo)) throw new ApiError("Create the demo data first (Dashboard > Load demo data).");
            var dir = Path.Combine(opt.DataPath, "jobs", id); Directory.CreateDirectory(dir);
            var old = AppDb.Mappings(j);
            var mid = "m" + NextMappingNumber(old);
            var path = Path.Combine(dir, $"mapping-{mid}.xlsx");
            File.Copy(demo, path, true);
            var m = InspectMapping(path, Path.GetFileName(demo), mid, u);
            var res = ApplyMappings(db, j, new List<MappingSource> { m }, new List<MappingSource> { m }, u, r, Ip(ctx), "Mapping uploaded");
            DeleteFiles(old);
            return Results.Ok(res);
        }).RequireAuthorization("Operate");

        api.MapPut("/jobs/{id}/config", (HttpContext ctx, string id, JobConfig cfg, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id); EnsureEditable(j, js);
            var maps = AppDb.Mappings(j);
            if (maps.Count == 0) throw new ApiError("Upload the mapping file first.");
            cfg.Template = Template.Normalize(cfg.Template ?? "");
            // Column mapping per file. A client that sends only "columns" (as v2.0 did) changes the first file;
            // files it does not mention keep their saved mapping, so different-column files are never overwritten.
            var saved = AppDb.Cfg(j.Str("config_json")).FileColumns ?? new();
            cfg.FileColumns ??= new();
            foreach (var k in cfg.FileColumns.Keys.Where(k => maps.All(m => m.Id != k)).ToList()) cfg.FileColumns.Remove(k);
            foreach (var m in maps)
                if (!cfg.FileColumns.ContainsKey(m.Id))
                    cfg.FileColumns[m.Id] = m == maps[0] ? cfg.Columns : saved.TryGetValue(m.Id, out var keep) ? keep : cfg.Columns;
            cfg.Columns = cfg.FileColumns[maps[0].Id];
            var errors = ValidateMappings(cfg, maps);
            if (cfg.DuplicateRule == "Overwrite" && !db.LoadSettings().AllowOverwrite) errors.Add("Overwrite is not permitted by the administrator.");
            if (!new[] { "UploadedOn", "YearColumn", "FileNamePattern", "PathPattern" }.Contains(cfg.YearRule)) errors.Add("Unknown year rule.");
            if (!new[] { "Skip", "Rename", "CompareHash", "Overwrite" }.Contains(cfg.DuplicateRule)) errors.Add("Unknown duplicate rule.");
            if (!new[] { "None", "Sha256", "Md5" }.Contains(cfg.Integrity)) errors.Add("Unknown integrity option.");
            if (errors.Count > 0) return Results.Json(new { error = string.Join(" ", errors), errors }, statusCode: 400);
            bool wasScanned = j.Str("status") != JobStatus.Draft;
            db.UpdateJob(id, ("config_json", AppDb.ToJson(cfg)), ("status", JobStatus.Draft), ("message", wasScanned ? "Configuration changed. Run the scan again." : null));
            if (wasScanned) { db.Db.Execute("DELETE FROM items WHERE job_id=@j", ("j", id)); db.UpdateJob(id, ("counts_json", null), ("discovered", 0)); }
            db.Audit(u, r, "Configuration saved", id,
                $"Year rule: {cfg.YearRule}{(cfg.YearRule is "FileNamePattern" or "PathPattern" ? " " + cfg.YearPattern : "")} · Year mandatory: {cfg.YearMandatory} · Template: {cfg.Template} · Duplicates: {cfg.DuplicateRule} · Integrity: {cfg.Integrity} · Batch: {cfg.BatchSize}", Ip(ctx));
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/scan", (HttpContext ctx, string id, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id); EnsureEditable(j, js);
            var maps = AppDb.Mappings(j);
            if (maps.Count == 0) throw new ApiError("Upload the mapping file first.");
            var cfg = AppDb.Cfg(j.Str("config_json"));
            var errors = ValidateMappings(cfg, maps);
            if (errors.Count > 0) throw new ApiError(string.Join(" ", errors));
            js.StartScan(id, u, r);
            return Results.Ok(new { started = true });
        }).RequireAuthorization("Operate");

        api.MapGet("/jobs/{id}/items", (string id, AppDb db, string? status, string? q, int? page, int? pageSize) =>
        {
            Need(db, id);
            int ps = Math.Clamp(pageSize ?? 50, 10, 500), pg = Math.Max(1, page ?? 1);
            var where = "job_id=@j";
            var args = new List<(string, object?)> { ("j", id) };
            if (!string.IsNullOrEmpty(status) && status != "ALL")
            {
                if (status == "EXCEPTIONS") where += " AND status NOT IN ('READY','PROCESSED')";
                else { where += " AND status=@s"; args.Add(("s", status)); }
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                where += " AND (source_key LIKE @q OR file_name LIKE @q OR record_id LIKE @q OR employee_code LIKE @q OR dest_key LIKE @q)";
                args.Add(("q", "%" + q.Trim() + "%"));
            }
            var total = Convert.ToInt64(db.Db.Scalar($"SELECT COUNT(*) FROM items WHERE {where}", args.ToArray()) ?? 0L);
            args.Add(("lim", ps)); args.Add(("off", (pg - 1) * ps));
            var rows = db.Db.Query($"SELECT * FROM items WHERE {where} ORDER BY CASE status WHEN 'AMBIGUOUS' THEN 0 WHEN 'FAILED' THEN 1 ELSE 2 END, id LIMIT @lim OFFSET @off", args.ToArray());
            return Results.Ok(new
            {
                total, page = pg, pageSize = ps,
                items = rows.Select(r => new
                {
                    id = r.Long("id"), status = r.Str("status"), reason = r.Str("reason"), sourceKey = r.Str("source_key"), fileName = r.Str("file_name"),
                    size = r.LongN("source_size"), recordId = r.Str("record_id"), employeeCode = r.Str("employee_code"), employeeName = r.Str("employee_name"),
                    documentType = r.Str("doc_type"), documentName = r.Str("doc_name"), uploadedOn = r.Str("uploaded_on"), year = r.Str("year"),
                    mappingFile = r.Str("mapping_file"), destKey = r.Str("dest_key"), sourceHash = r.Str("source_hash"), destHash = r.Str("dest_hash"), integrityMethod = r.Str("integrity_method"),
                    processedAt = r.Str("processed_at"), processedBy = r.Str("processed_by"), resolvedBy = r.Str("resolved_by"), attempts = r.Long("attempts"),
                    candidates = r.Str("candidates_json") is string cj ? JsonSerializer.Deserialize<List<Candidate>>(cj, AppDb.Json) : null
                })
            });
        }).RequireAuthorization("View");

        api.MapGet("/jobs/{id}/tree", (string id, AppDb db) =>
        {
            var j = Need(db, id);
            var dest = AppDb.Loc(j.Str("dest_json"));
            var stats = db.Db.QueryOne(@"SELECT COUNT(DISTINCT employee_code) emps, COUNT(DISTINCT doc_type) types, MIN(year) y1, MAX(year) y2, COUNT(*) n
                FROM items WHERE job_id=@j AND status IN ('READY','PROCESSED')", ("j", id))!;
            var emps = db.Db.Query("SELECT DISTINCT employee_code FROM items WHERE job_id=@j AND status IN ('READY','PROCESSED') ORDER BY employee_code LIMIT 3", ("j", id))
                .Select(r => r.Str("employee_code")).ToList();
            var sample = new List<string>();
            foreach (var e in emps)
                sample.AddRange(db.Db.Query("SELECT dest_key FROM items WHERE job_id=@j AND status IN ('READY','PROCESSED') AND employee_code=@e ORDER BY dest_key LIMIT 12", ("j", id), ("e", e))
                    .Select(r => r.Str("dest_key")!));
            var rootText = dest.Type == "Local" ? dest.Path : $"{dest.Container}/{(dest.Prefix ?? "").Trim('/')}";
            return Results.Ok(new { root = rootText, sample, employees = stats.Long("emps"), documentTypes = stats.Long("types"), firstYear = stats.Str("y1"), lastYear = stats.Str("y2"), files = stats.Long("n") });
        }).RequireAuthorization("View");

        api.MapPost("/jobs/{id}/items/{itemId:long}/resolve", (HttpContext ctx, string id, long itemId, ResolveBody body, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (j.Str("status") != JobStatus.AwaitingApproval) throw new ApiError("Ambiguous files can be resolved while the job is awaiting approval.");
            var (ok, msg) = js.Resolve(id, itemId, body.RecordId ?? "", body.Note, u, r, body.MappingFile);
            if (!ok) throw new ApiError(msg);
            return Results.Ok(new { message = msg });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/approve", (HttpContext ctx, string id, ApproveBody body, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (j.Str("status") != JobStatus.AwaitingApproval) throw new ApiError("Only a validated job awaiting approval can be approved.");
            if (!body.Acknowledge) throw new ApiError("Confirm that you have reviewed the preview and exceptions.");
            var c = db.CountByStatus(id);
            if (c[ItemStatus.Ready] == 0) throw new ApiError("There are no files ready for processing.");
            db.UpdateJob(id, ("approved_by", u), ("approved_at", DateTime.UtcNow.ToString("o")));
            db.Audit(u, r, "Processing approved", id, $"{c[ItemStatus.Ready]:N0} files · COPY", Ip(ctx));
            js.StartProcessing(id, u, r, "Processing started");
            return Results.Ok(new { started = true });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/pause", (HttpContext ctx, string id, AppDb db, JobService js) =>
        {
            Need(db, id);
            if (!js.Cancel(id)) throw new ApiError("The job is not running.");
            return Results.Ok(new { message = "Pausing after the files in progress finish." });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/resume", (HttpContext ctx, string id, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (j.Str("status") != JobStatus.Paused) throw new ApiError("Only a paused job can be resumed.");
            js.StartProcessing(id, u, r, "Processing resumed");
            return Results.Ok(new { started = true });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/retry-failed", async (HttpContext ctx, string id, AppDb db, JobService js, CancellationToken ct) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (js.IsRunning(id)) throw new ApiError("The job is running.");
            if (j.Str("status") is not (JobStatus.Completed or JobStatus.CompletedWithExceptions or JobStatus.Paused)) throw new ApiError("Retry is available after processing.");
            var (n, missing) = await js.RetryFailedAsync(id, ct);
            if (n == 0) throw new ApiError(missing > 0 ? $"None of the {missing:N0} failed files exist in the source any more." : "There are no failed files to retry.");
            db.RefreshCounts(id);
            db.Audit(u, r, "Retry", id, $"{n:N0} failed files queued again using their current source version.{(missing > 0 ? $" {missing:N0} still missing in the source." : "")}", Ip(ctx));
            js.StartProcessing(id, u, r, "Processing started (retry)");
            return Results.Ok(new { retried = n, stillMissing = missing });
        }).RequireAuthorization("Operate");

        api.MapGet("/jobs/{id}/report", async (HttpContext ctx, string id, string? kind, string? format, AppDb db, Reports reports) =>
        {
            var (u, r) = Who(ctx);
            Need(db, id);
            kind = kind == "reconciliation" ? "reconciliation" : "validation";
            format = format == "csv" ? "csv" : "xlsx";
            db.Audit(u, r, "Report exported", id, $"{kind} report ({format})", Ip(ctx));
            var fileName = $"{id}_{kind}.{format}";
            ctx.Response.ContentType = format == "csv" ? "text/csv; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            await reports.WriteAsync(id, kind, format, ctx.Response.Body);
        }).RequireAuthorization("View");

        // ---------------- audit ----------------
        api.MapGet("/audit", (AppDb db, string? q, string? jobId, int? page) =>
        {
            int pg = Math.Max(1, page ?? 1), ps = 100;
            var where = "1=1"; var args = new List<(string, object?)>();
            if (!string.IsNullOrWhiteSpace(q)) { where += " AND (event LIKE @q OR details LIKE @q OR user_name LIKE @q OR job_id LIKE @q)"; args.Add(("q", "%" + q + "%")); }
            if (!string.IsNullOrWhiteSpace(jobId)) { where += " AND job_id=@jid"; args.Add(("jid", jobId)); }
            var total = Convert.ToInt64(db.Db.Scalar($"SELECT COUNT(*) FROM audit WHERE {where}", args.ToArray()) ?? 0L);
            args.Add(("lim", ps)); args.Add(("off", (pg - 1) * ps));
            var rows = db.Db.Query($"SELECT * FROM audit WHERE {where} ORDER BY id DESC LIMIT @lim OFFSET @off", args.ToArray());
            return Results.Ok(new { total, page = pg, pageSize = ps, items = rows.Select(r => new { ts = r.Str("ts"), user = r.Str("user_name"), role = r.Str("role"), @event = r.Str("event"), jobId = r.Str("job_id"), details = r.Str("details"), ip = r.Str("ip") }) });
        }).RequireAuthorization("View");

        // ---------------- settings & users (administrator) ----------------
        api.MapGet("/settings", (AppDb db, OrganizerOptions opt) => Results.Ok(new { settings = db.LoadSettings(), sessionTimeoutMinutes = opt.SessionTimeoutMinutes, allowedLocalRoots = opt.AllowedLocalRoots }))
            .RequireAuthorization("View");

        api.MapPut("/settings", (HttpContext ctx, AppSettings s, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            var before = db.LoadSettings();
            s.MaxParallelCopies = Math.Clamp(s.MaxParallelCopies, 1, 64);
            s.JobRetentionDays = Math.Clamp(s.JobRetentionDays, 30, 3650);
            s.AuditRetentionDays = Math.Clamp(s.AuditRetentionDays, 365, 3650);
            db.SaveSettings(s);
            var changes = typeof(AppSettings).GetProperties().Where(p => !Equals(p.GetValue(before), p.GetValue(s))).Select(p => $"{p.Name}: {p.GetValue(before)} → {p.GetValue(s)}");
            db.Audit(u, r, "Settings changed", null, string.Join(" · ", changes), Ip(ctx));
            return Results.Ok(s);
        }).RequireAuthorization("Admin");

        api.MapGet("/users", (AppDb db) => Results.Ok(db.Db.Query("SELECT user_name, display_name, role, active, created_at, last_login FROM users ORDER BY user_name")
            .Select(r => new { userName = r.Str("user_name"), displayName = r.Str("display_name"), role = r.Str("role"), active = r.Long("active") == 1, createdAt = r.Str("created_at"), lastLogin = r.Str("last_login") })))
            .RequireAuthorization("Admin");

        api.MapPost("/users", (HttpContext ctx, UserBody b, UserStore users, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            if (string.IsNullOrWhiteSpace(b.UserName) || !System.Text.RegularExpressions.Regex.IsMatch(b.UserName, @"^[A-Za-z0-9._@-]{3,64}$")) throw new ApiError("User name: 3–64 letters, digits, dot, dash, underscore or @.");
            if (!Roles.All.Contains(b.Role)) throw new ApiError("Unknown role.");
            if (users.Get(b.UserName) != null) throw new ApiError("That user name already exists.");
            if (Passwords.Weakness(b.Password) is string w) throw new ApiError(w);
            users.Create(b.UserName, b.DisplayName, b.Role!, b.Password!);
            db.Audit(u, r, "User created", null, $"{b.UserName} ({b.Role})", Ip(ctx));
            return Results.Ok();
        }).RequireAuthorization("Admin");

        api.MapPut("/users/{name}", (HttpContext ctx, string name, UserBody b, UserStore users, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            var existing = users.Get(name) ?? throw new ApiError("User not found.", 404);
            if (b.Role != null && !Roles.All.Contains(b.Role)) throw new ApiError("Unknown role.");
            bool demotingSelf = name.Equals(u, StringComparison.OrdinalIgnoreCase) && ((b.Role != null && b.Role != Roles.Administrator) || b.Active == false);
            if (demotingSelf) throw new ApiError("You cannot remove your own administrator access.");
            if (b.Role != null) db.Db.Execute("UPDATE users SET role=@r WHERE user_name=@u", ("r", b.Role), ("u", name));
            if (b.Active != null) db.Db.Execute("UPDATE users SET active=@a WHERE user_name=@u", ("a", b.Active.Value ? 1 : 0), ("u", name));
            if (b.DisplayName != null) db.Db.Execute("UPDATE users SET display_name=@d WHERE user_name=@u", ("d", b.DisplayName), ("u", name));
            if (!string.IsNullOrEmpty(b.Password))
            {
                if (Passwords.Weakness(b.Password) is string w) throw new ApiError(w);
                users.SetPassword(name, b.Password);
            }
            db.Audit(u, r, "User changed", null, $"{name}: " + string.Join(", ", new[] { b.Role != null ? $"role {existing.Str("role")} → {b.Role}" : null, b.Active != null ? (b.Active.Value ? "enabled" : "disabled") : null, string.IsNullOrEmpty(b.Password) ? null : "password reset" }.Where(x => x != null)), Ip(ctx));
            return Results.Ok();
        }).RequireAuthorization("Admin");

        // ---------------- demo data ----------------
        api.MapPost("/demo", async (HttpContext ctx, DemoData demo, OrganizerOptions opt, AppDb db, CancellationToken ct) =>
        {
            var (u, r) = Who(ctx);
            if (!opt.EnableDemoData) throw new ApiError("Demo data is disabled.", 403);
            var res = await demo.CreateAsync(opt.DataPath, ct);
            db.Audit(u, r, "Demo data created", null, $"{res.Files} files, {res.Records} mapping records. {res.CloudNote}", Ip(ctx));
            return Results.Ok(res);
        }).RequireAuthorization("Operate");
    }

    // ---------------- helpers ----------------
    private static Row Need(AppDb db, string id) => db.GetJob(id) ?? throw new ApiError("Job not found.", 404);

    private static void EnsureEditable(Row j, JobService js)
    {
        if (js.IsRunning(j.Str("id")!)) throw new ApiError("The job is running.");
        if (j.Str("status") is not (JobStatus.Draft or JobStatus.AwaitingApproval))
            throw new ApiError("This job has already been approved. Create a new job to change its mapping or rules.");
    }

    private sealed record MappingInfoDto(int HeaderRow, string[] Headers, List<string[]> Preview, int DataRows);
    private static MappingInfoDto? MappingInfo(Row j) => j.Str("mapping_info_json") is string s ? JsonSerializer.Deserialize<MappingInfoDto>(s, AppDb.Json) : null;

    private static readonly string[] MappingExtensions = { ".xlsx", ".csv", ".txt" };

    /// <summary>Saves the uploaded files of a request and inspects each. IDs continue after the job's existing mapping files.</summary>
    private static int NextMappingNumber(List<MappingSource> existing) =>
        existing.Select(m => int.TryParse(m.Id.TrimStart('m'), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;

    private static void DeleteFiles(List<MappingSource> list) { foreach (var m in list) try { File.Delete(m.Path); } catch { } }

    /// <summary>Saves uploaded mapping files with IDs after the existing ones. With replace, the existing files are only used for numbering.</summary>
    private static async Task<List<MappingSource>> SaveUploads(HttpContext ctx, Row j, List<MappingSource> existing, OrganizerOptions opt, string user, bool replace = false)
    {
        var form = await ctx.Request.ReadFormAsync();
        if (form.Files.Count == 0) throw new ApiError("Choose a mapping file (.xlsx or .csv).");
        foreach (var f in form.Files)
            if (!MappingExtensions.Contains(Path.GetExtension(f.FileName).ToLowerInvariant()))
                throw new ApiError($"Mapping file must be .xlsx or .csv ({f.FileName}).");
        var id = j.Str("id")!;
        var dir = Path.Combine(opt.DataPath, "jobs", id); Directory.CreateDirectory(dir);
        int next = NextMappingNumber(existing);
        var added = new List<MappingSource>();
        try
        {
            foreach (var f in form.Files)
            {
                var mid = "m" + next++;
                var path = Path.Combine(dir, $"mapping-{mid}{Path.GetExtension(f.FileName).ToLowerInvariant()}");
                await using (var fs = File.Create(path)) await f.CopyToAsync(fs);
                MappingSource m;
                // Two files with the same name (e.g. two "export.csv") must stay distinguishable in results and reports.
                var name = Path.GetFileName(f.FileName);
                var taken = (replace ? Enumerable.Empty<MappingSource>() : existing).Concat(added).Select(x => x.OriginalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (int k = 2; taken.Contains(name); k++) name = $"{Path.GetFileNameWithoutExtension(f.FileName)} ({k}){Path.GetExtension(f.FileName)}";
                try { m = InspectMapping(path, name, mid, user); }
                catch { try { File.Delete(path); } catch { } throw; }
                added.Add(m);
                // The same file twice would turn every record into a duplicate: refuse it with a clear message.
                var same = (replace ? Enumerable.Empty<MappingSource>() : existing).Concat(added.Take(added.Count - 1)).FirstOrDefault(x => x.Sha256 == m.Sha256);
                if (same != null) throw new ApiError($"{m.OriginalName} has the same content as {same.OriginalName}, which is already added.");
            }
        }
        catch
        {
            DeleteFiles(added);
            throw;
        }
        return added;
    }

    private static MappingSource InspectMapping(string path, string originalName, string mid, string user)
    {
        MappingFile.Inspection insp;
        try { insp = MappingFile.Inspect(path); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        { throw new ApiError($"{originalName} could not be read as Excel (.xlsx) or CSV: " + ex.Message); }
        if (insp.Headers.Length == 0) throw new ApiError($"No header row found in {originalName}.");
        string sha;
        using (var fs = File.OpenRead(path)) sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        return new MappingSource
        {
            Id = mid, Path = path, OriginalName = originalName, Rows = insp.DataRows, Sha256 = sha, HeaderRow = insp.HeaderRow,
            Headers = insp.Headers, Preview = insp.Preview, UploadedBy = user, UploadedAt = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>Stores the job's mapping files, auto-maps the columns of new files, and clears any earlier scan.</summary>
    private static object ApplyMappings(AppDb db, Row j, List<MappingSource> list, List<MappingSource> added, string user, string role, string? ip, string evt, string? removedName = null)
    {
        var id = j.Str("id")!;
        var cfg = AppDb.Cfg(j.Str("config_json"));
        cfg.FileColumns ??= new();
        foreach (var k in cfg.FileColumns.Keys.Where(k => list.All(m => m.Id != k)).ToList()) cfg.FileColumns.Remove(k);
        foreach (var m in added) cfg.FileColumns[m.Id] = MappingFile.AutoMap(m.Headers);
        // A job from before v2.1 has its (possibly hand-edited) column mapping only in Columns: keep it for its file.
        foreach (var m in list)
            if (!cfg.FileColumns.ContainsKey(m.Id))
                cfg.FileColumns[m.Id] = m == list[0] && added.All(x => x.Id != m.Id) && HasAnyColumn(cfg.Columns) ? cfg.Columns : MappingFile.AutoMap(m.Headers);
        cfg.Columns = list.Count > 0 ? cfg.FileColumns[list[0].Id] : new ColumnMap();
        if (list.Count > 0 && list.All(m => cfg.ColumnsFor(m.Id).UploadedOn == null) && cfg.ColumnsFor(list[0].Id).Year != null) cfg.YearRule = "YearColumn";
        bool wasScanned = j.Str("status") != JobStatus.Draft;
        db.SaveMappings(id, list);
        db.UpdateJob(id, ("config_json", AppDb.ToJson(cfg)), ("status", JobStatus.Draft),
            ("message", wasScanned ? "Mapping files changed. Run the scan again." : null));
        if (wasScanned) { db.Db.Execute("DELETE FROM items WHERE job_id=@j", ("j", id)); db.UpdateJob(id, ("counts_json", null), ("discovered", 0)); }
        var detail = removedName != null ? removedName
            : string.Join("; ", added.Select(m => $"{m.OriginalName} · {m.Rows:N0} rows · SHA-256 {m.Sha256[..12]}…"));
        db.Audit(user, role, evt, id, detail, ip);
        var first = list.FirstOrDefault();
        return new
        {
            files = list.Select(MappingDto), rows = list.Sum(m => m.Rows),
            fileName = first?.OriginalName, headers = first?.Headers, preview = first?.Preview, headerRow = first?.HeaderRow, sha256 = first?.Sha256,
            columns = cfg.Columns, fileColumns = cfg.FileColumns
        };
    }

    private static bool HasAnyColumn(ColumnMap c) => typeof(ColumnMap).GetProperties().Any(p => p.GetValue(c) is string v && v.Length > 0);

    private static List<string> ValidateMappings(JobConfig cfg, List<MappingSource> maps)
    {
        var errors = new List<string>();
        foreach (var m in maps)
        {
            var e = MappingFile.ValidateMap(cfg.ColumnsFor(m.Id), cfg, m.Headers);
            errors.AddRange(maps.Count > 1 ? e.Select(x => x.StartsWith("Folder template") || x.StartsWith("Unknown token") || x.StartsWith("Year pattern") ? x : $"{m.OriginalName}: {x}") : e);
        }
        return errors.Distinct().ToList();
    }

    private static object MappingDto(MappingSource m) => new
    {
        id = m.Id, fileName = m.OriginalName, rows = m.Rows, sha256 = m.Sha256, uploadedBy = m.UploadedBy, uploadedAt = m.UploadedAt,
        info = new { headerRow = m.HeaderRow, headers = m.Headers, preview = m.Preview, dataRows = m.Rows }
    };

    private static object JobSummary(Row j) => new
    {
        id = j.Str("id"), name = j.Str("name"), status = j.Str("status"),
        source = AppDb.Loc(j.Str("source_json")).Describe(), destination = AppDb.Loc(j.Str("dest_json")).Describe(),
        volume = j.Long("discovered"), createdBy = j.Str("created_by"), createdAt = j.Str("created_at"),
        startedAt = j.Str("started_at"), endedAt = j.Str("ended_at"),
        counts = j.Str("counts_json") is string cj ? JsonSerializer.Deserialize<Dictionary<string, long>>(cj) : null
    };

    private static object JobDetail(Row j, AppDb db, JobService js)
    {
        var id = j.Str("id")!;
        var sp = js.ScanProgress(id);
        return new
        {
            id, name = j.Str("name"), status = j.Str("status"), message = j.Str("message"),
            source = AppDb.Loc(j.Str("source_json")), destination = AppDb.Loc(j.Str("dest_json")),
            sourceText = AppDb.Loc(j.Str("source_json")).Describe(), destinationText = AppDb.Loc(j.Str("dest_json")).Describe(),
            config = AppDb.Cfg(j.Str("config_json")),
            mapping = j.Str("mapping_file") == null ? null : new { fileName = j.Str("mapping_original_name"), rows = j.Long("mapping_rows"), sha256 = j.Str("mapping_sha256"), info = MappingInfo(j) },
            mappings = AppDb.Mappings(j).Select(MappingDto),
            createdBy = j.Str("created_by"), createdAt = j.Str("created_at"), scanStartedAt = j.Str("scan_started_at"), scanEndedAt = j.Str("scan_ended_at"),
            approvedBy = j.Str("approved_by"), approvedAt = j.Str("approved_at"), startedAt = j.Str("started_at"), endedAt = j.Str("ended_at"),
            progress = new { done = j.Long("progress_done"), total = j.Long("progress_total") },
            scan = sp == null ? null : new { phase = sp.Phase, done = sp.Done, total = sp.Total },
            running = js.IsRunning(id), discovered = j.Long("discovered"),
            counts = db.CountByStatus(id)
        };
    }

    public sealed record LoginBody(string? UserName, string? Password);
    public sealed record PasswordBody(string? CurrentPassword, string? NewPassword);
    public sealed record TestBody(Location? Location, bool NeedWrite);
    public sealed record CreateJobBody(string? Name, Location? Source, Location? Destination);
    public sealed record ResolveBody(string? RecordId, string? Note, string? MappingFile = null);
    public sealed record ApproveBody(bool Acknowledge);
    public sealed record UserBody(string? UserName, string? DisplayName, string? Role, string? Password, bool? Active);
}
