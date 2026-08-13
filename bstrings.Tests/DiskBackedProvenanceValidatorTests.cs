using Xunit;

namespace bstrings.Tests;

public sealed class DiskBackedProvenanceValidatorTests
{
    [Fact]
    public void Validate_FindsDuplicateIdsAcrossExternalSortRuns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        string indexDirectory;
        using (var validator = new DiskBackedProvenanceValidator(scope.DirectoryPath, 1, 1024))
        {
            indexDirectory = validator.WorkingDirectory;
            validator.AddOriginal("duplicate-record-id");
            for (var index = 0; index < 200; index++)
            {
                validator.AddOriginal($"unique-record-{index:D4}-{new string('x', 32)}");
            }
            validator.AddOriginal("duplicate-record-id");

            var error = Assert.Throws<InvalidDataException>(() =>
                validator.Validate(cancellationToken)
            );
            Assert.Contains("repeats recordId", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(Directory.Exists(indexDirectory));
    }

    [Fact]
    public void Validate_RequiresEveryTranslatedParentToExistExactly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(scope.DirectoryPath, 1, 1024);
        validator.AddOriginal("raw-1");
        validator.AddTranslation("translated-1", "raw-10");

        var error = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(cancellationToken)
        );

        Assert.Contains("raw-10", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAnIdRepeatedAcrossOriginalAndTranslationRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(scope.DirectoryPath, 1, 1024);
        validator.AddOriginal("raw-1");
        validator.AddTranslation("raw-1", "raw-1");

        var error = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(cancellationToken)
        );

        Assert.Contains("repeats recordId", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AcceptsUniqueRecordsAndExistingParentsAcrossRuns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(scope.DirectoryPath, 1, 1024);
        for (var index = 0; index < 200; index++)
        {
            validator.AddOriginal($"raw-{index:D4}-{new string('x', 32)}");
        }
        for (var index = 0; index < 200; index++)
        {
            validator.AddTranslation(
                $"translated-{index:D4}",
                $"raw-{index:D4}-{new string('x', 32)}"
            );
        }

        validator.Validate(cancellationToken);
    }

    [Fact]
    public void Validate_RejectsTranslatedLineageThatDiffersFromItsParent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(scope.DirectoryPath, 1, 1024);
        validator.AddOriginal("raw-1", Identity("0x10"));
        validator.AddTranslation("translated-1", "raw-1", Identity("0x11"));

        var error = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(cancellationToken)
        );

        Assert.Contains("exact sourceFile, location, and origin", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AcceptsDecodingChildWithExactRawParentLineage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(scope.DirectoryPath, 1, 1024);
        var identity = Identity("0x10");
        validator.AddOriginal("raw-1", identity);
        validator.AddDecoding("decoded-1", "raw-1", identity);

        validator.Validate(cancellationToken);
    }

    [Fact]
    public void Validate_RejectsDecodingChildWithMissingRawParent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(scope.DirectoryPath, 1, 1024);
        validator.AddOriginal("raw-1", Identity("0x10"));
        validator.AddDecoding("decoded-1", "raw-10", Identity("0x10"));

        var error = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(cancellationToken)
        );

        Assert.Contains("raw-10", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ExactCoverageRejectsACandidateWithoutAChild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(
            scope.DirectoryPath,
            1,
            1024,
            requireEveryOriginalReferenced: true
        );
        validator.AddOriginal("raw-1", Identity("0x10"));

        var error = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(cancellationToken)
        );

        Assert.Contains("exactly one translated child", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ExactCoverageRejectsMultipleChildrenForOneCandidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedProvenanceValidator(
            scope.DirectoryPath,
            1,
            1024,
            requireEveryOriginalReferenced: true
        );
        var identity = Identity("0x10");
        validator.AddOriginal("raw-1", identity);
        validator.AddTranslation("translated-1", "raw-1", identity);
        validator.AddTranslation("translated-2", "raw-1", identity);

        var error = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(cancellationToken)
        );

        Assert.Contains("2 translated child", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TranslationLineageIdentity Identity(string location) =>
        new("sample.exe", "file_offset", location, "bstrings", "1.9.0", "static");

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-provenance-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
