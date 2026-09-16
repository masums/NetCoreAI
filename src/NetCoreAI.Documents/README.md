# NetCoreAI.Documents

Document extractors for NetCoreAI: PDF (PdfPig), DOCX/PPTX/XLSX (Open XML) and HTML (AngleSharp).

```csharp
builder.Services.AddNetCoreAI().AddDocumentExtractors();
```

Plain text, Markdown, CSV and JSON need nothing extra — `NetCoreAI.Core` reads those itself. This package exists so a host that only ever ingests Markdown does not carry a PDF parser it never calls.

Extracted text keeps the page and section it came from, so a citation can point at a page number rather than at a file.

Part of [NetCoreAI](https://github.com/masums/NetCoreAI): turn any existing ASP.NET Core app into an AI-enabled application (local models, RAG, tools and agents) with two lines of code.

```csharp
builder.Services.AddNetCoreAI();
app.MapNetCoreAI();   // dashboard + APIs at /netcoreai
```

Documentation, plans and design decisions live in the repository under `docs/`.
