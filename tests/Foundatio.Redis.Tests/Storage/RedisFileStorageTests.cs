using System.Threading.Tasks;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Storage;

public class RedisFileStorageTests : FileStorageTestsBase, IAsyncLifetime
{
    protected virtual RedisProtocol? Protocol => null;

    public RedisFileStorageTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override IFileStorage? GetStorage()
    {
        var muxer = SharedConnection.GetMuxer(Log, Protocol);
        if (muxer is null)
            return null;

        return new RedisFileStorage(o => o.ConnectionMultiplexer(muxer).LoggerFactory(Log));
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
    public override Task RenameFileAsync_WhenSourceDoesNotExist_ReturnsFalse()
    {
        return base.RenameFileAsync_WhenSourceDoesNotExist_ReturnsFalse();
    }

    [Fact]
    public override Task CanConcurrentlyManageFilesAsync()
    {
        return base.CanConcurrentlyManageFilesAsync();
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
    public override void CanUseDataDirectory()
    {
        base.CanUseDataDirectory();
    }

    [Fact]
    public override Task CanDeleteEntireFolderAsync()
    {
        return base.CanDeleteEntireFolderAsync();
    }

    [Fact(Skip = "Redis storage returns true for delete of non-existent files")]
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

    [Fact(Skip = "Write Stream is not yet supported")]
    public override Task WillWriteStreamContentAsync()
    {
        return base.WillWriteStreamContentAsync();
    }

    [Fact]
    public override Task CanSaveOverExistingStoredContent()
    {
        return base.CanSaveOverExistingStoredContent();
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _logger.LogDebug("Initializing");
        var muxer = SharedConnection.GetMuxer(Log, Protocol);
        if (muxer is null)
            return;

        await muxer.FlushAllAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _logger.LogDebug("Disposing");
    }
}

public class RedisFileStorageResp3Tests : RedisFileStorageTests
{
    public RedisFileStorageResp3Tests(ITestOutputHelper output) : base(output) { }
    protected override RedisProtocol? Protocol => RedisProtocol.Resp3;
}
