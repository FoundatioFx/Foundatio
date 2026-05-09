using System.Threading.Tasks;
using Foundatio.Storage;
using Xunit;

namespace Foundatio.Tests.Storage;

public class InMemoryFileStorageTests : FileStorageTestsBase
{
    public InMemoryFileStorageTests(ITestOutputHelper output) : base(output) { }

    protected override IFileStorage GetStorage()
    {
        return new InMemoryFileStorage { MaxFiles = 2000 };
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

    [Fact]
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
    public override Task CopyFileAsync_WithExistingFile_CreatesIdenticalCopy()
    {
        return base.CopyFileAsync_WithExistingFile_CreatesIdenticalCopy();
    }

    [Fact]
    public override Task CopyFileAsync_WithNonExistentSource_ReturnsFalse()
    {
        return base.CopyFileAsync_WithNonExistentSource_ReturnsFalse();
    }

    [Fact]
    public override Task DeleteFileAsync_WhenFileDoesNotExist_ReturnsFalse()
    {
        return base.DeleteFileAsync_WhenFileDoesNotExist_ReturnsFalse();
    }

    [Fact]
    public override Task DeleteFilesAsync_WithFileSpecCollection_DeletesSpecifiedFiles()
    {
        return base.DeleteFilesAsync_WithFileSpecCollection_DeletesSpecifiedFiles();
    }

    [Fact]
    public override Task GetFileContentsRawAsync_WithExistingFile_ReturnsByteArray()
    {
        return base.GetFileContentsRawAsync_WithExistingFile_ReturnsByteArray();
    }

    [Fact]
    public override Task GetFileStreamAsync_WithNonExistentFileInReadMode_ReturnsNull()
    {
        return base.GetFileStreamAsync_WithNonExistentFileInReadMode_ReturnsNull();
    }

    [Fact]
    public override Task RenameFileAsync_WhenSourceDoesNotExist_ReturnsFalse()
    {
        return base.RenameFileAsync_WhenSourceDoesNotExist_ReturnsFalse();
    }
}
