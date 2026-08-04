# Microsoft runtime files embedded in FLOSS v3.1.1

This notice covers Microsoft-named runtime files inside the byte-pinned upstream
`floss.exe`. It does not cover, and must not be replaced by, the newer Visual C++
runtime staged separately for bstrings itself.

The reviewed FLOSS executable is 31,866,819 bytes with SHA-256
`3a208ab834b4791e81592d66e91a7f07b3458504484d87c44ed32bc7384df7ef`.
The executable is kept unchanged from Mandiant's `floss-v3.1.1-windows.zip`
release asset. The exact embedded file hashes, sizes, version resources, and
Authenticode identities are recorded in `FLOSS-Embedded-Native-Runtime.tsv`.

## Reviewed Microsoft runtime families

| Embedded files | Version evidence | Authenticode evidence |
| --- | --- | --- |
| `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` | 14.28.29914.0 | Valid Microsoft Corporation signature |
| `MSVCP140_CODECVT_IDS.dll` | 14.40.33810.0 | Valid Microsoft Windows Software Compatibility Publisher signature |
| `MSVCP140.dll` | 14.16.27033.0 | Microsoft version resource; valid Eclipse Foundation signature |
| `ucrtbase.dll` and 38 `api-ms-win-*` forwarders | 10.0.17134.12 | Microsoft version resources; valid Eclipse Foundation signature |

Microsoft's Visual Studio 2017, 2019, and 2022 redistribution lists permit
licensed Visual Studio users to distribute unmodified files from the applicable
`VC\Redist` tree with their programs. The corresponding license terms also
allow an application's distributors to pass those files on as part of the
application, subject to the stated requirements. Exact reference copies of the
three Community-edition license documents used for this review are shipped next
to this notice:

- `Microsoft-Visual-Studio-Community-2017-License.docx`
- `Microsoft-Visual-Studio-Community-2019-License.docx`
- `Microsoft-Visual-Studio-Community-2022-License.docx`

Authoritative online references:

- <https://learn.microsoft.com/en-us/visualstudio/releases/2017/2017-redistribution-vs>
- <https://learn.microsoft.com/en-us/visualstudio/releases/2019/redistribution>
- <https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution>
- <https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment>

The UCRT deployment documentation identifies `ucrtbase.dll` and the
`api-ms-win-*` forwarders as the supported application-local UCRT set. On
Windows 10 and later, Windows uses the operating-system UCRT instead of the
application-local copy.

## Distribution boundary and residual limitation

bstrings distributes the upstream FLOSS executable as one unchanged optional
analysis component. It does not extract, modify, or offer these runtime DLLs as
standalone files. FLOSS adds string-recovery functionality to the larger
bstrings analysis workflow, and the bundle retains the applicable Microsoft
terms and this provenance record.

There is one explicit provenance limitation: 40 older runtime files have
Microsoft version resources but are signed by the Eclipse Foundation rather
than Microsoft. Their names and versions are consistent with redistributable
VC/UCRT families, but this review cannot prove that those exact signed bytes
came unmodified from a Microsoft `VC\Redist` or Windows SDK directory. Their
onward-distribution basis is therefore limited to preserving the official
Mandiant FLOSS artifact byte-for-byte; it is not a claim that bstrings has
independently requalified those DLLs as Microsoft REDIST files.

If a distributor requires direct Microsoft provenance for every embedded byte,
the supported upstream artifact is insufficient evidence. The remedy is a
separately reviewed FLOSS rebuild whose Microsoft inputs and build lock are
captured at build time. Such a rebuild is intentionally outside this release
because it would no longer be the reviewed upstream Mandiant executable.

The bundled documents and links are evidence for the review, not a substitute
for satisfying the license terms that apply to a particular distributor.
