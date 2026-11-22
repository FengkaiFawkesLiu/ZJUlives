# Classroom collection components

Copied from the user's local ZJUSS source (2026-10-24) for a self-contained
Shike integration. No runtime dependency on the Downloads folder.

Includes only the read-only CAS/classroom adapter, local material storage,
PDF/PPTX extraction, and six-section note parsing/prompt. Original namespaces
are retained to make future comparisons straightforward. Access and note
contracts and classroom messages are trimmed to the required types.

Shike owns credentials, matching, task persistence, multi-file selection,
note versions, candidate confirmation, and UI. The original ZJUSS app,
learning database, backup/security systems and note queue are not imported.

PdfPig is Apache-2.0; Open XML SDK is MIT. Their NuGet packages carry licenses.
