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
        // Changes to one job are handled one at a time (submit, approve, upload, scan, exclude, ...), so a check such as
        // "not submitted" or "awaiting approval" cannot be overtaken by another request between the check and the change.
        api.AddEndpointFilter(async (fctx, next) =>
        {
            var http = fctx.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || !http.Request.Path.StartsWithSegments("/api/jobs")
                || http.Request.RouteValues["id"] is not string jobId) return await next(fctx);
            // Read an uploaded file completely before waiting for the job, so a slow upload does not hold up other people
            if (http.Request.HasFormContentType) await http.Request.ReadFormAsync(http.RequestAborted);
            var gate = JobGates.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(60), http.RequestAborted))
                return Results.Json(new { error = "This job is busy with another change. Try again in a moment." }, statusCode: 409);
            try { return await next(fctx); }
            finally { gate.Release(); }
        });

        // ---------------- auth ----------------
        api.MapPost("/auth/login", async (HttpContext ctx, LoginBody body, UserStore users, AppDb db) =>
        {
            var (u, err) = users.Authenticate(body.UserName ?? "", body.Password ?? "", Ip(ctx));
            if (u == null)
            {
                db.Audit(body.UserName, null, "Login failed", null, err, Ip(ctx));
                return Results.Json(new { error = err }, statusCode: 401);
            }
            var claims = new List<Claim> { new(ClaimTypes.Name, u.Str("user_name")!), new(ClaimTypes.Role, u.Str("role")!), new("display", u.Str("display_name") ?? ""),
                new("sid", users.StartSession(u.Str("user_name")!)) };
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie")));
            db.Audit(u.Str("user_name"), u.Str("role"), "Login", null, null, Ip(ctx));
            return Results.Ok(new { userName = u.Str("user_name"), role = u.Str("role") });
        });

        api.MapPost("/auth/logout", async (HttpContext ctx, AppDb db, UserStore users) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true) { var (u, r) = Who(ctx); users.EndSession(ctx.User.FindFirstValue("sid")); db.Audit(u, r, "Logout", null, null, Ip(ctx)); }
            await ctx.SignOutAsync();
            return Results.Ok();
        });

        api.MapGet("/auth/me", (HttpContext ctx, OrganizerOptions opt) =>
        {
            var (u, r) = Who(ctx);
            return Results.Ok(new { userName = u, role = r, displayName = ctx.User.FindFirstValue("display"), sessionTimeoutMinutes = opt.SessionTimeoutMinutes });
        }).RequireAuthorization("View");

        api.MapPost("/account/password", async (HttpContext ctx, PasswordBody body, UserStore users, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            var row = users.Get(u)!;
            if (!Passwords.Verify(body.CurrentPassword ?? "", row.Str("password_hash")!)) throw new ApiError("Current password is incorrect.");
            if (Passwords.Weakness(body.NewPassword) is string w) throw new ApiError(w);
            users.SetPassword(u, body.NewPassword!);   // ends every session of this user, on every computer
            // keep this browser signed in with a new session
            var claims = ctx.User.Claims.Where(c => c.Type != "sid").Append(new Claim("sid", users.StartSession(u))).ToList();
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie")));
            db.Audit(u, r, "Password changed", null, "Other sessions of this user were signed out.", Ip(ctx));
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
                tokens = Template.Tokens,
                version = AppVersion, parallelCopies = Math.Clamp(s.MaxParallelCopies, 1, 64)
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
                // Compare real locations (a folder that is a link to another counts as that folder); case matters except on Windows and macOS
                static string Real(string p)
                {
                    var full = Path.GetFullPath(p);
                    try { if (new DirectoryInfo(full).ResolveLinkTarget(true) is FileSystemInfo t) full = t.FullName; } catch (IOException) { }
                    return full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                }
                var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var a = Real(body.Source.Path!); var b = Real(body.Destination.Path!);
                if (b.StartsWith(a, cmp)) throw new ApiError("Destination folder cannot be inside the source folder.");
                if (a.StartsWith(b, cmp)) throw new ApiError("Source folder cannot be inside the destination folder (copies could be written into the source).");
            }
            if (body.Source.Type != "Local" && body.Source.Type == body.Destination.Type
                && string.Equals(body.Source.Connection, body.Destination.Connection, StringComparison.OrdinalIgnoreCase)
                && string.Equals(body.Source.Container, body.Destination.Container, StringComparison.Ordinal))
            {
                static string P(string? s) { s = (s ?? "").Trim('/'); return s.Length == 0 ? "" : s + "/"; }
                string sp = P(body.Source.Prefix), dp = P(body.Destination.Prefix);
                if (dp.StartsWith(sp)) throw new ApiError("Destination prefix cannot be inside the source prefix.");
                if (sp.StartsWith(dp)) throw new ApiError("Source prefix cannot be inside the destination prefix (copies could be written into the source).");
            }
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
            FourEyes.TouchMaker(db, id, u);
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
            db.Db.Execute("DELETE FROM exclusions WHERE job_id=@j", ("j", id));
            db.Db.Execute("DELETE FROM change_requests WHERE job_id=@j", ("j", id));
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
            if (string.IsNullOrEmpty(cfg.AmbiguousRule)) cfg.AmbiguousRule = "Flag";
            if (!new[] { "Flag", "KeepAll" }.Contains(cfg.AmbiguousRule)) errors.Add("Unknown rule for files matching several records.");
            if (!new[] { "None", "Sha256", "Md5" }.Contains(cfg.Integrity)) errors.Add("Unknown integrity option.");
            if (errors.Count > 0) return Results.Json(new { error = string.Join(" ", errors), errors }, statusCode: 400);
            bool wasScanned = j.Str("status") != JobStatus.Draft;
            db.UpdateJob(id, ("config_json", AppDb.ToJson(cfg)), ("status", JobStatus.Draft), ("message", wasScanned ? "Configuration changed. Run the scan again." : null));
            if (wasScanned) { db.Db.Execute("DELETE FROM items WHERE job_id=@j", ("j", id)); db.UpdateJob(id, ("counts_json", null), ("discovered", 0)); }
            FourEyes.TouchMaker(db, id, u);
            db.Audit(u, r, "Configuration saved", id,
                $"Year rule: {cfg.YearRule}{(cfg.YearRule is "FileNamePattern" or "PathPattern" ? " " + cfg.YearPattern : "")} · Year mandatory: {cfg.YearMandatory} · Template: {cfg.Template} · Several records: {(cfg.AmbiguousRule == "KeepAll" ? "keep all" : "flag as ambiguous")} · Duplicates: {cfg.DuplicateRule} · Integrity: {cfg.Integrity} · Batch: {cfg.BatchSize}", Ip(ctx));
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
            FourEyes.TouchMaker(db, id, u);
            js.StartScan(id, u, r);
            return Results.Ok(new { started = true });
        }).RequireAuthorization("Operate");

        api.MapGet("/jobs/{id}/items", (string id, AppDb db, string? status, string? q, string? group, int? page, int? pageSize) =>
        {
            Need(db, id);
            int ps = Math.Clamp(pageSize ?? 50, 10, 500), pg = Math.Max(1, page ?? 1);
            var (where, args) = Exclusions.Where(id, status, q, group);
            var total = Convert.ToInt64(db.Db.Scalar($"SELECT COUNT(*) FROM items WHERE {where}", args.ToArray()) ?? 0L);
            args.Add(("lim", ps)); args.Add(("off", (pg - 1) * ps));
            var rows = db.Db.Query($"SELECT *, {Exclusions.GroupSql} reason_group FROM items WHERE {where} ORDER BY CASE status WHEN 'AMBIGUOUS' THEN 0 WHEN 'FAILED' THEN 1 ELSE 2 END, id LIMIT @lim OFFSET @off", args.ToArray());
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
                    reasonGroup = r.Str("reason_group"), excludedFrom = r.Str("excluded_from"), excludedBy = r.Str("excluded_by"),
                    excludedAt = r.Str("excluded_at"), exclusionNote = r.Str("exclusion_note"),
                    candidates = r.Str("candidates_json") is string cj ? JsonSerializer.Deserialize<List<Candidate>>(cj, AppDb.Json) : null
                })
            });
        }).RequireAuthorization("View");

        // Exceptions grouped by status and reason, for excluding a whole reason at once (excluded=true: what was excluded)
        api.MapGet("/jobs/{id}/reasons", (string id, bool? excluded, AppDb db, Exclusions ex) =>
        {
            Need(db, id);
            return Results.Ok(ex.Reasons(id, excluded == true).Select(x => new { status = x.Status, group = x.Group, count = x.Count }));
        }).RequireAuthorization("View");

        // Bulk exclude / restore: either item IDs (ticked rows) or a filter (everything in the current view: status, search, reason)
        api.MapPost("/jobs/{id}/items/exclude", (HttpContext ctx, string id, BulkBody body, AppDb db, JobService js, Exclusions ex) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (Exclusions.CannotChange(j, js.IsRunning(id)) is string why) throw new ApiError(why);
            var note = (body.Note ?? "").Trim();
            if (note.Length < 3) throw new ApiError("Enter a note saying why these items are excluded (it is kept in the reports and audit log).");
            if (note.Length > 500) note = note[..500];
            if (FourEyes.IsSubmitted(j)) throw new ApiError(FourEyes.LockedMessage(j));
            var (ids, filter) = BulkTarget(body);
            if (NeedsRequest(db, j)) return Results.Ok(CreateRequest(db, ex, id, "exclude", ids, filter, note, u, r, Ip(ctx)));
            var (n, by) = ex.Exclude(id, ids, filter, note, u);
            if (n == 0) throw new ApiError("None of the chosen items can be excluded (only exceptions can).");
            FourEyes.TouchMaker(db, id, u);
            db.Audit(u, r, "Exceptions excluded", id, $"{n:N0} items ({Describe(by)}){DescribeFilter(filter)} · Note: {note}", Ip(ctx));
            return Results.Ok(new { count = n, byStatus = by, message = $"{n:N0} item{(n == 1 ? "" : "s")} excluded." });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/items/restore", (HttpContext ctx, string id, BulkBody body, AppDb db, JobService js, Exclusions ex) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (Exclusions.CannotChange(j, js.IsRunning(id)) is string why) throw new ApiError(why);
            if (FourEyes.IsSubmitted(j)) throw new ApiError(FourEyes.LockedMessage(j));
            var (ids, filter) = BulkTarget(body, restore: true);
            if (NeedsRequest(db, j)) return Results.Ok(CreateRequest(db, ex, id, "restore", ids, filter, (body.Note ?? "").Trim(), u, r, Ip(ctx)));
            var (n, by) = ex.Restore(id, ids, filter);
            if (n == 0) throw new ApiError("None of the chosen items are excluded.");
            FourEyes.TouchMaker(db, id, u);
            db.Audit(u, r, "Exclusions restored", id, $"{n:N0} items back to {Describe(by)}{DescribeFilter(filter)}", Ip(ctx));
            return Results.Ok(new { count = n, byStatus = by, message = $"{n:N0} item{(n == 1 ? "" : "s")} restored." });
        }).RequireAuthorization("Operate");

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
            if (FourEyes.IsSubmitted(j)) throw new ApiError(FourEyes.LockedMessage(j));
            var (ok, msg) = js.Resolve(id, itemId, body.RecordId ?? "", body.Note, u, r, body.MappingFile);
            if (!ok) throw new ApiError(msg);
            FourEyes.TouchMaker(db, id, u);
            return Results.Ok(new { message = msg });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/approve", (HttpContext ctx, string id, ApproveBody body, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (j.Str("status") != JobStatus.AwaitingApproval) throw new ApiError("Only a validated job awaiting approval can be approved.");
            if (js.IsRunning(id)) throw new ApiError("The job is running.");
            if (!body.Acknowledge) throw new ApiError("Confirm that you have reviewed the preview and exceptions.");
            var c = db.CountByStatus(id);
            if (c[ItemStatus.Ready] == 0) throw new ApiError("There are no files ready for processing.");
            bool fourEyes = db.LoadSettings().RequireSecondApprover;
            if (fourEyes)
            {
                if (!FourEyes.IsSubmitted(j)) throw new ApiError("Maker-checker is on: submit the job for approval first. A different person then approves it.");
                if (FourEyes.CheckerProblem(j, u) is string why) throw new ApiError(why, 403);
            }
            if (fourEyes && string.IsNullOrEmpty(body.Version))
                throw new ApiError("Maker-checker is on: send the version of the job you reviewed (\"version\" from GET /api/jobs/{id}).");
            if (!string.IsNullOrEmpty(body.Version) && body.Version != JobVersion(j))
                throw new ApiError("The job has changed since you opened it (it was rescanned, resubmitted or its exceptions changed). Reload the page, review it again, then approve.", 409);
            db.UpdateJob(id, ("approved_by", u), ("approved_at", DateTime.UtcNow.ToString("o")));
            db.Audit(u, r, "Processing approved", id, $"{c[ItemStatus.Ready]:N0} files · COPY" +
                (fourEyes ? $" · checker {u}; submitted by {j.Str("submitted_by")}; prepared by {string.Join(", ", FourEyes.Makers(j))}" : ""), Ip(ctx));
            js.StartProcessing(id, u, r, "Processing started");
            return Results.Ok(new { started = true });
        }).RequireAuthorization("Operate");

        // Maker-checker: submit a scanned job for approval, withdraw it, or (checker) reject it
        api.MapPost("/jobs/{id}/submit", (HttpContext ctx, string id, NoteBody body, AppDb db, JobService js) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (!db.LoadSettings().RequireSecondApprover) throw new ApiError("Maker-checker is off: the job can be approved directly.");
            if (j.Str("status") != JobStatus.AwaitingApproval || js.IsRunning(id)) throw new ApiError("Only a scanned job awaiting approval can be submitted.");
            if (FourEyes.IsSubmitted(j)) throw new ApiError($"Already submitted by {j.Str("submitted_by")}.");
            if (db.CountByStatus(id)[ItemStatus.Ready] == 0) throw new ApiError("There are no files ready for processing.");
            FourEyes.TouchMaker(db, id, u);
            var note = (body.Note ?? "").Trim();
            db.UpdateJob(id, ("submitted_by", u), ("submitted_at", DateTime.UtcNow.ToString("o")), ("submit_note", note.Length > 500 ? note[..500] : note), ("message", null));
            db.Audit(u, r, "Submitted for approval", id, string.IsNullOrEmpty(note) ? null : "Note: " + note, Ip(ctx));
            return Results.Ok(new { message = "Submitted. A different person must now approve or reject it." });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/withdraw", (HttpContext ctx, string id, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (!FourEyes.IsSubmitted(j) || j.Str("status") != JobStatus.AwaitingApproval) throw new ApiError("The job is not waiting for approval.");
            if (!string.Equals(j.Str("submitted_by"), u, StringComparison.OrdinalIgnoreCase) && r != Roles.Administrator)
                throw new ApiError("Only the person who submitted it, or an administrator, can withdraw it.", 403);
            db.UpdateJob(id, ("submitted_by", null), ("submitted_at", null), ("submit_note", null));
            db.Audit(u, r, "Submission withdrawn", id, null, Ip(ctx));
            return Results.Ok(new { message = "Withdrawn. The job can be changed again." });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/reject", (HttpContext ctx, string id, NoteBody body, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            if (!FourEyes.IsSubmitted(j) || j.Str("status") != JobStatus.AwaitingApproval) throw new ApiError("The job is not waiting for approval.");
            if (FourEyes.CheckerProblem(j, u) is string why) throw new ApiError(why, 403);
            var reason = (body.Note ?? "").Trim();
            if (reason.Length < 3) throw new ApiError("Enter the reason for rejecting, so the maker knows what to change.");
            if (reason.Length > 500) reason = reason[..500];
            db.UpdateJob(id, ("submitted_by", null), ("submitted_at", null), ("submit_note", null), ("message", $"Rejected by {u}: {reason}"));
            db.Audit(u, r, "Approval rejected", id, $"Submitted by {j.Str("submitted_by")} · Reason: {reason}", Ip(ctx));
            return Results.Ok(new { message = "Rejected. The maker sees your reason on the job." });
        }).RequireAuthorization("Operate");

        // Change requests: after approval, with maker-checker on, excluding or restoring items needs a second person
        api.MapGet("/jobs/{id}/requests", (string id, AppDb db) =>
        {
            Need(db, id);
            return Results.Ok(db.Db.Query("SELECT * FROM change_requests WHERE job_id=@j ORDER BY id DESC LIMIT 200", ("j", id)).Select(RequestDto));
        }).RequireAuthorization("View");

        api.MapPost("/jobs/{id}/requests/{rid:long}/approve", (HttpContext ctx, string id, long rid, AppDb db, JobService js, Exclusions ex) =>
        {
            var (u, r) = Who(ctx);
            var j = Need(db, id);
            var req = PendingRequest(db, id, rid);
            if (string.Equals(req.Str("requested_by"), u, StringComparison.OrdinalIgnoreCase))
                throw new ApiError("You made this request, so a different person must approve it.", 403);
            if (Exclusions.CannotChange(j, js.IsRunning(id)) is string why) throw new ApiError(why);
            var ids = JsonSerializer.Deserialize<long[]>(req.Str("item_ids_json")!) ?? Array.Empty<long>();
            var by = req.Str("requested_by")!;
            var note = $"{req.Str("note")} (approved by {u})";
            // Claim the request first: only one decision can win
            if (db.Db.Execute("UPDATE change_requests SET status='Approved', decided_by=@u, decided_at=@t WHERE id=@id AND status='Pending'",
                ("u", u), ("t", DateTime.UtcNow.ToString("o")), ("id", rid)) == 0) throw new ApiError("This request has just been decided by someone else.");
            int n; Dictionary<string, int> stat;
            try { (n, stat) = req.Str("kind") == "exclude" ? ex.Exclude(id, ids, null, note, by) : ex.Restore(id, ids, null); }
            catch { db.Db.Execute("UPDATE change_requests SET status='Pending', decided_by=NULL, decided_at=NULL WHERE id=@id", ("id", rid)); throw; }
            db.Db.Execute("UPDATE change_requests SET decision_note=@n WHERE id=@id", ("n", $"{n:N0} items changed"), ("id", rid));
            db.Audit(u, r, req.Str("kind") == "exclude" ? "Exclusion request approved" : "Restore request approved", id,
                $"Request {rid} by {by}: {n:N0} items ({Describe(stat)}) · Note: {req.Str("note")}", Ip(ctx));
            return Results.Ok(new { count = n, message = $"Approved. {n:N0} item{(n == 1 ? "" : "s")} {(req.Str("kind") == "exclude" ? "excluded" : "restored")}." });
        }).RequireAuthorization("Operate");

        api.MapPost("/jobs/{id}/requests/{rid:long}/reject", (HttpContext ctx, string id, long rid, NoteBody body, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            Need(db, id);
            var req = PendingRequest(db, id, rid);
            bool own = string.Equals(req.Str("requested_by"), u, StringComparison.OrdinalIgnoreCase);
            var reason = (body.Note ?? "").Trim();
            if (!own && reason.Length < 3) throw new ApiError("Enter the reason for rejecting.");
            if (db.Db.Execute("UPDATE change_requests SET status=@s, decided_by=@u, decided_at=@t, decision_note=@n WHERE id=@id AND status='Pending'",
                ("s", own ? "Withdrawn" : "Rejected"), ("u", u), ("t", DateTime.UtcNow.ToString("o")), ("n", own ? "Withdrawn by the requester" : reason), ("id", rid)) == 0)
                throw new ApiError("This request has just been decided by someone else.");
            db.Audit(u, r, own ? "Change request withdrawn" : "Change request rejected", id, $"Request {rid} by {req.Str("requested_by")}{(own ? "" : " · Reason: " + reason)}", Ip(ctx));
            return Results.Ok(new { message = own ? "Request withdrawn." : "Request rejected." });
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

        api.MapPut("/settings", (HttpContext ctx, Dictionary<string, JsonElement> body, AppDb db) =>
        {
            var (u, r) = Who(ctx);
            var before = db.LoadSettings();
            // Only the settings that are sent change; the others keep their current value
            var s = db.LoadSettings();
            foreach (var (k, v) in body)
            {
                var prop = typeof(AppSettings).GetProperties().FirstOrDefault(pp => pp.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (prop == null) throw new ApiError($"Unknown setting: {k}.");
                try { prop.SetValue(s, v.Deserialize(prop.PropertyType, AppDb.Json)); }
                catch (Exception) { throw new ApiError($"Setting {k} has an invalid value."); }
            }
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
        if (FourEyes.IsSubmitted(j)) throw new ApiError(FourEyes.LockedMessage(j));
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
        FourEyes.TouchMaker(db, id, user);
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
        startedAt = j.Str("started_at"), endedAt = j.Str("ended_at"), submittedBy = j.Str("submitted_by"),
        counts = j.Str("counts_json") is string cj ? JsonSerializer.Deserialize<Dictionary<string, long>>(cj) : null
    };

    private static object JobDetail(Row j, AppDb db, JobService js)
    {
        var id = j.Str("id")!;
        var sp = js.ScanProgress(id);
        var cp = js.CopyProgressOf(id);
        object? scan = null;
        if (sp != null)
        {
            sp.Meter.Mark(sp.Done);
            var (perSec, _) = sp.Meter.Rate();
            scan = new
            {
                phase = sp.Phase, done = sp.Done, total = sp.Total, rate = Math.Round(perSec, 1),
                etaSeconds = sp.Total > 0 ? sp.Meter.EtaSeconds(sp.Total - sp.Done) : null,
                phaseElapsedSeconds = (long)(DateTime.UtcNow - sp.PhaseStarted).TotalSeconds,
                elapsedSeconds = (long)(DateTime.UtcNow - sp.Started).TotalSeconds
            };
        }
        object progress;
        if (cp != null)
        {
            cp.Meter.Mark(Interlocked.Read(ref cp.Done), Interlocked.Read(ref cp.BytesDone));
            var (perSec, bytesPerSec) = cp.Meter.Rate();
            long done = Interlocked.Read(ref cp.Done), bytesDone = Interlocked.Read(ref cp.BytesDone);
            progress = new
            {
                done, total = cp.Total, bytesDone, bytesTotal = cp.BytesTotal,
                copied = cp.Copied, skipped = cp.Skipped, failed = cp.Failed,
                rate = Math.Round(perSec, 1), bytesRate = (long)bytesPerSec,
                etaSeconds = cp.Meter.EtaSeconds(cp.Total - done, Math.Max(0, cp.BytesTotal - bytesDone)),
                elapsedSeconds = (long)(DateTime.UtcNow - cp.Meter.Started).TotalSeconds, live = true
            };
        }
        else progress = new { done = j.Long("progress_done"), total = j.Long("progress_total"), live = false };
        // What the copy step did (copied, skipped because already at the destination, failed). Live figures while it runs.
        var run = cp != null
            ? new Dictionary<string, long> { [ItemStatus.Processed] = cp.Copied, [ItemStatus.Duplicate] = cp.Skipped, [ItemStatus.Failed] = cp.Failed }
            : db.Db.Query("SELECT status, COUNT(*) n FROM items WHERE job_id=@j AND processed_at IS NOT NULL GROUP BY status", ("j", id))
                .ToDictionary(r => r.Str("status")!, r => r.Long("n"));
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
            progress, scan,
            copyResult = new { processed = run.GetValueOrDefault(ItemStatus.Processed), skipped = run.GetValueOrDefault(ItemStatus.Duplicate), failed = run.GetValueOrDefault(ItemStatus.Failed) },
            running = js.IsRunning(id), discovered = j.Long("discovered"), version = JobVersion(j),
            makerChecker = db.LoadSettings().RequireSecondApprover, makers = FourEyes.Makers(j),
            submittedBy = j.Str("submitted_by"), submittedAt = j.Str("submitted_at"), submitNote = j.Str("submit_note"),
            pendingRequests = Convert.ToInt64(db.Db.Scalar("SELECT COUNT(*) FROM change_requests WHERE job_id=@j AND status='Pending'", ("j", id)) ?? 0L),
            counts = db.CountByStatus(id)
        };
    }

    public sealed record LoginBody(string? UserName, string? Password);
    public sealed record PasswordBody(string? CurrentPassword, string? NewPassword);
    public sealed record TestBody(Location? Location, bool NeedWrite);
    public sealed record CreateJobBody(string? Name, Location? Source, Location? Destination);
    /// <summary>The application version (from the project file), shown in the side bar so people can see which build is running.</summary>
    public static readonly string AppVersion = typeof(Endpoints).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> JobGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Changes whenever what an approver reviews changes (scan, counts after resolve or exclude, submission).
    /// The approve call can send it back, so an approval from a page opened before a change is refused.
    /// </summary>
    public static string JobVersion(Row j) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{j.Str("scan_ended_at")}|{j.Str("submitted_at")}|{j.Str("counts_json")}|{j.Str("config_json")}|{j.Str("mappings_json")}")))[..16];

    public sealed record NoteBody(string? Note);

    /// <summary>With maker-checker on, a job that has been approved (copying, paused or finished) is changed only through a request.</summary>
    private static bool NeedsRequest(AppDb db, Row j) => db.LoadSettings().RequireSecondApprover && j.Str("status") != JobStatus.AwaitingApproval;

    private static object CreateRequest(AppDb db, Exclusions ex, string jobId, string kind, long[]? ids, Exclusions.Filter? filter, string note, string user, string role, string? ip)
    {
        var (sel, by) = ex.Preview(jobId, ids, filter, kind == "restore");
        if (sel.Length == 0) throw new ApiError(kind == "exclude" ? "None of the chosen items can be excluded (only exceptions can)." : "None of the chosen items are excluded.");
        var chosen = sel.ToHashSet();
        foreach (var p in db.Db.Query("SELECT id, item_ids_json FROM change_requests WHERE job_id=@j AND kind=@k AND status='Pending'", ("j", jobId), ("k", kind)))
        {
            var overlap = (JsonSerializer.Deserialize<long[]>(p.Str("item_ids_json")!) ?? Array.Empty<long>()).Count(chosen.Contains);
            if (overlap > 0) throw new ApiError($"{overlap:N0} of these items are already in pending request {p.Long("id")}. Wait for it to be decided, or withdraw it.");
        }
        if (note.Length > 500) note = note[..500];
        var summary = (kind == "exclude" ? "Exclude " : "Restore ") + Describe(by) + DescribeFilter(filter);
        var rid = db.Db.Insert(@"INSERT INTO change_requests(job_id,kind,item_ids_json,item_count,summary,note,requested_by,requested_at) VALUES(@j,@k,@ids,@n,@s,@note,@u,@t)",
            ("j", jobId), ("k", kind), ("ids", JsonSerializer.Serialize(sel)), ("n", sel.Length), ("s", summary), ("note", note), ("u", user), ("t", DateTime.UtcNow.ToString("o")));
        db.Audit(user, role, kind == "exclude" ? "Exclusion requested" : "Restore requested", jobId, $"Request {rid}: {sel.Length:N0} items ({Describe(by)}) · Note: {note}", ip);
        return new { pending = true, requestId = rid, count = sel.Length, message = $"Request sent: {sel.Length:N0} item{(sel.Length == 1 ? "" : "s")} will be {(kind == "exclude" ? "excluded" : "restored")} when a different person approves it." };
    }

    private static Row PendingRequest(AppDb db, string jobId, long rid)
    {
        var req = db.Db.QueryOne("SELECT * FROM change_requests WHERE id=@id AND job_id=@j", ("id", rid), ("j", jobId)) ?? throw new ApiError("Request not found.", 404);
        if (req.Str("status") != "Pending") throw new ApiError($"This request is already {req.Str("status")!.ToLowerInvariant()}.");
        return req;
    }

    private static object RequestDto(Row r) => new
    {
        id = r.Long("id"), kind = r.Str("kind"), count = r.Long("item_count"), summary = r.Str("summary"), note = r.Str("note"),
        requestedBy = r.Str("requested_by"), requestedAt = r.Str("requested_at"), status = r.Str("status"),
        decidedBy = r.Str("decided_by"), decidedAt = r.Str("decided_at"), decisionNote = r.Str("decision_note")
    };

    public sealed record BulkBody(long[]? Ids, string? Status, string? Q, string? Group, string? Note, bool? AllInView);

    private static (long[]? Ids, Exclusions.Filter? Filter) BulkTarget(BulkBody b, bool restore = false)
    {
        if (b.Ids is { Length: > 0 })
        {
            if (b.Ids.Length > 100_000) throw new ApiError("Too many items in one request; use \"all in this view\" instead.");
            return (b.Ids, null);
        }
        if (b.AllInView != true) throw new ApiError("Choose items, or confirm all items in the current view.");
        var status = restore ? ItemStatus.Excluded : string.IsNullOrEmpty(b.Status) || b.Status == "ALL" ? "EXCEPTIONS" : b.Status;
        if (!restore && status != "EXCEPTIONS" && !ItemStatus.Excludable.Contains(status)) throw new ApiError($"Items with status {status} cannot be excluded.");
        return (null, new Exclusions.Filter(status, b.Q, b.Group));
    }

    private static string Describe(Dictionary<string, int> by) => string.Join(", ", by.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value:N0} {kv.Key}"));
    private static string DescribeFilter(Exclusions.Filter? f) => f == null ? " · ticked rows" :
        $" · all in view: {f.Status}{(string.IsNullOrWhiteSpace(f.Group) ? "" : ", reason " + f.Group)}{(string.IsNullOrWhiteSpace(f.Q) ? "" : $", search \"{f.Q}\"")}";

    public sealed record ResolveBody(string? RecordId, string? Note, string? MappingFile = null);
    public sealed record ApproveBody(bool Acknowledge, string? Version = null);
    public sealed record UserBody(string? UserName, string? DisplayName, string? Role, string? Password, bool? Active);
}
