using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Exceptionless;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage;

public abstract class FileStorageTestsBase : TestWithLoggingBase
{
    protected FileStorageTestsBase(ITestOutputHelper output) : base(output) { }

    protected virtual IFileStorage GetStorage()
    {
        return null;
    }

    public virtual async Task CanGetEmptyFileListOnMissingDirectoryAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            Assert.Empty(await storage.GetFileListAsync(Guid.NewGuid() + "\\*"));
        }
    }

    public virtual async Task CanGetFileListForSingleFolderAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync(@"archived\archived.txt", "archived");
            await storage.SaveFileAsync(@"q\new.txt", "new");
            await storage.SaveFileAsync(@"long/path/in/here/1.hey.stuff-2.json", "archived");

            Assert.Equal(3, (await storage.GetFileListAsync()).Count);
            Assert.Single(await storage.GetFileListAsync(limit: 1));
            Assert.Single(await storage.GetFileListAsync(@"long\path\in\here\*stuff*.json"));

            Assert.Single(await storage.GetFileListAsync(@"archived\*"));
            Assert.Equal("archived", await storage.GetFileContentsAsync(@"archived\archived.txt"));

            Assert.Single(await storage.GetFileListAsync(@"q\*"));
            Assert.Equal("new", await storage.GetFileContentsAsync(@"q\new.txt"));
        }
    }

    public virtual async Task CanGetFileListForSingleFileAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync(@"archived\archived.txt", "archived");
            await storage.SaveFileAsync(@"archived\archived.csv", "archived");
            await storage.SaveFileAsync(@"q\new.txt", "new");
            await storage.SaveFileAsync(@"long/path/in/here/1.hey.stuff-2.json", "archived");

            Assert.Single(await storage.GetFileListAsync(@"archived\archived.txt"));
        }
    }

    public virtual async Task CanGetPagedFileListForSingleFolderAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            var result = await storage.GetPagedFileListAsync(1);
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);
            Assert.False(await result.NextPageAsync());
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);

            await storage.SaveFileAsync(@"archived\archived.txt", "archived");
            result = await storage.GetPagedFileListAsync(1);
            Assert.False(result.HasMore);
            Assert.Single(result.Files);
            Assert.False(await result.NextPageAsync());
            Assert.False(result.HasMore);
            Assert.Single(result.Files);

            await storage.SaveFileAsync(@"q\new.txt", "new");
            result = await storage.GetPagedFileListAsync(1);
            Assert.True(result.HasMore);
            Assert.Single(result.Files);
            Assert.True(await result.NextPageAsync());
            Assert.False(result.HasMore);
            Assert.Single(result.Files);

            await storage.SaveFileAsync(@"long/path/in/here/1.hey.stuff-2.json", "archived");

            Assert.Equal(3, (await storage.GetPagedFileListAsync(100)).Files.Count);
            Assert.Single((await storage.GetPagedFileListAsync(1)).Files);
            Assert.Single((await storage.GetPagedFileListAsync(2, @"long\path\in\here\*stuff*.json")).Files);

            Assert.Single((await storage.GetPagedFileListAsync(2, @"archived\*")).Files);
            Assert.Equal("archived", await storage.GetFileContentsAsync(@"archived\archived.txt"));

            Assert.Single((await storage.GetPagedFileListAsync(2, @"q\*")).Files);
            Assert.Equal("new", await storage.GetFileContentsAsync(@"q\new.txt"));
        }
    }

    public virtual async Task CanGetFileInfoAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            var fileInfo = await storage.GetFileInfoAsync(Guid.NewGuid().ToString());
            Assert.Null(fileInfo);

            var startTime = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1));
            string path = $"folder\\{Guid.NewGuid()}-nested.txt";
            Assert.True(await storage.SaveFileAsync(path, "test"));
            fileInfo = await storage.GetFileInfoAsync(path);
            Assert.NotNull(fileInfo);
            Assert.True(fileInfo.Path.EndsWith("nested.txt"), "Incorrect file");
            Assert.True(fileInfo.Size > 0, "Incorrect file size");
            // NOTE: File creation time might not be accurate: http://stackoverflow.com/questions/2109152/unbelievable-strange-file-creation-time-problem
            Assert.True(fileInfo.Created > DateTime.MinValue, "File creation time should be newer than the start time");
            Assert.True(startTime <= fileInfo.Modified, $"File {path} modified time {fileInfo.Modified:O} should be newer than the start time {startTime:O}.");

            path = $"{Guid.NewGuid()}-test.txt";
            Assert.True(await storage.SaveFileAsync(path, "test"));
            fileInfo = await storage.GetFileInfoAsync(path);
            Assert.NotNull(fileInfo);
            Assert.True(fileInfo.Path.EndsWith("test.txt"), "Incorrect file");
            Assert.True(fileInfo.Size > 0, "Incorrect file size");
            Assert.True(fileInfo.Created > DateTime.MinValue, "File creation time should be newer than the start time.");
            Assert.True(startTime <= fileInfo.Modified, $"File {path} modified time {fileInfo.Modified:O} should be newer than the start time {startTime:O}.");
        }
    }

    public virtual async Task CanGetNonExistentFileInfoAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.GetFileInfoAsync(null));
            Assert.Null(await storage.GetFileInfoAsync(Guid.NewGuid().ToString()));
        }
    }

    public virtual async Task CanManageFilesAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync("test.txt", "test");
            var file = (await storage.GetFileListAsync()).Single();
            Assert.NotNull(file);
            Assert.Equal("test.txt", file.Path);
            string content = await storage.GetFileContentsAsync("test.txt");
            Assert.Equal("test", content);
            await storage.RenameFileAsync("test.txt", "new.txt");
            Assert.Contains(await storage.GetFileListAsync(), f => f.Path == "new.txt");
            await storage.DeleteFileAsync("new.txt");
            Assert.Empty(await storage.GetFileListAsync());
        }
    }

    public virtual async Task CanRenameFilesAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            Assert.True(await storage.SaveFileAsync("test.txt", "test"));
            Assert.True(await storage.RenameFileAsync("test.txt", @"archive\new.txt"));
            Assert.Equal("test", await storage.GetFileContentsAsync(@"archive\new.txt"));
            Assert.Single(await storage.GetFileListAsync());

            Assert.True(await storage.SaveFileAsync("test2.txt", "test2"));
            Assert.True(await storage.RenameFileAsync("test2.txt", @"archive\new.txt"));
            Assert.Equal("test2", await storage.GetFileContentsAsync(@"archive\new.txt"));
            Assert.Single(await storage.GetFileListAsync());
        }
    }

    protected virtual string GetTestFilePath()
    {
        var currentDirectory = new DirectoryInfo(PathHelper.ExpandPath(@"|DataDirectory|\"));
        var currentFilePath = Path.Combine(currentDirectory.FullName, "README.md");
        while (!File.Exists(currentFilePath) && currentDirectory.Parent != null)
        {
            currentDirectory = currentDirectory.Parent;
            currentFilePath = Path.Combine(currentDirectory.FullName, "README.md");
        }

        if (File.Exists(currentFilePath))
            return currentFilePath;

        throw new ApplicationException("Unable to find test README.md file in path hierarchy.");
    }

    public virtual async Task CanSaveFilesAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        string readmeFile = GetTestFilePath();
        using (storage)
        {
            const string path = "cansavefiles.txt";
            Assert.False(await storage.ExistsAsync(path));

            await using (var stream = new NonSeekableStream(File.Open(readmeFile, FileMode.Open, FileAccess.Read)))
            {
                bool result = await storage.SaveFileAsync(path, stream);
                Assert.True(result);
            }

            Assert.Single(await storage.GetFileListAsync());
            Assert.True(await storage.ExistsAsync(path));

            await using (var stream = await storage.GetFileStreamAsync(path, StreamMode.Read))
            {
                string result = await new StreamReader(stream).ReadToEndAsync();
                Assert.Equal(await File.ReadAllTextAsync(readmeFile), result);
            }
        }
    }

    public virtual async Task CanDeleteEntireFolderAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync(@"x\hello.txt", "hello");
            await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
            Assert.Equal(2, (await storage.GetFileListAsync()).Count);

            await storage.DeleteFilesAsync(@"x\*");
            Assert.Empty(await storage.GetFileListAsync());
        }
    }

    public virtual async Task CanDeleteEntireFolderWithWildcardAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync(@"x\hello.txt", "hello");
            await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
            Assert.Equal(2, (await storage.GetFileListAsync()).Count);
            Assert.Single(await storage.GetFileListAsync(limit: 1));
            Assert.Equal(2, (await storage.GetFileListAsync(@"x\*")).Count);
            Assert.Single(await storage.GetFileListAsync(@"x\nested\*"));

            await storage.DeleteFilesAsync(@"x\*");

            Assert.Empty(await storage.GetFileListAsync());
        }
    }

    public virtual async Task CanDeleteFolderWithMultiFolderWildcardsAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            const int filesPerMonth = 5;
            for (int year = 2020; year <= 2021; year++)
            {
                for (int month = 1; month <= 12; month++)
                {
                    for (int index = 0; index < filesPerMonth; index++)
                        await storage.SaveFileAsync($"archive\\year-{year}\\month-{month:00}\\file-{index:00}.txt", "hello");
                }
            }

            _logger.LogInformation(@"List by pattern: archive\*");
            Assert.Equal(2 * 12 * filesPerMonth, (await storage.GetFileListAsync(@"archive\*")).Count);
            _logger.LogInformation(@"List by pattern: archive\*month-01*");
            Assert.Equal(2 * filesPerMonth, (await storage.GetFileListAsync(@"archive\*month-01*")).Count);
            _logger.LogInformation(@"List by pattern: archive\year-2020\*month-01*");
            Assert.Equal(filesPerMonth, (await storage.GetFileListAsync(@"archive\year-2020\*month-01*")).Count);

            _logger.LogInformation(@"Delete by pattern: archive\*month-01*");
            await storage.DeleteFilesAsync(@"archive\*month-01*");

            Assert.Equal(2 * 11 * filesPerMonth, (await storage.GetFileListAsync()).Count);
        }
    }

    public virtual async Task CanDeleteSpecificFilesAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync(@"x\hello.txt", "hello");
            await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
            await storage.SaveFileAsync(@"x\nested\hello.txt", "nested hello");
            Assert.Equal(3, (await storage.GetFileListAsync()).Count);
            Assert.Single(await storage.GetFileListAsync(limit: 1));
            Assert.Equal(3, (await storage.GetFileListAsync(@"x\*")).Count);
            Assert.Equal(2, (await storage.GetFileListAsync(@"x\nested\*")).Count);
            Assert.Equal(2, (await storage.GetFileListAsync(@"x\*.txt")).Count);

            await storage.DeleteFilesAsync(@"x\*.txt");

            Assert.Single(await storage.GetFileListAsync());
            Assert.False(await storage.ExistsAsync(@"x\hello.txt"));
            Assert.False(await storage.ExistsAsync(@"x\nested\hello.txt"));
            Assert.True(await storage.ExistsAsync(@"x\nested\world.csv"));
        }
    }

    public virtual async Task CanDeleteNestedFolderAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync(@"x\hello.txt", "hello");
            await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
            await storage.SaveFileAsync(@"x\nested\hello.txt", "nested hello");
            Assert.Equal(3, (await storage.GetFileListAsync()).Count);
            Assert.Single(await storage.GetFileListAsync(limit: 1));
            Assert.Equal(3, (await storage.GetFileListAsync(@"x\*")).Count);
            Assert.Equal(2, (await storage.GetFileListAsync(@"x\nested\*")).Count);
            Assert.Equal(2, (await storage.GetFileListAsync(@"x\*.txt")).Count);

            await storage.DeleteFilesAsync(@"x\nested\*");

            Assert.Single(await storage.GetFileListAsync());
            Assert.True(await storage.ExistsAsync(@"x\hello.txt"));
            Assert.False(await storage.ExistsAsync(@"x\nested\hello.txt"));
            Assert.False(await storage.ExistsAsync(@"x\nested\world.csv"));
        }
    }

    public virtual async Task CanDeleteSpecificFilesInNestedFolderAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await storage.SaveFileAsync(@"x\hello.txt", "hello");
            await storage.SaveFileAsync(@"x\world.csv", "world");
            await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
            await storage.SaveFileAsync(@"x\nested\hello.txt", "nested hello");
            await storage.SaveFileAsync(@"x\nested\again.txt", "nested again");
            Assert.Equal(5, (await storage.GetFileListAsync()).Count);
            Assert.Single(await storage.GetFileListAsync(limit: 1));
            Assert.Equal(5, (await storage.GetFileListAsync(@"x\*")).Count);
            Assert.Equal(3, (await storage.GetFileListAsync(@"x\nested\*")).Count);
            Assert.Equal(3, (await storage.GetFileListAsync(@"x\*.txt")).Count);

            await storage.DeleteFilesAsync(@"x\nested\*.txt");

            Assert.Equal(3, (await storage.GetFileListAsync()).Count);
            Assert.True(await storage.ExistsAsync(@"x\hello.txt"));
            Assert.True(await storage.ExistsAsync(@"x\world.csv"));
            Assert.False(await storage.ExistsAsync(@"x\nested\hello.txt"));
            Assert.False(await storage.ExistsAsync(@"x\nested\again.txt"));
            Assert.True(await storage.ExistsAsync(@"x\nested\world.csv"));
        }
    }

    public virtual async Task CanRoundTripSeekableStreamAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            const string path = "user.xml";
            var element = XElement.Parse("<user>Blake</user>");

            using (var memoryStream = new MemoryStream())
            {
                _logger.LogTrace("Saving xml to stream with position {Position}.", memoryStream.Position);
                element.Save(memoryStream, SaveOptions.DisableFormatting);

                memoryStream.Seek(0, SeekOrigin.Begin);
                _logger.LogTrace("Saving contents with position {Position}", memoryStream.Position);
                await storage.SaveFileAsync(path, memoryStream);
                _logger.LogTrace("Saved contents with position {Position}.", memoryStream.Position);
            }

#pragma warning disable CS0618 // Type or member is obsolete
            await using var stream = await storage.GetFileStreamAsync(path);
#pragma warning restore CS0618 // Type or member is obsolete
            var actual = XElement.Load(stream);
            Assert.Equal(element.ToString(SaveOptions.DisableFormatting), actual.ToString(SaveOptions.DisableFormatting));
        }
    }

    public virtual async Task WillRespectStreamOffsetAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            string path = "blake.txt";
            using (var memoryStream = new MemoryStream())
            {
                long offset;
                await using (var writer = new StreamWriter(memoryStream, Encoding.UTF8, 1024, true))
                {
                    writer.AutoFlush = true;
                    await writer.WriteAsync("Eric");
                    offset = memoryStream.Position;
                    await writer.WriteAsync("Blake");
                    await writer.FlushAsync();
                }

                memoryStream.Seek(offset, SeekOrigin.Begin);
                await storage.SaveFileAsync(path, memoryStream);
            }

            Assert.Equal("Blake", await storage.GetFileContentsAsync(path));
        }
    }

    public virtual async Task WillWriteStreamContentAsync()
    {

        const string testContent = "test";
        const string path = "created.txt";

        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            await using var stream = await storage.GetFileStreamAsync(path, StreamMode.Write);
            Assert.NotNull(stream);
            Assert.True(stream.CanWrite);

            await using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, false))
            {
                await writer.WriteAsync(testContent);
            }

            string content = await storage.GetFileContentsAsync(path);
            Assert.Equal(testContent, content);
        }
    }

    protected virtual async Task ResetAsync(IFileStorage storage)
    {
        if (storage == null)
            return;

        _logger.LogInformation("Deleting all files...");
        await storage.DeleteFilesAsync();
        _logger.LogInformation("Asserting empty files...");
        Assert.Empty(await storage.GetFileListAsync(limit: 10000));
    }

    public virtual async Task CanConcurrentlyManageFilesAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            const string queueFolder = "q";
            var queueItems = new BlockingCollection<int>();

            var info = await storage.GetFileInfoAsync("nope");
            Assert.Null(info);

            await Parallel.ForEachAsync(Enumerable.Range(1, 10), async (i, ct) =>
            {
                var ev = new PostInfo
                {
                    ApiVersion = 2,
                    CharSet = "utf8",
                    ContentEncoding = "application/json",
                    Data = Encoding.UTF8.GetBytes("{}"),
                    IpAddress = "127.0.0.1",
                    MediaType = "gzip",
                    ProjectId = i.ToString(),
                    UserAgent = "test"
                };

                await storage.SaveObjectAsync(Path.Combine(queueFolder, i + ".json"), ev, cancellationToken: ct);
                queueItems.Add(i, ct);
            });

            Assert.Equal(10, (await storage.GetFileListAsync()).Count);

            await Parallel.ForEachAsync(Enumerable.Range(1, 10), async (_, _) =>
            {
                string path = Path.Combine(queueFolder, queueItems.Random() + ".json");
                var eventPost = await storage.GetEventPostAndSetActiveAsync(Path.Combine(queueFolder, RandomData.GetInt(0, 25) + ".json"), _logger);
                if (eventPost == null)
                    return;

                if (RandomData.GetBool())
                {
                    await storage.CompleteEventPostAsync(path, eventPost.ProjectId, DateTime.UtcNow, true, _logger);
                }
                else
                    await storage.SetNotActiveAsync(path, _logger);
            });
        }
    }

    public virtual void CanUseDataDirectory()
    {
        const string DATA_DIRECTORY_QUEUE_FOLDER = @"|DataDirectory|\Queue";

        var storage = new FolderFileStorage(new FolderFileStorageOptions
        {
            Folder = DATA_DIRECTORY_QUEUE_FOLDER
        });
        Assert.NotNull(storage.Folder);
        Assert.NotEqual(DATA_DIRECTORY_QUEUE_FOLDER, storage.Folder);
        Assert.True(storage.Folder.EndsWith("Queue" + Path.DirectorySeparatorChar), storage.Folder);
    }
}

public class PostInfo
{
    public int ApiVersion { get; set; }
    public string CharSet { get; set; }
    public string ContentEncoding { get; set; }
    public byte[] Data { get; set; }
    public string IpAddress { get; set; }
    public string MediaType { get; set; }
    public string ProjectId { get; set; }
    public string UserAgent { get; set; }
}

public static class StorageExtensions
{
    public static async Task<PostInfo> GetEventPostAndSetActiveAsync(this IFileStorage storage, string path, ILogger logger = null)
    {
        PostInfo eventPostInfo = null;
        try
        {
            eventPostInfo = await storage.GetObjectAsync<PostInfo>(path);
            if (eventPostInfo == null)
                return null;

            if (!await storage.ExistsAsync(path + ".x") && !await storage.SaveFileAsync(path + ".x", String.Empty))
                return null;
        }
        catch (Exception ex)
        {
            if (logger != null && logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "Error retrieving event post data {Path}: {Message}", path, ex.Message);
            return null;
        }

        return eventPostInfo;
    }

    public static async Task<bool> SetNotActiveAsync(this IFileStorage storage, string path, ILogger logger = null)
    {
        try
        {
            return await storage.DeleteFileAsync(path + ".x");
        }
        catch (Exception ex)
        {
            if (logger != null && logger.IsEnabled(LogLevel.Error))
                logger.LogError(ex, "Error deleting work marker {Path}: {Message}", path, ex.Message);
        }

        return false;
    }

    public static async Task<bool> CompleteEventPostAsync(this IFileStorage storage, string path, string projectId, DateTime created, bool shouldArchive = true, ILogger logger = null)
    {
        // don't move files that are already in the archive
        if (path.StartsWith("archive"))
            return true;

        string archivePath = $"archive\\{projectId}\\{created.ToString("yy\\\\MM\\\\dd")}\\{Path.GetFileName(path)}";

        try
        {
            if (shouldArchive)
            {
                if (!await storage.RenameFileAsync(path, archivePath))
                    return false;
            }
            else
            {
                if (!await storage.DeleteFileAsync(path))
                    return false;
            }
        }
        catch (Exception ex)
        {
            if (logger != null && logger.IsEnabled(LogLevel.Error))
                logger?.LogError(ex, "Error archiving event post data {Path}: {Message}", path, ex.Message);
            return false;
        }

        await storage.SetNotActiveAsync(path);

        return true;
    }
}
