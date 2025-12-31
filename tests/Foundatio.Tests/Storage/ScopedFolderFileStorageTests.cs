using System;
using System.IO;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Storage;
using Xunit;

namespace Foundatio.Tests.Storage;

public class ScopedFolderFileStorageTests : FileStorageTestsBase
{
    public ScopedFolderFileStorageTests(ITestOutputHelper output) : base(output) { }

    protected override IFileStorage GetStorage()
    {
        return new ScopedFileStorage(new FolderFileStorage(o => o.Folder("|DataDirectory|\\temp")), "scoped");
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenFileStorageIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ScopedFileStorage(null, "scope"));
    }

    [InlineData("*")]
    [Theory]
    public void Constructor_ShouldThrowArgumentException_WhenScopeContainsInvalidCharacters(string scope)
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        Assert.Throws<ArgumentException>(() => new ScopedFileStorage(storage, scope));
    }

    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("test")]
    [InlineData(" test ")]
    [InlineData("|DataDirectory|\\temp")]
    [InlineData("|DataDirectory|/temp")]
    [Theory]
    public void Constructor_ShouldInitializeProperties_WhenArgumentsAreValid(string scope)
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        var scopedStorage = new ScopedFileStorage(storage, scope);
        Assert.Equal(storage, scopedStorage.UnscopedStorage);

        if (String.IsNullOrWhiteSpace(scope))
            Assert.Null(scopedStorage.Scope);
        else
            Assert.Equal(scope.Trim().NormalizePath(), scopedStorage.Scope);
    }

    [Fact]
    public override Task CanGetEmptyFileListOnMissingDirectoryAsync()
    {
        return base.CanGetEmptyFileListOnMissingDirectoryAsync();
    }

    [Fact]
    public override Task CanGetFileListForSingleFolderAsync()
    {
        return base.CanGetFileListForSingleFolderAsync();
    }

    [Fact]
    public override Task CanGetFileListForSingleFileAsync()
    {
        return base.CanGetFileListForSingleFileAsync();
    }

    [Fact]
    public override Task CanGetPagedFileListForSingleFolderAsync()
    {
        return base.CanGetPagedFileListForSingleFolderAsync();
    }

    [Fact]
    public override Task CanGetFileInfoAsync()
    {
        return base.CanGetFileInfoAsync();
    }

    [Fact]
    public override Task CanGetNonExistentFileInfoAsync()
    {
        return base.CanGetNonExistentFileInfoAsync();
    }

    [Fact]
    public override Task CanSaveFilesAsync()
    {
        return base.CanSaveFilesAsync();
    }

    [Fact]
    public override Task CanManageFilesAsync()
    {
        return base.CanManageFilesAsync();
    }

    [Fact]
    public override Task CanRenameFilesAsync()
    {
        return base.CanRenameFilesAsync();
    }

    [Fact]
    public override Task CanConcurrentlyManageFilesAsync()
    {
        return base.CanConcurrentlyManageFilesAsync();
    }

    [Fact]
    public override void CanUseDataDirectory()
    {
        base.CanUseDataDirectory();
    }

    [Fact]
    public override Task CanDeleteEntireFolderAsync()
    {
        return base.CanDeleteEntireFolderAsync();
    }

    [Fact]
    public override Task CanDeleteEntireFolderWithWildcardAsync()
    {
        return base.CanDeleteEntireFolderWithWildcardAsync();
    }

    [Fact(Skip = "Directory.EnumerateFiles does not support nested folder wildcards")]
    public override Task CanDeleteFolderWithMultiFolderWildcardsAsync()
    {
        return base.CanDeleteFolderWithMultiFolderWildcardsAsync();
    }

    [Fact]
    public override Task CanDeleteSpecificFilesAsync()
    {
        return base.CanDeleteSpecificFilesAsync();
    }

    [Fact]
    public override Task CanDeleteNestedFolderAsync()
    {
        return base.CanDeleteNestedFolderAsync();
    }

    [Fact]
    public override Task CanDeleteSpecificFilesInNestedFolderAsync()
    {
        return base.CanDeleteSpecificFilesInNestedFolderAsync();
    }

    [Fact]
    public override Task CanRoundTripSeekableStreamAsync()
    {
        return base.CanRoundTripSeekableStreamAsync();
    }

    [Fact]
    public override Task WillRespectStreamOffsetAsync()
    {
        return base.WillRespectStreamOffsetAsync();
    }

    [Fact]
    public override Task WillWriteStreamContentAsync()
    {
        return base.WillWriteStreamContentAsync();
    }

    [Fact]
    public override Task CanSaveOverExistingStoredContent()
    {
        return base.CanSaveOverExistingStoredContent();
    }

    [Fact]
    public async Task WillNotReturnDirectoryInGetPagedFileListAsync()
    {
        var storage = GetStorage();
        if (storage is null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            var result = await storage.GetPagedFileListAsync();
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);
            Assert.False(await result.NextPageAsync());
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);

            const string directory = "EmptyDirectory/";
            string folder = storage is ScopedFileStorage { UnscopedStorage: FolderFileStorage folderStorage } ? folderStorage.Folder : null;
            Assert.NotNull(folder);
            Directory.CreateDirectory(Path.Combine(folder, "scoped", directory));

            result = await storage.GetPagedFileListAsync();
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);
            Assert.False(await result.NextPageAsync());
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);

            // Ensure the directory will not be returned via get file info
            var info = await storage.GetFileInfoAsync(directory);
            Assert.Null(info);

            // Ensure delete files can remove all files including fake folders
            await storage.DeleteFilesAsync("*");

            // Assert folder was removed by Delete Files
            Assert.False(Directory.Exists(Path.Combine(folder, "scoped", directory)));

            info = await storage.GetFileInfoAsync(directory);
            Assert.Null(info);
        }
    }
}
