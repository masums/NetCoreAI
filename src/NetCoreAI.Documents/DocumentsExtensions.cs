using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetCoreAI.Documents;

namespace NetCoreAI;

public static class DocumentsExtensions
{
    /// <summary>
    /// Adds extractors for PDF, Word, PowerPoint, Excel and HTML. Plain text, Markdown, CSV and JSON are
    /// handled by Core already, so a host that only ingests those needs no extra package.
    /// </summary>
    public static NetCoreAIBuilder AddDocumentExtractors(this NetCoreAIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, PdfExtractor>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, DocxExtractor>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, PptxExtractor>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, XlsxExtractor>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, HtmlExtractor>());
        return builder;
    }
}
