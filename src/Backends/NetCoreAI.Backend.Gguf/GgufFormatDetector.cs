namespace NetCoreAI.Backends.Gguf;

/// <summary>
/// Recognises GGUF files for import and download, reading the header rather than trusting the extension.
/// </summary>
internal sealed class GgufFormatDetector : IModelFormatDetector
{
    public DetectedModel? TryDetect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        GgufMetadata metadata;
        try
        {
            metadata = GgufMetadataReader.Read(path);
        }
        catch (Exception ex) when (ex is GgufFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return new DetectedModel(ModelFormat.Gguf, GgufModelProvider.ProviderId)
        {
            Family = metadata.Architecture,
            Quantization = metadata.Quantization,
            ContextLength = metadata.ContextLength,
            ParameterCount = metadata.ParameterCount,
            SizeBytes = Size(path),
            ChatTemplate = metadata.ChatTemplate,
            Capabilities = metadata.ToCapabilities(),
            Name = metadata.Name,
        };
    }

    private static long? Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
