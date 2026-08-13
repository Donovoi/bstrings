# Document-reading research and roadmap (August 2026)

## Decision

Bstrings provides one user-facing **Full** analysis preset. It should not force
every document through one engine. The defensible local design is:

1. extract native text and object structure first;
2. run coordinate-preserving OCR/layout analysis where native coverage is absent
   or suspect;
3. use a vision-language model only as separately labelled derived enrichment
   for complex or low-confidence pages; and
4. retain every original and derived representation with exact engine, model,
   render, coordinate, and source-hash provenance.

The August 2026 code change keeps the current PP-OCRv6 medium path as canonical,
retires the smaller translation bundle choices, and adds measured OCR and
Magika/FLOSS file percentages. It does **not** add a generative document model
to the release: the available evidence does not yet establish exact forensic
token recall, hallucination rate, determinism, Windows deployment closure, or
better end-to-end wall time on a representative bstrings corpus.

Confidence is high in the routed architecture and medium in the eventual model
choice. The latter remains an empirical question.

## What current systems do

| System | Primary-source behavior | Relevant lesson | Boundary |
| --- | --- | --- | --- |
| Microsoft Document Intelligence | Read/Layout returns spans, pages, polygons, confidence, tables, figures, sections, handwriting and selection marks; scaling guidance uses asynchronous requests, bounded concurrency, retry and polling | Preserve structure and provenance; pipeline independent pages/documents instead of seeking 100% utilization | Local containers still require Azure metering connectivity; Office embedded images and some unrendered coordinates are limited |
| Google Document AI | Enterprise OCR combines native PDF parsing, OCR, rotation/deskew, language and quality signals; the newer layout parser grounds Gemini annotation in specialized OCR | Native/OCR grounding before VLM interpretation is the strongest shared architecture | Cloud/data-residency and format/version limits make it unsuitable as the default air-gapped evidence path |
| OpenAI file inputs | PDFs supply extracted text plus page images; non-PDF documents supply text only, and embedded images/charts are omitted unless converted to PDF | Hybrid text+vision is useful, and document type must control routing | It is a cloud model path, not byte-offset evidence; model output still requires grounding and validation |
| PaddlePaddle / Hugging Face ecosystem | PP-OCRv6 provides conventional text boxes/scores; PP-StructureV3 adds structure; PaddleOCR-VL-1.6 and OvisOCR2 are compact page-to-structure/VLM candidates | Keep deterministic OCR as canonical and benchmark VLM/layout output as a supplement | Headline page-to-Markdown scores do not measure exact forensic identifier recall or hallucinated artifacts |

Primary sources: Microsoft [analyze response](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/concept/analyze-document-response?view=doc-intel-4.0.0),
[Layout](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/prebuilt/layout?view=doc-intel-4.0.0),
[service limits](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/service-limits?view=doc-intel-4.0.0), and
[containers](https://learn.microsoft.com/en-us/azure/ai-services/document-intelligence/containers/install-run?tabs=custom&view=doc-intel-4.0.0);
Google [Enterprise Document OCR](https://docs.cloud.google.com/document-ai/docs/enterprise-document-ocr),
[response schema](https://docs.cloud.google.com/document-ai/docs/handle-response), and
[layout parser](https://docs.cloud.google.com/document-ai/docs/layout-parse-chunk);
OpenAI [file inputs](https://developers.openai.com/api/docs/guides/file-inputs) and
[vision limitations](https://developers.openai.com/api/docs/guides/images-vision);
PaddlePaddle [PP-OCRv6](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/algorithm/PP-OCRv6/PP-OCRv6.md) and
[PaddleOCR-VL-1.6](https://huggingface.co/PaddlePaddle/PaddleOCR-VL-1.6);
and [OvisOCR2](https://huggingface.co/ATH-MaaS/OvisOCR2).

## Why one large VLM is not the Full preset

The strongest contrary proposal is to replace native parsing and OCR with the
highest-scoring compact VLM. It fails the current forensic requirements:

- native PDF text and coordinates can be recovered far faster and without
  generative reconstruction;
- PP-OCRv6 documents that language-prior “correction” can insert text absent
  from an image—particularly dangerous for hashes, JWTs, URLs, GUIDs, Registry
  paths, credentials, and Base64;
- OvisOCR2's own model card warns that output can be incorrect or incomplete;
- page-to-Markdown output lacks canonical word-level confidence and byte/source
  attribution; and
- [PureDocBench](https://github.com/zhihengli-casia/PureDocBench) reports much
  lower absolute performance and ranking changes on degraded real documents
  than headline OmniDocBench results suggest.

Therefore a future VLM result must be a derived record with `null/generated`
confidence unless it can be grounded to retained native/OCR spans. It must never
silently repair or replace an evidence identifier.

## Changes worth making next

### Priority 1: coverage and correctness

- Add provenance-preserving parsers for OOXML/OLE, ODF, RTF, HTML, text, EML and
  MSG, including embedded/attached children. Enforce archive-expansion, entity,
  macro, path, recursion, and size limits.
- Replace the current PDF “32 characters / suspicious controls” decision with
  visual/text coverage reconciliation. Retain both text-layer and OCR records
  when they disagree.
- Add line, paragraph, table, and adjacency-derived records while preserving
  each original OCR hit, so patterns split across boxes can match without
  losing the underlying evidence.

### Priority 2: throughput

- Build a bounded `parse/render/decode -> inference -> ordered write` pipeline so
  CPU preparation overlaps one resident GPU session.
- Batch compatible page/region dimensions and benchmark recognizer batching.
- Cache by source hash, embedded path, page/object, renderer configuration, and
  exact engine/model hash.
- Do not select OCR/GPU by total file size. Use format, native-text density,
  visual coverage, page quality, language/script, layout complexity, and model
  confidence. A 1 GiB cutoff has no evidentiary or performance basis.
- Do not increase DirectML session count or fixed hybrid weighting merely to
  raise utilization. On the existing receipt diagnostic, DirectML measured
  1.2452 documents/s and hybrid 1.2319 documents/s; its integrity gate also
  failed, so this is performance guidance rather than an acceptance claim.

### Candidate benchmark, not release dependencies

Benchmark the current PP-OCRv6 path, PP-StructureV3, Tesseract, PaddleOCR-VL-1.6,
OvisOCR2, and routed combinations. Include born-digital and malformed PDFs,
Office/email containers, scans, handwriting, rotation, mixed scripts, columns,
tables/charts, and exact synthetic forensic identifiers.

Predeclare and measure:

- exact identifier and pattern precision/recall;
- hallucinated-artifact and missing-page rates;
- CER/WER, reading order, table structure, and coordinate accuracy;
- page/byte coverage and provenance completeness;
- ten-run determinism on fixed hardware/runtime versions;
- cold/warm pages per second, p50/p95 latency, RAM/VRAM, and CPU/GPU/I/O; and
- complete Windows offline packaging and licence closure.

Adopt a VLM/layout candidate only if the routed result materially improves exact
forensic recall without weakening attribution or false-artifact limits. Reverse
the staged design only if a pure local VLM matches or beats it across every
accuracy and provenance gate while also reducing representative wall time.
