#!/usr/bin/env python3
"""
Builds the PDF versions of the guides in docs/ from their Markdown sources.

    pip install markdown pypdf playwright && playwright install chromium
    python docs/build/build_pdfs.py            # all documents
    python docs/build/build_pdfs.py user-guide # one document

Each PDF gets a cover, a document-control page, a table of contents with page numbers,
running headers and footers. Output goes to docs/pdf/.
"""
import html
import io
import re
import sys
from pathlib import Path

import markdown
from playwright.sync_api import sync_playwright
from pypdf import PdfReader, PdfWriter

DOCS = Path(__file__).resolve().parent.parent
OUT = DOCS / "pdf"
PRODUCT = "Document Organizer"
VERSION = "2.1"
DATE = "22-Oct-2026"
AUTHOR = "Yasin Jariwala"
CLASSIFICATION = "Public"

# Order, file names of the published PDFs.
DOCUMENTS = [
    ("user-guide", "DO-UG-01_User_Guide"),
    ("installation-and-administration", "DO-IG-02_Installation_and_Administration_Guide"),
    ("quick-start", "DO-QS-03_Quick_Start"),
    ("faq-and-troubleshooting", "DO-FAQ-04_FAQ_and_Troubleshooting"),
    ("technical-design", "DO-TD-05_Technical_Design"),
    ("uat-test-cases", "DO-UAT-06_UAT_Test_Cases"),
    ("release-notes", "DO-RN-07_Release_Notes_v2.1"),
]

HISTORY = [
    ("1.0", "06-Oct-2026", AUTHOR, "First release"),
    ("2.0", "13-Oct-2026", AUTHOR, "General-purpose release: Amazon S3 and S3-compatible storage, copy between providers, "
                          "container hosting; tenant ID field removed"),
    ("2.1", DATE, AUTHOR, "Several mapping files per job; File name cells listing several files split into one entry per file; "
                          "NULL placeholders treated as blank; repeats of missing records reported as MISSING"),
]

# Narrower display widths for small screenshots (percentage of the text width).
IMAGE_WIDTH = {"02-sign-in.jpg": 52, "07-demo-shortcuts.jpg": 85, "15-resolve.jpg": 52, "17-approve-confirm.jpg": 62,
               "19-paused.jpg": 92, "23-users.jpg": 92}

CALLOUT_TITLES = {"NOTE": "Note", "TIP": "Tip", "IMPORTANT": "Important", "WARNING": "Warning", "CAUTION": "Caution"}


def front_matter(text):
    m = re.match(r"^---\n(.*?)\n---\n", text, re.S)
    meta = {}
    if m:
        for line in m.group(1).splitlines():
            k, _, v = line.partition(":")
            meta[k.strip()] = v.strip()
        text = text[m.end():]
    return meta, text


def callouts(text):
    """GitHub alert blocks (> [!NOTE] ...) to <div class="callout ..."> with Markdown inside."""
    out, lines, i = [], text.split("\n"), 0
    while i < len(lines):
        m = re.match(r"^> \[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", lines[i])
        if not m:
            out.append(lines[i]); i += 1; continue
        kind, body = m.group(1), []
        i += 1
        while i < len(lines) and lines[i].startswith(">"):
            body.append(lines[i][2:] if lines[i].startswith("> ") else lines[i][1:]); i += 1
        inner = md_to_html("\n".join(body))
        out.append(f'\n<div class="callout {kind.lower()}">{inner}</div>\n')
    return "\n".join(out)


def md_to_html(text):
    return markdown.markdown(text, extensions=["tables", "fenced_code", "sane_lists"], output_format="html5")


def figures(body):
    """A paragraph holding only an image becomes a figure with its alt text as the caption."""
    def fig(m):
        alt, src = html.unescape(m.group(1)), m.group(2)
        width = IMAGE_WIDTH.get(Path(src).name, 100)
        return (f'<figure><img src="{src}" style="max-width:{width}%" alt="{html.escape(alt)}">'
                f'<figcaption>{html.escape(alt)}</figcaption></figure>')
    return re.sub(r'<p><img alt="([^"]*)" src="([^"]+)"\s*/?></p>', fig, body)


def body_html(md_text):
    text = callouts(md_text)
    text = re.sub(r"^# .*\n", "", text, count=1, flags=re.M)  # the title is on the cover
    body = md_to_html(text)
    body = figures(body)
    # anchors for the table of contents
    heads = []
    def anchor(m):
        level, title = int(m.group(1)), m.group(2)
        hid = f"h{len(heads)}"
        heads.append((level, re.sub(r"<[^>]+>", "", title), hid))
        cls = ' class="chapter"' if level == 2 else ""
        return f'<h{level} id="{hid}"{cls}>{title}</h{level}>'
    body = re.sub(r"<h([23])>(.*?)</h\1>", anchor, body)
    body = re.sub(r"<table>(\s*<thead>\s*<tr>\s*<th>ID</th>\s*<th>Pri</th>)", r'<table class="cases">\1', body)
    # status words as small pills, and table column classes
    body = re.sub(r"<td>(READY|PROCESSED|UNMAPPED|MISSING|AMBIGUOUS|DUPLICATE|INVALID|FAILED)</td>",
                  r'<td><span class="st st-\1">\1</span></td>', body)
    body = re.sub(r"<td>(\d+)</td>", r'<td class="num">\1</td>', body)
    return body, heads


def related_rows(current_id):
    rows = []
    for slug, _ in DOCUMENTS:
        meta, _ = front_matter((DOCS / f"{slug}.md").read_text(encoding="utf-8"))
        if meta["id"] != current_id:
            rows.append(f"<tr><td>{meta['id']}</td><td>{html.escape(meta['title'])}</td></tr>")
    return "".join(rows)


CSS = """
@page { size: A4; }
:root { --ink:#15262b; --ink2:#4b5f65; --ink3:#6e8186; --line:#d9e0e2; --soft:#eef2f3; --accent:#0d6b73; --accentsoft:#dcefef; --rail:#0f2a30;
  --ok:#1f7a45; --oksoft:#e1f2e7; --warn:#9a5c00; --warnsoft:#fbefd9; --bad:#b4312b; --badsoft:#f8e1df; --info:#2257a6; --infosoft:#e2ebf8; --vio:#6a3fa6; --viosoft:#eee6f8; }
* { box-sizing: border-box; }
html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
body { margin: 0; font: 10pt/1.5 "Inter", "Segoe UI", system-ui, sans-serif; color: var(--ink); }
h2, h3, h4 { font-family: "Inter Display", "Inter", sans-serif; color: var(--rail); break-after: avoid; }
h2.chapter { font-size: 19pt; font-weight: 650; margin: 0 0 10pt; padding-bottom: 6pt; border-bottom: 2.5pt solid var(--accent); break-before: page; }
h2.chapter.first { break-before: auto; }
.flow h2.chapter { break-before: auto; margin-top: 20pt; }
h3 { font-size: 12.5pt; font-weight: 650; margin: 16pt 0 6pt; color: var(--accent); }
p { margin: 0 0 7pt; }
ul, ol { margin: 0 0 8pt; padding-left: 18pt; } li { margin: 0 0 3pt; }
a { color: var(--accent); text-decoration: none; }
code { font: 8.6pt "Cascadia Mono", "DejaVu Sans Mono", monospace; background: var(--soft); border: .5pt solid var(--line); border-radius: 3pt; padding: 0 2.5pt; color: #0b4f55; }
pre { background: #f6f8f9; border: .6pt solid var(--line); border-left: 3pt solid var(--accent); border-radius: 4pt; padding: 7pt 9pt; margin: 0 0 9pt;
  white-space: pre-wrap; word-break: break-word; break-inside: avoid; }
pre code { background: none; border: 0; padding: 0; font-size: 8.2pt; line-height: 1.45; color: var(--ink); }
table { width: 100%; border-collapse: collapse; margin: 4pt 0 11pt; font-size: 8.8pt; }
thead { display: table-header-group; }
th { background: var(--rail); color: #fff; text-align: left; font-weight: 600; padding: 5pt 6pt; font-size: 8.4pt; }
td { padding: 4.5pt 6pt; border-bottom: .6pt solid var(--line); vertical-align: top; }
tr { break-inside: avoid; }
tbody tr:nth-child(even) td { background: #f8fafa; }
figure { margin: 6pt 0 10pt; text-align: center; break-inside: avoid; break-after: avoid; }
figure img { width: auto; max-height: 118mm; border: .6pt solid var(--line); border-radius: 4pt; box-shadow: 0 1pt 4pt rgba(15,42,48,.10); }
figcaption { font-size: 8.4pt; color: var(--ink3); font-style: italic; margin-top: 4pt; }
.callout { border-radius: 5pt; padding: 8pt 11pt 4pt; margin: 4pt 0 11pt; border-left: 3.5pt solid; break-inside: avoid; }
.callout p:first-child strong:first-child { display: block; margin-bottom: 1pt; }
.callout.note { background: var(--infosoft); border-color: var(--info); }
.callout.tip { background: var(--oksoft); border-color: var(--ok); }
.callout.important { background: var(--accentsoft); border-color: var(--accent); }
.callout.warning { background: var(--warnsoft); border-color: var(--warn); }
.callout.caution { background: var(--badsoft); border-color: var(--bad); }
.st { display: inline-block; font: 700 7pt "Inter", sans-serif; letter-spacing: .03em; padding: 1.5pt 5pt; border-radius: 8pt; white-space: nowrap; }
.st-READY { background: var(--infosoft); color: var(--info); } .st-PROCESSED { background: var(--oksoft); color: var(--ok); }
.st-UNMAPPED, .st-INVALID { background: var(--warnsoft); color: var(--warn); } .st-MISSING, .st-FAILED { background: var(--badsoft); color: var(--bad); }
.st-AMBIGUOUS { background: var(--viosoft); color: var(--vio); } .st-DUPLICATE { background: #e7ecee; color: #536671; }
/* marker-number column in screen tables */
td.mk { width: 20pt; text-align: center; padding-top: 4pt; }
td.mk span { display: inline-block; width: 14pt; height: 14pt; line-height: 14pt; border-radius: 50%; background: #EA580C; color: #fff; font-weight: 700; font-size: 7.5pt; }
.compact table { font-size: 8pt; } .compact td { padding: 3.5pt 5pt; }
/* UAT case tables: ID, Pri, Test, Steps, Expected result, P/F */
.compact table.cases { table-layout: fixed; }
.compact table.cases th:nth-child(1) { width: 11%; } .compact table.cases th:nth-child(2) { width: 5%; }
.compact table.cases th:nth-child(3) { width: 15%; } .compact table.cases th:nth-child(4) { width: 32%; }
.compact table.cases th:nth-child(5) { width: 31%; } .compact table.cases th:nth-child(6) { width: 6%; }
.compact table.cases td:first-child { white-space: nowrap; font-weight: 600; color: var(--accent); }
/* front pages */
.front h2 { font-size: 15pt; margin: 0 0 8pt; color: var(--rail); border: 0; }
.front table td:first-child { width: 34%; color: var(--ink2); font-weight: 600; }
.front section { margin-bottom: 20pt; }
.toc { margin-top: 2pt; }
.toc div { display: flex; align-items: baseline; gap: 4pt; padding: 1.3pt 0; }
.toc .l2 { font-weight: 600; margin-top: 3.5pt; color: var(--rail); }
.toc .l3 { padding-left: 16pt; color: var(--ink2); font-size: 9pt; }
.toc .dots { flex: 1; border-bottom: .8pt dotted #b9c6c9; transform: translateY(-3pt); }
.toc .pg { min-width: 14pt; text-align: right; font-variant-numeric: tabular-nums; }
"""

COVER_CSS = """
@page { size: A4; margin: 0; }
html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
body { margin: 0; font-family: "Inter", "Segoe UI", sans-serif; }
.cover { width: 210mm; height: 296.5mm; background: #0f2a30; color: #fff; position: relative; overflow: hidden; }
.cover .band { position: absolute; left: 0; right: 0; top: 0; height: 7mm; background: #0d6b73; }
.cover .ring { position: absolute; right: -70mm; top: 70mm; width: 210mm; height: 210mm; border-radius: 50%; border: 26mm solid rgba(77,179,186,.10); }
.cover .ring2 { position: absolute; right: -10mm; top: 130mm; width: 90mm; height: 90mm; border-radius: 50%; border: 10mm solid rgba(77,179,186,.08); }
.cover .brand { position: absolute; left: 22mm; top: 32mm; display: flex; gap: 5mm; align-items: center; }
.cover .mark { width: 15mm; height: 15mm; border-radius: 3.2mm; background: #0d6b73; display: grid; place-items: center; font: 700 15pt "DejaVu Sans Mono", monospace; }
.cover .brand b { display: block; font: 700 19pt "Inter Display", "Inter", sans-serif; }
.cover .brand small { display: block; color: #86a5a9; letter-spacing: .14em; font-size: 8pt; text-transform: uppercase; margin-top: 1mm; }
.cover .titles { position: absolute; left: 22mm; right: 30mm; top: 118mm; }
.cover .eyebrow { color: #7fd0d6; letter-spacing: .18em; font-size: 9pt; font-weight: 700; text-transform: uppercase; margin-bottom: 6mm; }
.cover h1 { font: 700 34pt/1.08 "Inter Display", "Inter", sans-serif; margin: 0 0 6mm; }
.cover .sub { font-size: 13pt; color: #cfe2e3; line-height: 1.45; max-width: 140mm; }
.cover .meta { position: absolute; left: 22mm; right: 22mm; bottom: 34mm; border-left: 1.2mm solid #0d6b73; padding: 1mm 0 1mm 6mm; font-size: 9.5pt; color: #cfe2e3; line-height: 1.75; }
.cover .meta b { color: #fff; }
.cover .foot { position: absolute; left: 22mm; right: 22mm; bottom: 14mm; display: flex; justify-content: space-between; font-size: 8pt; color: #86a5a9;
  border-top: .3mm solid rgba(255,255,255,.18); padding-top: 3mm; }
"""


def marker_tables(body):
    """In tables whose first header is '#', render the number as an orange marker like the screenshots."""
    def fix(table):
        if not re.search(r"<thead>\s*<tr>\s*<th>#</th>", table):
            return table
        table = table.replace("<th>#</th>", '<th style="width:20pt;text-align:center">#</th>', 1)
        return re.sub(r'<tr>\s*<td class="num">(\d+)</td>', r'<tr><td class="mk"><span>\1</span></td>', table)
    body = re.sub(r"<table>.*?</table>",
                  lambda m: fix(m.group(0)) if m.string[:m.start()].rstrip().endswith("</figure>") else m.group(0), body, flags=re.S)
    # remaining numbers in tables are ordinary text
    return body.replace('<td class="num">', "<td>")


def build(slug, out_name, browser):
    meta, md_text = front_matter((DOCS / f"{slug}.md").read_text(encoding="utf-8"))
    body, heads = body_html(md_text)
    body = marker_tables(body)
    body = body.replace('<h2 id="h0" class="chapter">', '<h2 id="h0" class="chapter first">', 1)
    title, doc_id = meta["title"], meta["id"]
    compact = (" compact" if slug == "uat-test-cases" else "") + (" flow" if slug in ("uat-test-cases", "release-notes") else "")

    def page_html(toc_pages):
        toc = "".join(
            f'<div class="l{lvl}"><span>{html.escape(t)}</span><span class="dots"></span><span class="pg">{toc_pages.get(hid, "")}</span></div>'
            for lvl, t, hid in heads)
        history = "".join(f"<tr><td>{v}</td><td>{d}</td><td>{a}</td><td>{html.escape(c)}</td></tr>" for v, d, a, c in HISTORY)
        front = f"""
<div class="front">
<section><h2>Document control</h2><table><tbody>
<tr><td>Document</td><td>{html.escape(title)}</td></tr><tr><td>Document ID</td><td>{doc_id}</td></tr>
<tr><td>Version</td><td>{VERSION}</td></tr><tr><td>Date</td><td>{DATE}</td></tr>
<tr><td>Prepared by</td><td>{AUTHOR}</td></tr><tr><td>Product</td><td>{PRODUCT} v{VERSION}</td></tr>
<tr><td>Audience</td><td>{html.escape(meta.get('audience', ''))}</td></tr><tr><td>Classification</td><td>{CLASSIFICATION}</td></tr>
<tr><td>Source</td><td>github.com/yasin679/Document_Organizer · docs/{slug}.md</td></tr>
</tbody></table></section>
<section><h2>Version history</h2><table><thead><tr><th style="width:12%">Version</th><th style="width:16%">Date</th><th style="width:20%">Author</th><th>Changes</th></tr></thead><tbody>{history}</tbody></table></section>
<section><h2>Related documents</h2><table><thead><tr><th style="width:22%">ID</th><th>Document</th></tr></thead><tbody>{related_rows(doc_id)}</tbody></table></section>
</div>
<div style="break-before:page"></div>
<div class="front"><h2>Contents</h2><div class="toc">{toc}</div></div>
<div style="break-before:page"></div>
"""
        return f'<!doctype html><html><head><meta charset="utf-8"><style>{CSS}</style></head><body class="{compact}">{front}{body}</body></html>'

    header = (f'<div style="width:100%;font:7.5pt Inter,sans-serif;color:#6e8186;padding:0 16mm;display:flex;justify-content:space-between">'
              f'<span><b style="color:#0d6b73">{PRODUCT}</b></span><span>{html.escape(title)}</span></div>')
    footer = (f'<div style="width:100%;font:7.5pt Inter,sans-serif;color:#6e8186;padding:0 16mm;display:flex;justify-content:space-between">'
              f'<span>{doc_id} · Version {VERSION} · {CLASSIFICATION}</span><span>Page <span class="pageNumber"></span> of <span class="totalPages"></span></span></div>')

    page = browser.new_page()

    tmp = DOCS / f".build-{slug}.html"   # loaded from a file so relative image paths resolve

    def render(html_text):
        tmp.write_text(html_text, encoding="utf-8")
        page.goto(tmp.as_uri(), wait_until="networkidle")
        return page.pdf(format="A4", print_background=True, display_header_footer=True, header_template=header, footer_template=footer,
                        margin={"top": "19mm", "bottom": "17mm", "left": "16mm", "right": "16mm"})

    # Pass 1 finds the page of every heading; pass 2 writes the page numbers into the contents.
    first = render(page_html({}))
    reader = PdfReader(io.BytesIO(first))
    texts = [re.sub(r"\s+", " ", p.extract_text() or "") for p in reader.pages]
    pages, start = {}, 2
    for lvl, t, hid in heads:
        needle = re.sub(r"\s+", " ", t).strip()
        for i in range(start, len(texts)):
            if needle in texts[i]:
                pages[hid] = i + 1
                start = i
                break
    body_pdf = render(page_html(pages))
    tmp.unlink(missing_ok=True)

    meta_line = f"<b>Document ID</b> {doc_id} · <b>Version</b> {VERSION} · <b>Date</b> {DATE}<br><b>Prepared by</b> {AUTHOR}<br><b>Audience</b> {html.escape(meta.get('audience', ''))}"
    cover = f"""<!doctype html><html><head><meta charset="utf-8"><style>{COVER_CSS}</style></head><body><div class="cover">
<div class="band"></div><div class="ring"></div><div class="ring2"></div>
<div class="brand"><div class="mark">DO</div><div><b>{PRODUCT}</b><small>Copy-only document re-organizer</small></div></div>
<div class="titles"><div class="eyebrow">{html.escape(title)}</div><h1>{html.escape(title)}</h1><div class="sub">{html.escape(meta.get('subtitle', ''))}</div></div>
<div class="meta">{meta_line}</div>
<div class="foot"><span>{CLASSIFICATION}</span><span>{PRODUCT} v{VERSION}</span></div></div></body></html>"""
    page.set_content(cover, wait_until="networkidle")
    cover_pdf = page.pdf(format="A4", print_background=True, margin={"top": "0", "bottom": "0", "left": "0", "right": "0"})
    page.close()

    writer = PdfWriter()
    writer.append(PdfReader(io.BytesIO(cover_pdf)), pages=(0, 1))
    writer.append(PdfReader(io.BytesIO(body_pdf)))
    writer.add_metadata({"/Title": f"{PRODUCT} – {title}", "/Author": AUTHOR, "/Subject": f"{doc_id} v{VERSION}", "/Creator": PRODUCT})
    OUT.mkdir(exist_ok=True)
    path = OUT / f"{out_name}.pdf"
    with open(path, "wb") as f:
        writer.write(f)
    print(f"{path.relative_to(DOCS.parent)}  ({len(writer.pages)} pages)")


def main():
    only = set(sys.argv[1:])
    with sync_playwright() as p:
        browser = p.chromium.launch()
        for slug, out_name in DOCUMENTS:
            if not only or slug in only:
                build(slug, out_name, browser)
        browser.close()


if __name__ == "__main__":
    main()
