using System.Text;
using DocumentOrganizer.Infrastructure;
using DocumentOrganizer.Storage;

namespace DocumentOrganizer.Engine;

/// <summary>
/// Creates a small, clearly fictional sample dataset (files + mapping file) that exercises every status:
/// ready, unmapped, missing, ambiguous, duplicate, invalid. Used to try the application without real documents.
/// The files are written to a local folder and, optionally, to every storage connection marked "UseForDemo" (local emulators).
/// </summary>
public sealed class DemoData(StorageFactory storage, OrganizerOptions opt, ILogger<DemoData> log)
{
    public const string MappingFileName = "demo-mapping.xlsx";
    public const string SourceContainer = "employee-documents", DestinationContainer = "organized-documents", SourcePrefix = "hr/";

    public sealed record CloudDemo(string Connection, string Provider, string SourceContainer, string SourcePrefix, string DestinationContainer);
    public sealed record Result(string LocalSource, string LocalDestination, string MappingFile, int Files, int Records, string? CloudNote, List<CloudDemo> Cloud);

    private static readonly string[] Types = { "Offer Letter", "ID Proof", "Tax ID", "Payslip", "Tax Form", "Medical Certificate", "Relieving Letter", "Experience Letter" };

    public async Task<Result> CreateAsync(string dataPath, CancellationToken ct)
    {
        var root = Path.Combine(dataPath, "demo");
        var src = Path.Combine(root, "source");
        var dst = Path.Combine(root, "organized");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(src);
        var rnd = new Random(20240601);
        var rows = new List<string?[]>();
        var files = new Dictionary<string, byte[]>(); // relative path -> content
        int id = 775300;
        string Folder(string type) => type is "Payslip" or "Tax Form" ? "hr/payroll" : "hr/docs";
        byte[] Pdf(string label) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% Sample document for Document Organizer demo\n% {label}\n%%EOF\n");
        void Rec(string? emp, string? type, string? name, string? up, string? file) =>
            rows.Add(new[] { (++id).ToString(), emp, type, name, type == null ? null : $"{type} uploaded via self-service", up, file, "EMP", "DOCS" });

        for (int e = 800; e < 950; e++)
        {
            var emp = $"E{e:00000}";
            foreach (var type in Types.OrderBy(_ => rnd.Next()).Take(rnd.Next(2, 5)))
            {
                var date = new DateTime(rnd.Next(2016, 2026), rnd.Next(1, 13), rnd.Next(1, 28));
                var file = $"{emp.ToLowerInvariant()}_{type.ToLowerInvariant().Replace(' ', '_')}.pdf";
                Rec(emp, type, type, date.ToString("dd-MMM-yyyy"), file);
                var sub = rnd.Next(4) == 0 ? $"/{date.Year}" : "";
                files[$"{Folder(type)}{sub}/{file}"] = Pdf($"{emp} {type}");
            }
        }
        // Reference example: E00879 / Medical Certificate / 2021 / e00879_final_certificate.pdf
        Rec("E00879", "Medical Certificate", "Final Certificate", "25-Nov-2021", "e00879_final_certificate.pdf");
        files["hr/docs/2021/e00879_final_certificate.pdf"] = Pdf("E00879 Medical Certificate");
        // Case-insensitive match and normalized match
        Rec("E00951", "Tax ID", "Tax ID", "09-Mar-2021", "E00951_TAXID.PDF"); files["hr/docs/e00951_taxid.pdf"] = Pdf("E00951 Tax ID");
        Rec("E00952", "Offer Letter", "Offer", "02-Jun-2019", "E00952 Offer Letter.pdf"); files["hr/docs/E00952_Offer_Letter.pdf"] = Pdf("E00952 Offer");
        // Unmapped files (no record)
        for (int i = 0; i < 6; i++) files[$"hr/misc/scan_{i:0000}_final.pdf"] = Pdf("unmapped scan");
        files["hr/uploads/bulk/IMG-20190714-WA0011.jpg"] = Encoding.ASCII.GetBytes("JPEG demo");
        // Missing files (records only)
        Rec("E00960", "Tax ID", "Tax ID", "11-Jan-2022", "E00960_TaxID.pdf");
        Rec("E00961", "Relieving Letter", "Relieving", "30-Sep-2023", "e00961_relieving_letter.pdf");
        Rec("E00962", "Tax Form", "Tax Form FY23", "15-Jun-2023", "E00962_TaxForm_FY23.pdf");
        // Ambiguous: two employees' records name the same file
        Rec("E00412", "Offer Letter", "Offer Letter", "03-Feb-2020", "offer_letter.pdf");
        Rec("E00977", "Offer Letter", "Offer Letter", "11-Aug-2020", "offer_letter.pdf");
        files["hr/docs/offer_letter.pdf"] = Pdf("ambiguous offer letter");
        Rec("E01544", "ID Proof", "ID Proof", "05-May-2020", "id_proof.pdf");
        Rec("E01560", "ID Proof", "ID Proof", "19-May-2020", "id_proof.pdf");
        files["hr/docs/id_proof.pdf"] = Pdf("ambiguous id proof");
        // Duplicate mapping record and duplicate physical file
        Rec("E00879", "Tax Form", "Tax Form FY22", "18-Jun-2022", "E00879_TaxForm_FY22.pdf");
        Rec("E00879", "Tax Form", "Tax Form FY22 (re-upload)", "19-Jun-2022", "E00879_TaxForm_FY22.pdf");
        files["hr/payroll/E00879_TaxForm_FY22.pdf"] = Pdf("tax form");
        files["hr/payroll/copy/E00879_TaxForm_FY22.pdf"] = Pdf("tax form");
        // Validation errors
        Rec(null, "Medical Certificate", "Certificate", "01-Jul-2021", "e03381_cert.pdf"); files["hr/docs/e03381_cert.pdf"] = Pdf("blank emp");
        Rec("E03390", null, "Medical", "12-Dec-2022", "E03390_medical.pdf"); files["hr/docs/E03390_medical.pdf"] = Pdf("blank type");
        Rec("E02004", "Experience Letter", "Experience", null, "E02004_experience.pdf"); files["hr/docs/E02004_experience.pdf"] = Pdf("no date");
        Rec("E02871", "Payslip", "Payslip Mar", "05-Apr-2022", "E02871_payslip_mar.pdf"); files["hr/payroll/E02871_payslip_mar.pdf"] = Array.Empty<byte>();
        Rec("E02872", "Payslip", "Payslip Apr", "05-May-2022", null);

        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, content, ct);
        }

        var mapping = Path.Combine(root, MappingFileName);
        await using (var fs = File.Create(mapping))
        using (var x = new XlsxWriter(fs))
        {
            x.BeginSheet("Sheet1", new[] { "ID", "EmployeeCode", "Type", "Name", "Description", "UploadedOn", "FileName", "ModuleCode", "SubModuleCode" });
            foreach (var r in rows) x.WriteRow(r);
        }

        // Optional: also load the same files into every connection marked UseForDemo (e.g. Azurite or MinIO running locally).
        var cloud = new List<CloudDemo>(); var notes = new List<string>();
        foreach (var (name, conn) in opt.StorageConnections.Where(kv => kv.Value.UseForDemo))
        {
            string provider;
            try { provider = conn.ResolveProvider(); } catch (Exception ex) { notes.Add($"{name}: {ex.Message}"); continue; }
            try
            {
                var store = storage.Create(new Domain.Location { Type = provider, Connection = name, Container = SourceContainer, Prefix = "" });
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(60));
                await store.TestAsync(needWrite: true, cts.Token);
                await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cts.Token }, async (f, t) =>
                {
                    try { await store.WriteAsync(f.Key, new MemoryStream(f.Value), null, overwrite: true, t); } catch (DestinationExistsException) { }
                });
                var dest = storage.Create(new Domain.Location { Type = provider, Connection = name, Container = DestinationContainer, Prefix = "" });
                await dest.TestAsync(needWrite: true, cts.Token);
                cloud.Add(new CloudDemo(name, provider, SourceContainer, SourcePrefix, DestinationContainer));
                notes.Add($"Also loaded into '{name}': '{SourceContainer}' with prefix '{SourcePrefix}'; destination '{DestinationContainer}' is ready.");
            }
            catch (Exception ex)
            {
                log.LogInformation("Demo data not loaded into {Conn}: {Msg}", name, ex.Message);
                notes.Add($"'{name}' is not reachable, so it was skipped ({ex.Message.Split('\n')[0]}).");
            }
        }
        return new Result(src, dst, mapping, files.Count, rows.Count, notes.Count > 0 ? string.Join(" ", notes) : null, cloud);
    }
}
