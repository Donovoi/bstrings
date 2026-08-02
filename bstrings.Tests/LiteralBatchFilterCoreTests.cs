using Xunit;

namespace bstrings.Tests;

public sealed class LiteralBatchFilterCoreTests
{
    [Fact]
    public void TransformBatch_FiltersInPlaceWithOrdinalIgnoreCaseMatching()
    {
        var hits = new List<string> { "Alpha SECRET value", "ordinary", "token=Needle" };
        var original = hits;
        var filter = new LiteralBatchFilterCore(["secret", "needle"], includeOffset: false);

        var actual = filter.TransformBatch(hits);

        Assert.Same(original, actual);
        Assert.Equal(["Alpha SECRET value", "token=Needle"], actual);
    }

    [Fact]
    public void TransformBatch_SearchesDataInsteadOfFormattedOffset()
    {
        var hits = new List<string>
        {
            "0xDEADBEEF\tordinary",
            "0x10\tcontains beef in data",
            "  0xDEADBEEF\tboundary presentation remains unparsed",
        };
        var filter = new LiteralBatchFilterCore(["beef"], includeOffset: true);

        var actual = filter.TransformBatch(hits);

        Assert.Equal(
            ["0x10\tcontains beef in data", "  0xDEADBEEF\tboundary presentation remains unparsed"],
            actual
        );
    }

    [Fact]
    public void TransformBatch_ClearsBatchWhenAllConfiguredLiteralsAreIgnored()
    {
        var hits = new List<string> { "Alpha", "Beta" };
        var filter = new LiteralBatchFilterCore(["", "  ", "\t"], includeOffset: false);

        var actual = filter.TransformBatch(hits);

        Assert.Empty(actual);
    }
}
