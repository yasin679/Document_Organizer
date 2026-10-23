using System.Text.Json;
using DocumentOrganizer.Domain;
using DocumentOrganizer.Infrastructure;

namespace DocumentOrganizer.Engine;

/// <summary>
/// Maker-checker (four-eyes) control, switched on by an administrator in Settings.
/// <list type="bullet">
/// <item>Everyone who prepares or changes a job (creates it, uploads or removes mapping files, saves rules, scans,
/// resolves ambiguous files, excludes or restores items) is recorded as one of its <b>makers</b>.</item>
/// <item>A maker <b>submits</b> the scanned job for approval. While submitted, the job cannot be changed; the submitter
/// can withdraw it.</item>
/// <item>A <b>checker</b>, an operator or administrator who is not one of the job's makers, approves it (copying starts)
/// or rejects it with a reason.</item>
/// <item>After approval, excluding or restoring items creates a <b>change request</b> that a different person must approve.</item>
/// </list>
/// </summary>
public static class FourEyes
{
    /// <summary>Everyone who worked on the job; the creator always counts (also for jobs created before maker-checker existed).</summary>
    public static List<string> Makers(Row job)
    {
        var list = job.Str("makers_json") is string s ? JsonSerializer.Deserialize<List<string>>(s) ?? new() : new();
        if (job.Str("created_by") is string c && !list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Insert(0, c);
        return list;
    }

    /// <summary>Records the user as one of the job's makers.</summary>
    public static void TouchMaker(AppDb db, string jobId, string user)
    {
        var job = db.GetJob(jobId);
        if (job == null) return;
        var makers = Makers(job);
        if (makers.Contains(user, StringComparer.OrdinalIgnoreCase)) return;
        makers.Add(user);
        db.UpdateJob(jobId, ("makers_json", JsonSerializer.Serialize(makers)));
    }

    /// <summary>Waiting for a checker. (After approval, submitted_by is kept for the reports but no longer locks the job.)</summary>
    public static bool IsSubmitted(Row job) => !string.IsNullOrEmpty(job.Str("submitted_by")) && job.Str("status") == JobStatus.AwaitingApproval;

    /// <summary>Why this user may not act as checker for the job (null when they may).</summary>
    public static string? CheckerProblem(Row job, string user)
    {
        var makers = Makers(job);
        if (makers.Contains(user, StringComparer.OrdinalIgnoreCase))
            return $"You prepared or changed this job, so a different person must approve or reject it. People who worked on it: {string.Join(", ", makers)}.";
        return null;
    }

    public static string LockedMessage(Row job) =>
        $"This job was submitted for approval by {job.Str("submitted_by")}. It cannot be changed until it is approved, rejected or withdrawn.";
}
