using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DocumentOrganizer.Infrastructure;

/// <summary>
/// Streaming reader for the data worksheet of an .xlsx file. Rows are yielded one at a time,
/// so very large mapping files (175k+ rows) are not loaded into memory as a whole.
/// Cell values are returned as strings; date-formatted numbers are returned as ISO dates (yyyy-MM-dd).
/// </summary>
public static class XlsxReader
{
    private static readonly XmlReaderSettings Xs = new() { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <summary>
    /// Reads the rows of the data sheet: the first visible sheet that has a header row (per <paramref name="isHeader"/>)
    /// in its first 20 rows, so a workbook that starts with a cover or summary sheet still works; otherwise the first visible sheet.
    /// </summary>
    public static IEnumerable<string[]> ReadRows(string path, Func<string[], bool>? isHeader = null)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);
        var sheets = SheetPaths(zip);
        if (sheets.Count == 0) throw new InvalidDataException("Workbook has no sheets.");
        var sheetPath = sheets[0];
        if (isHeader != null && sheets.Count > 1)
            sheetPath = sheets.FirstOrDefault(p => ReadSheet(zip, p, shared, dateStyles).Take(20).Any(isHeader)) ?? sheets[0];
        foreach (var r in ReadSheet(zip, sheetPath, shared, dateStyles)) yield return r;
    }

    private static IEnumerable<string[]> ReadSheet(ZipArchive zip, string sheetPath, List<string> shared, HashSet<int> dateStyles)
    {
        var entry = zip.GetEntry(sheetPath) ?? throw new InvalidDataException("Worksheet not found in workbook.");
        using var s = entry.Open();
        using var xr = XmlReader.Create(s, Xs);
        var row = new List<string>();
        int lastRowNum = 0;
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName == "row")
            {
                int rowNum = int.TryParse(xr.GetAttribute("r"), out var rn) ? rn : lastRowNum + 1;
                // emit blank rows for gaps so row numbers stay meaningful
                while (lastRowNum + 1 < rowNum) { lastRowNum++; yield return Array.Empty<string>(); }
                lastRowNum = rowNum;
                row.Clear();
                if (xr.IsEmptyElement) { yield return Array.Empty<string>(); continue; }
                using var sub = xr.ReadSubtree();
                while (sub.Read())
                {
                    if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "c") continue;
                    var r = sub.GetAttribute("r");
                    var t = sub.GetAttribute("t");
                    var sAttr = sub.GetAttribute("s");
                    int col = r != null ? ColIndex(r) : row.Count;
                    string value = "";
                    if (!sub.IsEmptyElement)
                    {
                        using var cell = sub.ReadSubtree();
                        var sb = new StringBuilder();
                        string? v = null;
                        while (cell.Read())
                        {
                            if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v") v = cell.ReadElementContentAsString();
                            else if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "t") sb.Append(cell.ReadElementContentAsString());
                        }
                        value = t switch
                        {
                            "s" => int.TryParse(v, out var si) && si < shared.Count ? shared[si] : "",
                            "inlineStr" => sb.ToString(),
                            "b" => v == "1" ? "TRUE" : "FALSE",
                            "str" or "e" => v ?? "",
                            _ => FormatNumber(v, sAttr, dateStyles)
                        };
                    }
                    while (row.Count < col) row.Add("");
                    if (row.Count == col) row.Add(value); else row[col] = value;
                }
                yield return row.ToArray();
            }
        }
    }

    private static string FormatNumber(string? v, string? style, HashSet<int> dateStyles)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (style != null && int.TryParse(style, out var si) && dateStyles.Contains(si)
            && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && d < 2958466)
        {
            var dt = DateTime.FromOADate(d);
            return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        // Keep integers without a trailing ".0"
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && Math.Abs(n % 1) < double.Epsilon && Math.Abs(n) < 1e15)
            return ((long)n).ToString(CultureInfo.InvariantCulture);
        return v;
    }

    public static int ColIndex(string cellRef)
    {
        int col = 0;
        foreach (var ch in cellRef)
        {
            if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        return Math.Max(0, col - 1);
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var e = zip.GetEntry("xl/sharedStrings.xml");
        if (e == null) return list;
        using var s = e.Open();
        using var xr = XmlReader.Create(s, Xs);
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si")
            {
                var sb = new StringBuilder();
                if (!xr.IsEmptyElement)
                {
                    using var sub = xr.ReadSubtree();
                    bool inPhonetic = false;
                    while (sub.Read())
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "rPh") inPhonetic = true;
                        else if (sub.NodeType == XmlNodeType.EndElement && sub.LocalName == "rPh") inPhonetic = false;
                        else if (!inPhonetic && sub.NodeType == XmlNodeType.Element && sub.LocalName == "t") sb.Append(sub.ReadElementContentAsString());
                    }
                }
                list.Add(sb.ToString());
            }
        }
        return list;
    }

    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        var e = zip.GetEntry("xl/styles.xml");
        if (e == null) return result;
        var doc = new XmlDocument { XmlResolver = null };
        using (var s = e.Open()) using (var xr = XmlReader.Create(s, Xs)) doc.Load(xr);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var custom = new Dictionary<int, string>();
        foreach (XmlElement nf in doc.SelectNodes("//x:numFmts/x:numFmt", ns)!)
            if (int.TryParse(nf.GetAttribute("numFmtId"), out var id)) custom[id] = nf.GetAttribute("formatCode");
        int idx = 0;
        foreach (XmlElement xf in doc.SelectNodes("//x:cellXfs/x:xf", ns)!)
        {
            if (int.TryParse(xf.GetAttribute("numFmtId"), out var fid))
            {
                bool isDate = (fid >= 14 && fid <= 22) || (fid >= 45 && fid <= 47) || (fid >= 27 && fid <= 36) || (fid >= 50 && fid <= 58);
                if (custom.TryGetValue(fid, out var code))
                {
                    var c = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", "").ToLowerInvariant();
                    isDate = c.Contains('y') || c.Contains('d') || (c.Contains('m') && (c.Contains('h') || c.Contains('s'))) || c.Contains("mmm");
                }
                if (isDate) result.Add(idx);
            }
            idx++;
        }
        return result;
    }

    /// <summary>Worksheet entry paths in workbook order, visible sheets first (hidden ones last).</summary>
    private static List<string> SheetPaths(ZipArchive zip)
    {
        var wb = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Not a valid .xlsx file (workbook.xml missing).");
        var doc = new XmlDocument { XmlResolver = null };
        using (var s = wb.Open()) using (var xr = XmlReader.Create(s, Xs)) doc.Load(xr);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var targets = new Dictionary<string, string>();
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (rels != null)
        {
            var rd = new XmlDocument { XmlResolver = null };
            using (var s = rels.Open()) using (var xr = XmlReader.Create(s, Xs)) rd.Load(xr);
            foreach (XmlElement rel in rd.GetElementsByTagName("Relationship"))
            {
                var target = rel.GetAttribute("Target").TrimStart('/');
                targets[rel.GetAttribute("Id")] = target.StartsWith("xl/") ? target : "xl/" + target;
            }
        }
        var visible = new List<string>(); var hidden = new List<string>(); int n = 0;
        foreach (XmlElement sheet in doc.SelectNodes("//x:sheets/x:sheet", ns)!)
        {
            n++;
            var rid = sheet.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            var path = targets.TryGetValue(rid, out var t) ? t : $"xl/worksheets/sheet{n}.xml";
            if (zip.GetEntry(path) == null) continue;   // chart sheets and the like
            (sheet.GetAttribute("state") is "hidden" or "veryHidden" ? hidden : visible).Add(path);
        }
        if (visible.Count + hidden.Count == 0 && zip.GetEntry("xl/worksheets/sheet1.xml") != null) visible.Add("xl/worksheets/sheet1.xml");
        return visible.Concat(hidden).ToList();
    }
}

/// <summary>Streaming .xlsx writer (inline strings), suitable for reports with hundreds of thousands of rows.</summary>
public sealed class XlsxWriter : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly List<string> _sheets = new();
    private XmlWriter? _xw;
    private Stream? _entryStream;
    private int _row;

    public XlsxWriter(Stream output) { _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true); }

    public void BeginSheet(string name, IReadOnlyList<string> headers, IReadOnlyList<double>? widths = null)
    {
        EndSheet();
        _sheets.Add(SafeSheetName(name));
        var e = _zip.CreateEntry($"xl/worksheets/sheet{_sheets.Count}.xml", CompressionLevel.Fastest);
        _entryStream = e.Open();
        _xw = XmlWriter.Create(_entryStream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        _xw.WriteStartDocument(true);
        _xw.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        _xw.WriteStartElement("sheetViews"); _xw.WriteStartElement("sheetView"); _xw.WriteAttributeString("workbookViewId", "0");
        _xw.WriteStartElement("pane"); _xw.WriteAttributeString("ySplit", "1"); _xw.WriteAttributeString("topLeftCell", "A2"); _xw.WriteAttributeString("activePane", "bottomLeft"); _xw.WriteAttributeString("state", "frozen"); _xw.WriteEndElement();
        _xw.WriteEndElement(); _xw.WriteEndElement();
        _xw.WriteStartElement("cols");
        for (int i = 0; i < headers.Count; i++)
        {
            _xw.WriteStartElement("col");
            _xw.WriteAttributeString("min", (i + 1).ToString()); _xw.WriteAttributeString("max", (i + 1).ToString());
            var w = widths != null && i < widths.Count ? widths[i] : Math.Clamp(headers[i].Length + 6, 12, 48);
            _xw.WriteAttributeString("width", w.ToString(CultureInfo.InvariantCulture)); _xw.WriteAttributeString("customWidth", "1");
            _xw.WriteEndElement();
        }
        _xw.WriteEndElement();
        _xw.WriteStartElement("sheetData");
        _row = 0;
        WriteRow(headers, bold: true);
    }

    public void WriteRow(IReadOnlyList<string?> values, bool bold = false)
    {
        if (_xw == null) throw new InvalidOperationException("Call BeginSheet first.");
        _row++;
        _xw.WriteStartElement("row"); _xw.WriteAttributeString("r", _row.ToString());
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (string.IsNullOrEmpty(v)) continue;
            _xw.WriteStartElement("c");
            _xw.WriteAttributeString("r", ColName(i) + _row);
            if (bold) _xw.WriteAttributeString("s", "1");
            _xw.WriteAttributeString("t", "inlineStr");
            _xw.WriteStartElement("is"); _xw.WriteStartElement("t");
            _xw.WriteAttributeString("xml", "space", null, "preserve");
            _xw.WriteString(CleanXml(v));
            _xw.WriteEndElement(); _xw.WriteEndElement(); _xw.WriteEndElement();
        }
        _xw.WriteEndElement();
    }

    private void EndSheet()
    {
        if (_xw == null) return;
        _xw.WriteEndElement(); // sheetData
        _xw.WriteEndElement(); // worksheet
        _xw.WriteEndDocument(); _xw.Flush(); _xw.Dispose(); _entryStream!.Dispose();
        _xw = null; _entryStream = null;
    }

    private static string CleanXml(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (XmlConvert.IsXmlChar(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static string SafeSheetName(string n)
    {
        foreach (var c in new[] { '\\', '/', '?', '*', '[', ']', ':' }) n = n.Replace(c, ' ');
        return n.Length > 31 ? n[..31] : n;
    }

    public static string ColName(int i)
    {
        var s = ""; i++;
        while (i > 0) { int m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; }
        return s;
    }

    private void Put(string path, string xml)
    {
        var e = _zip.CreateEntry(path, CompressionLevel.Fastest);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(xml);
    }

    public void Dispose()
    {
        EndSheet();
        var sheetsXml = new StringBuilder();
        var relsXml = new StringBuilder();
        var ctXml = new StringBuilder();
        for (int i = 0; i < _sheets.Count; i++)
        {
            sheetsXml.Append($"<sheet name=\"{System.Security.SecurityElement.Escape(_sheets[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            relsXml.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            ctXml.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }
        int n = _sheets.Count;
        Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" + ctXml + "</Types>");
        Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Put("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" + sheetsXml + "</sheets></workbook>");
        Put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + relsXml + $"<Relationship Id=\"rId{n + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Put("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs></styleSheet>");
        _zip.Dispose();
    }
}

public static class Csv
{
    public static IEnumerable<string[]> ReadRows(string path)
    {
        using var sr = new StreamReader(path, DetectEncoding(path), detectEncodingFromByteOrderMarks: true);
        char delim = ',';
        var first = sr.ReadLine();
        if (first == null) yield break;
        int commas = first.Count(c => c == ','), semis = first.Count(c => c == ';'), tabs = first.Count(c => c == '\t');
        if (tabs > commas && tabs >= semis) delim = '\t'; else if (semis > commas) delim = ';';
        sr.BaseStream.Seek(0, SeekOrigin.Begin); sr.DiscardBufferedData();
        var field = new StringBuilder(); var row = new List<string>(); bool inQ = false;
        int c;
        while ((c = sr.Read()) != -1)
        {
            char ch = (char)c;
            if (ch == '﻿' && field.Length == 0 && row.Count == 0) continue;
            if (inQ)
            {
                if (ch == '"') { if (sr.Peek() == '"') { sr.Read(); field.Append('"'); } else inQ = false; }
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) inQ = true;
            else if (ch == delim) { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(field.ToString()); field.Clear(); yield return row.ToArray(); row.Clear(); }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); yield return row.ToArray(); }
    }

    /// <summary>UTF-8 unless the file is not valid UTF-8 (a CSV saved by Excel on Windows): then Windows-1252.</summary>
    public static Encoding DetectEncoding(string path)
    {
        var strict = new UTF8Encoding(false, true).GetDecoder();
        var buf = new byte[1 << 16]; var chars = new char[(1 << 16) + 4];
        using var fs = File.OpenRead(path);
        int n;
        try
        {
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) strict.GetChars(buf, 0, n, chars, 0, flush: false);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
    }

    public static string Escape(string? v)
    {
        v ??= "";
        // Guard against spreadsheet formula injection when opened in Excel
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static void WriteRow(TextWriter w, IEnumerable<string?> values) => w.Write(string.Join(",", values.Select(Escape)) + "\r\n");
}
