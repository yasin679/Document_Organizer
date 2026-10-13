# Documentation

| ID | Document | For | Read online | PDF |
|---|---|---|---|---|
| DO-UG-01 | User Guide | Operators, viewers, administrators | [user-guide.md](user-guide.md) | [PDF](pdf/DO-UG-01_User_Guide.pdf) |
| DO-IG-02 | Installation and Administration Guide | Administrators, cloud and infrastructure teams | [installation-and-administration.md](installation-and-administration.md) | [PDF](pdf/DO-IG-02_Installation_and_Administration_Guide.pdf) |
| DO-QS-03 | Quick Start | New operators | [quick-start.md](quick-start.md) | [PDF](pdf/DO-QS-03_Quick_Start.pdf) |
| DO-FAQ-04 | FAQ and Troubleshooting | Everyone | [faq-and-troubleshooting.md](faq-and-troubleshooting.md) | [PDF](pdf/DO-FAQ-04_FAQ_and_Troubleshooting.pdf) |
| DO-TD-05 | Technical Design | Developers, reviewers, security | [technical-design.md](technical-design.md) | [PDF](pdf/DO-TD-05_Technical_Design.pdf) |
| DO-UAT-06 | UAT Test Cases | Testers, sign-off owners | [uat-test-cases.md](uat-test-cases.md) | [PDF](pdf/DO-UAT-06_UAT_Test_Cases.pdf) |
| DO-RN-07 | Release Notes v2.0 | Everyone | [release-notes.md](release-notes.md) | [PDF](pdf/DO-RN-07_Release_Notes_v2.0.pdf) |

The Markdown files are the source. The PDFs are generated from them, with screenshots from `images/`:

```bash
pip install markdown pypdf playwright
playwright install chromium
python docs/build/build_pdfs.py
```
