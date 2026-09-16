using LLama.Native;
using Microsoft.Extensions.Logging.Abstractions;
using NetCoreAI.Backends.Gguf;
using Xunit;

namespace NetCoreAI.Core.Tests.Gguf;

/// <summary>
/// Keeping llama.cpp's reason so a failed load can repeat it.
/// </summary>
/// <remarks>
/// Reported as "is this my laptop's problem?" after a 4B model would not load. It was not: the file was
/// missing a tensor. llama.cpp said so in its log and the exception did not, because
/// LoadWeightsFailedException carries only the file path. Somebody shown "could not load" with no cause
/// has nothing to go on but their hardware.
/// </remarks>
public sealed class NativeErrorCaptureTests
{
    [Fact]
    public void The_reason_survives_the_load_that_produced_it()
    {
        using var capture = NativeErrorCapture.Begin();

        NativeErrorCapture.Record("llama_model_load: error loading model: missing tensor 'blk.32.ssm_conv1d.weight'");

        Assert.Equal("missing tensor 'blk.32.ssm_conv1d.weight'", capture!.Detail);
    }

    [Fact]
    public void The_line_that_names_the_reason_wins_over_the_one_that_merely_says_it_failed()
    {
        using var capture = NativeErrorCapture.Begin();

        NativeErrorCapture.Record("llama_model_load: error loading model: missing tensor 'blk.32.ssm_conv1d.weight'");
        NativeErrorCapture.Record("llama_model_load_from_file_impl: failed to load model");

        // Taking the last would report "failed to load model", which only repeats what the caller knows.
        Assert.Equal("missing tensor 'blk.32.ssm_conv1d.weight'", capture!.Detail);
    }

    [Fact]
    public void An_error_with_no_recognisable_prefix_is_still_better_than_nothing()
    {
        using var capture = NativeErrorCapture.Begin();

        NativeErrorCapture.Record("unknown model architecture: 'qwen35'");

        Assert.Equal("unknown model architecture: 'qwen35'", capture!.Detail);
    }

    [Fact]
    public void A_load_that_says_nothing_offers_nothing()
    {
        using var capture = NativeErrorCapture.Begin();

        Assert.Null(capture!.Detail);
    }

    [Fact]
    public void A_second_concurrent_load_gets_no_capture_rather_than_the_first_ones_lines()
    {
        using var first = NativeErrorCapture.Begin();
        using var second = NativeErrorCapture.Begin();

        // A load explained by another load's failure is worse than one explained by nothing.
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void Recording_outside_a_load_is_harmless()
    {
        // Every llama.cpp error line reaches Record, including ones emitted while nothing is loading.
        NativeErrorCapture.Record("an error with nobody listening");

        using var capture = NativeErrorCapture.Begin();
        Assert.Null(capture!.Detail);
    }

    [Fact]
    public void A_reason_split_across_fragments_is_reassembled_before_it_is_recorded()
    {
        // llama.cpp does not write lines. It writes pieces, and marks every piece after the first with
        // Continue to mean "still the same line, still its level". Treating each piece as a line would
        // record "missing tensor " and lose the name, which is the only part worth showing.
        var forward = GgufNativeBackend.Forward(NullLogger.Instance);
        using var capture = NativeErrorCapture.Begin();

        forward(LLamaLogLevel.Error, "llama_model_load: error loading ");
        forward(LLamaLogLevel.Continue, "model: missing tensor ");
        forward(LLamaLogLevel.Continue, "'blk.32.ssm_conv1d.weight'\n");

        Assert.Equal("missing tensor 'blk.32.ssm_conv1d.weight'", capture!.Detail);
    }

    [Fact]
    public void An_unfinished_line_is_not_recorded_as_if_it_were_complete()
    {
        var forward = GgufNativeBackend.Forward(NullLogger.Instance);
        using var capture = NativeErrorCapture.Begin();

        forward(LLamaLogLevel.Error, "llama_model_load: error loading model: missing ten");

        // Half a reason is worse than none: it would name the wrong thing with full confidence.
        Assert.Null(capture!.Detail);
    }

    [Fact]
    public void A_continuation_keeps_the_level_of_the_line_it_continues()
    {
        var forward = GgufNativeBackend.Forward(NullLogger.Instance);
        using var capture = NativeErrorCapture.Begin();

        // Continue carries no level of its own. Reading it as one would file the tail of an error
        // somewhere other than with its head.
        forward(LLamaLogLevel.Error, "error loading model: ");
        forward(LLamaLogLevel.Continue, "wrong number of tensors\n");

        Assert.Equal("wrong number of tensors", capture!.Detail);
    }

    [Fact]
    public void Ordinary_progress_chatter_is_not_mistaken_for_a_reason()
    {
        var forward = GgufNativeBackend.Forward(NullLogger.Instance);
        using var capture = NativeErrorCapture.Begin();

        forward(LLamaLogLevel.Info, "llama_model_loader: loaded meta data with 35 key-value pairs\n");
        forward(LLamaLogLevel.Warning, "llama_model_loader: some tensors are in an old format\n");

        Assert.Null(capture!.Detail);
    }

    [Fact]
    public void A_capture_stops_collecting_once_the_load_is_over()
    {
        var capture = NativeErrorCapture.Begin();
        capture!.Dispose();

        NativeErrorCapture.Record("llama_model_load: error loading model: something later");

        using var next = NativeErrorCapture.Begin();
        Assert.NotNull(next);
        Assert.Null(next!.Detail);
    }
}
