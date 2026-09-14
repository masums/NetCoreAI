namespace NetCoreAI.Backends.Onnx;

/// <summary>
/// Recognises ONNX model folders for import and download: an ONNX Runtime GenAI export, or a
/// sentence-transformers encoder with its tokenizer.
/// </summary>
internal sealed class OnnxFormatDetector : IModelFormatDetector
{
    public DetectedModel? TryDetect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || OnnxModelFolderReader.TryRead(path) is not { } folder)
        {
            return null;
        }

        return new DetectedModel(ModelFormat.Onnx, OnnxModelProvider.ProviderId)
        {
            Family = folder.ModelType,
            Quantization = folder.Quantization,
            ContextLength = folder.ContextLength,
            SizeBytes = folder.GraphBytes,
            ChatTemplate = folder.ChatTemplate,
            Capabilities = folder.ToCapabilities(),
        };
    }
}
