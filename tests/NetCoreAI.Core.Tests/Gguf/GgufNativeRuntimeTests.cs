using NetCoreAI.Backends.Gguf;
using Xunit;

namespace NetCoreAI.Core.Tests.Gguf;

/// <summary>
/// The message a host gets when the GGUF provider is registered but the native llama.cpp library is not
/// there.
/// </summary>
/// <remarks>
/// This is a packaging trap, not a bug, and it is the one failure most likely to meet somebody on their
/// first local model. NuGet does not flow build assets to a consumer of a consumer, and LLamaSharp ships
/// its native binaries only through build targets, so referencing NetCoreAI.Backend.Gguf alone leaves the
/// managed assembly with nothing behind it. Raw, that surfaces as a type initializer failure carrying a
/// four-item checklist that never mentions the one thing to do.
/// </remarks>
public sealed class GgufNativeRuntimeTests
{
    [Fact]
    public void A_missing_native_library_is_recognised_through_the_type_initializer_that_wraps_it()
    {
        // LLamaSharp raises its error from a static constructor, so callers never see it directly.
        var wrapped = new TypeInitializationException("LLama.Native.NativeApi", new InvalidOperationException("native library"));

        Assert.True(GgufModelProvider.IsNativeLoadFailure(wrapped));
    }

    [Theory]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(BadImageFormatException))]
    public void The_other_shapes_a_missing_or_wrong_architecture_library_takes_are_recognised(Type kind)
    {
        Assert.True(GgufModelProvider.IsNativeLoadFailure((Exception)Activator.CreateInstance(kind)!));
    }

    [Fact]
    public void An_ordinary_load_failure_is_not_mistaken_for_a_missing_library()
    {
        // A corrupt file, a file that is not a GGUF, a model too large for memory: all real load failures
        // that must keep their own message rather than being relabelled as a packaging problem.
        Assert.False(GgufModelProvider.IsNativeLoadFailure(new IOException("the file is truncated")));
        Assert.False(GgufModelProvider.IsNativeLoadFailure(new InvalidOperationException("the model needs more memory than is free")));
    }

    [Fact]
    public void The_message_names_the_package_to_add_and_says_whose_project_it_goes_in()
    {
        var message = GgufModelProvider.NativeRuntimeMissing;

        // Naming the package is the whole point: the raw failure lists four possibilities and no action.
        Assert.Contains("LLamaSharp.Backend.Cpu", message, StringComparison.Ordinal);
        Assert.Contains("LLamaSharp.Backend.Cuda12", message, StringComparison.Ordinal);

        // And it has to say the reference goes in the host's own project, because adding it to NetCoreAI
        // is the obvious thing to try and it does not work.
        Assert.Contains("your own project", message, StringComparison.Ordinal);
    }
}
