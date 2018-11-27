using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Exceptionless;
using Foundatio.Logging.Xunit;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage {
    public abstract class FileStorageTestsBase : TestWithLoggingBase {
        protected FileStorageTestsBase(ITestOutputHelper output) : base(output) {}

        protected virtual IFileStorage GetStorage() {
            return null;
        }

        public virtual async Task CanGetEmptyFileListOnMissingDirectoryAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                var result = await storage.GetFileListAsync(Guid.NewGuid() + "\\*");
                Assert.Empty(result.Files);
            }
        }

        public virtual async Task CanGetFileListForSingleFolderAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"archived\archived.txt", "archived");
                await storage.SaveFileAsync(@"q\new.txt", "new");
                await storage.SaveFileAsync(@"long/path/in/here/1.hey.stuff-2.json", "archived");

                var result = await storage.GetFileListAsync();
                Assert.Equal(3, result.Files.Count);
                result = await storage.GetFileListAsync(limit: 1);
                Assert.Single(result.Files);
                result = await storage.GetFileListAsync(@"long\path\in\here\*stuff*.json");
                Assert.Single(result.Files);

                result = await storage.GetFileListAsync(@"archived\*");
                Assert.Single(result.Files);
                Assert.Equal("archived", await storage.GetFileContentsAsync(@"archived\archived.txt"));

                result = await storage.GetFileListAsync(@"q\*");
                Assert.Single(result.Files);
                Assert.Equal("new", await storage.GetFileContentsAsync(@"q\new.txt"));
            }
        }

        public virtual async Task CanGetFileInfoAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                var fileInfo = await storage.GetFileInfoAsync(Guid.NewGuid().ToString());
                Assert.Null(fileInfo);

                var startTime = SystemClock.UtcNow.Floor(TimeSpan.FromSeconds(1));
                string path = $"folder\\{Guid.NewGuid()}-nested.txt";
                Assert.True(await storage.SaveFileAsync(path, "test"));
                fileInfo = await storage.GetFileInfoAsync(path);
                Assert.NotNull(fileInfo);
                Assert.True(fileInfo.Path.EndsWith("nested.txt"), "Incorrect file");
                Assert.True(fileInfo.Size > 0, "Incorrect file size");
                Assert.Equal(DateTimeKind.Utc, fileInfo.Created.Kind);
                // NOTE: File creation time might not be accurate: http://stackoverflow.com/questions/2109152/unbelievable-strange-file-creation-time-problem
                Assert.True(fileInfo.Created > DateTime.MinValue, "File creation time should be newer than the start time.");
                Assert.Equal(DateTimeKind.Utc, fileInfo.Modified.Kind);
                Assert.True(startTime <= fileInfo.Modified, $"File {path} modified time {fileInfo.Modified:O} should be newer than the start time {startTime:O}.");

                path = $"{Guid.NewGuid()}-test.txt";
                Assert.True(await storage.SaveFileAsync(path, "test"));
                fileInfo = await storage.GetFileInfoAsync(path);
                Assert.NotNull(fileInfo);
                Assert.True(fileInfo.Path.EndsWith("test.txt"), "Incorrect file");
                Assert.True(fileInfo.Size > 0, "Incorrect file size");
                Assert.Equal(DateTimeKind.Utc, fileInfo.Created.Kind);
                Assert.True(fileInfo.Created > DateTime.MinValue, "File creation time should be newer than the start time.");
                Assert.Equal(DateTimeKind.Utc, fileInfo.Modified.Kind);
                Assert.True(startTime <= fileInfo.Modified, $"File {path} modified time {fileInfo.Modified:O} should be newer than the start time {startTime:O}.");
            }
        }

        public virtual async Task CanGetNonExistentFileInfoAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await Assert.ThrowsAnyAsync<ArgumentException>(() => storage.GetFileInfoAsync(null));
                Assert.Null(await storage.GetFileInfoAsync(Guid.NewGuid().ToString()));
            }
        }

        public virtual async Task CanManageFilesAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync("test.txt", "test");
                var file = (await storage.GetFileListAsync()).Files.Single();
                Assert.NotNull(file);
                Assert.Equal("test.txt", file.Path);
                string content = await storage.GetFileContentsAsync("test.txt");
                Assert.Equal("test", content);
                await storage.RenameFileAsync("test.txt", "new.txt");
                var result = await storage.GetFileListAsync();
                Assert.Contains(result.Files, f => f.Path == "new.txt");
                await storage.DeleteFileAsync("new.txt");
                result = await storage.GetFileListAsync();
                Assert.Empty(result.Files);
            }
        }

        public virtual async Task CanRenameFilesAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                Assert.True(await storage.SaveFileAsync("test.txt", "test"));
                Assert.True(await storage.RenameFileAsync("test.txt", @"archive\new.txt"));
                Assert.Equal("test", await storage.GetFileContentsAsync(@"archive\new.txt"));
                var result = await storage.GetFileListAsync();
                Assert.Single(result.Files);

                Assert.True(await storage.SaveFileAsync("test2.txt", "test2"));
                Assert.True(await storage.RenameFileAsync("test2.txt", @"archive\new.txt"));
                Assert.Equal("test2", await storage.GetFileContentsAsync(@"archive\new.txt"));
                result = await storage.GetFileListAsync();
                Assert.Single(result.Files);
            }
        }

        protected virtual string GetTestFilePath() {
            var currentDirectory = new DirectoryInfo(PathHelper.ExpandPath(@"|DataDirectory|\"));
            var currentFilePath = Path.Combine(currentDirectory.FullName, "README.md");
            while (!File.Exists(currentFilePath) && currentDirectory.Parent != null) {
                currentDirectory = currentDirectory.Parent;
                currentFilePath = Path.Combine(currentDirectory.FullName, "README.md");
            }

            if (File.Exists(currentFilePath))
                return currentFilePath;

            throw new ApplicationException("Unable to find test README.md file in path hierarchy.");
        }

        public virtual async Task CanSaveFilesAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            string readmeFile = GetTestFilePath();
            using (storage) {
                Assert.False(await storage.ExistsAsync("Foundatio.Tests.csproj"));

                using (var stream = new NonSeekableStream(File.Open(readmeFile, FileMode.Open, FileAccess.Read))) {
                    bool saveResult = await storage.SaveFileAsync("Foundatio.Tests.csproj", stream);
                    Assert.True(saveResult);
                }

                var result = await storage.GetFileListAsync();
                Assert.Single(result.Files);
                Assert.True(await storage.ExistsAsync("Foundatio.Tests.csproj"));

                using (var stream = await storage.GetFileStreamAsync("Foundatio.Tests.csproj")) {
                    string contents = await new StreamReader(stream).ReadToEndAsync();
                    Assert.Equal(File.ReadAllText(readmeFile), contents);
                }
            }
        }

        public virtual async Task CanDeleteEntireFolderAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"x\hello.txt", "hello");
                await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
                var result = await storage.GetFileListAsync();
                Assert.Equal(2, result.Files.Count);

                await storage.DeleteFilesAsync(@"x");
                result = await storage.GetFileListAsync();
                Assert.Empty(result.Files);
            }
        }

        public virtual async Task CanDeleteEntireFolderWithWildcardAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"x\hello.txt", "hello");
                await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
                var result = await storage.GetFileListAsync();
                Assert.Equal(2, result.Files.Count);
                result = await storage.GetFileListAsync(limit: 1);
                Assert.Single(result.Files);
                result = await storage.GetFileListAsync(@"x\*");
                Assert.Equal(2, result.Files.Count);
                result = await storage.GetFileListAsync(@"x\nested\*");
                Assert.Single(result.Files);

                await storage.DeleteFilesAsync(@"x\*");

                result = await storage.GetFileListAsync();
                Assert.Empty(result.Files);
            }
        }

        public virtual async Task CanDeleteSpecificFilesAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"x\hello.txt", "hello");
                await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
                await storage.SaveFileAsync(@"x\nested\hello.txt", "nested hello");
                var result = await storage.GetFileListAsync();
                Assert.Equal(3, result.Files.Count);
                result = await storage.GetFileListAsync(limit: 1);
                Assert.Single(result.Files);
                result = await storage.GetFileListAsync(@"x\*");
                Assert.Equal(3, result.Files.Count);
                result = await storage.GetFileListAsync(@"x\nested\*");
                Assert.Equal(2, result.Files.Count);
                result = await storage.GetFileListAsync(@"x\*.txt");
                Assert.Equal(2, result.Files.Count);

                await storage.DeleteFilesAsync(@"x\*.txt");

                result = await storage.GetFileListAsync();
                Assert.Single(result.Files);
                Assert.False(await storage.ExistsAsync(@"x\hello.txt"));
                Assert.False(await storage.ExistsAsync(@"x\nested\hello.txt"));
                Assert.True(await storage.ExistsAsync(@"x\nested\world.csv"));
            }
        }

        public virtual async Task CanDeleteNestedFolderAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"x\hello.txt", "hello");
                await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
                await storage.SaveFileAsync(@"x\nested\hello.txt", "nested hello");
                var result = await storage.GetFileListAsync();
                Assert.Equal(3, result.Files.Count);
                result = await storage.GetFileListAsync(limit: 1);
                Assert.Single(result.Files);
                result = await storage.GetFileListAsync(@"x\*");
                Assert.Equal(3, result.Files.Count);
                result = await storage.GetFileListAsync(@"x\nested\*");
                Assert.Equal(2, result.Files.Count);
                result = await storage.GetFileListAsync(@"x\*.txt");
                Assert.Equal(2, result.Files.Count);

                await storage.DeleteFilesAsync(@"x\nested");

                result = await storage.GetFileListAsync();
                Assert.Single(result.Files);
                Assert.True(await storage.ExistsAsync(@"x\hello.txt"));
                Assert.False(await storage.ExistsAsync(@"x\nested\hello.txt"));
                Assert.False(await storage.ExistsAsync(@"x\nested\world.csv"));
            }
        }

        public virtual async Task CanDeleteSpecificFilesInNestedFolderAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"x\hello.txt", "hello");
                await storage.SaveFileAsync(@"x\world.csv", "world");
                await storage.SaveFileAsync(@"x\nested\world.csv", "nested world");
                await storage.SaveFileAsync(@"x\nested\hello.txt", "nested hello");
                await storage.SaveFileAsync(@"x\nested\again.txt", "nested again");
                var result = await storage.GetFileListAsync();
                Assert.Equal(5, result.Files.Count);
                result = await storage.GetFileListAsync(limit: 1);
                Assert.Single(result.Files);
                result = await storage.GetFileListAsync(@"x\*");
                Assert.Equal(5, result.Files.Count);
                result = await storage.GetFileListAsync(@"x\nested\*");
                Assert.Equal(3, result.Files.Count);
                result = await storage.GetFileListAsync(@"x\*.txt");
                Assert.Equal(3, result.Files.Count);

                await storage.DeleteFilesAsync(@"x\nested\*.txt");

                result = await storage.GetFileListAsync();
                Assert.Equal(3, result.Files.Count);
                Assert.True(await storage.ExistsAsync(@"x\hello.txt"));
                Assert.True(await storage.ExistsAsync(@"x\world.csv"));
                Assert.False(await storage.ExistsAsync(@"x\nested\hello.txt"));
                Assert.False(await storage.ExistsAsync(@"x\nested\again.txt"));
                Assert.True(await storage.ExistsAsync(@"x\nested\world.csv"));
            }
        }

        public virtual async Task CanRoundTripSeekableStreamAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                const string path = "user.xml";
                var element = XElement.Parse("<user>Blake</user>");

                using (var memoryStream = new MemoryStream()) {
                    _logger.LogTrace("Saving xml to stream with position {Position}.", memoryStream.Position);
                    element.Save(memoryStream, SaveOptions.DisableFormatting);

                    memoryStream.Seek(0, SeekOrigin.Begin);
                    _logger.LogTrace("Saving contents with position {Position}", memoryStream.Position);
                    await storage.SaveFileAsync(path, memoryStream);
                    _logger.LogTrace("Saved contents with position {Position}.", memoryStream.Position);
                }

                using (var stream = await storage.GetFileStreamAsync(path)) {
                    var actual = XElement.Load(stream);
                    Assert.Equal(element.ToString(SaveOptions.DisableFormatting), actual.ToString(SaveOptions.DisableFormatting));
                }
            }
        }

        public virtual async Task WillRespectStreamOffsetAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                string path = "blake.txt";
                using (var memoryStream = new MemoryStream()) {
                    long offset;
                    using (var writer = new StreamWriter(memoryStream, Encoding.UTF8, 1024, true)) {
                        writer.AutoFlush = true;
                        await writer.WriteAsync("Eric");
                        offset = memoryStream.Position;
                        await writer.WriteAsync("Blake");
                    }

                    memoryStream.Seek(offset, SeekOrigin.Begin);
                    await storage.SaveFileAsync(path, memoryStream);
                }

                Assert.Equal("Blake", await storage.GetFileContentsAsync(path));
            }
        }

        protected async Task ResetAsync() {
            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                var result = await storage.GetFileListAsync();
                if (result.Files.Count > 0) {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Deleting: {0}", String.Join(", ", result.Files.Select(f => f.Path)));
                    await storage.DeleteFilesAsync(result.Files);
                }

                result = await storage.GetFileListAsync();
                Assert.Empty(result.Files);
            }
        }

        public virtual async Task CanConcurrentlyManageFilesAsync() {
            await ResetAsync();

            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                const string queueFolder = "q";
                var queueItems = new BlockingCollection<int>();

                var info = await storage.GetFileInfoAsync("nope");
                Assert.Null(info);

                await Run.InParallelAsync(10, async i => {
                    var ev = new PostInfo {
                        ApiVersion = 2,
                        CharSet = "utf8",
                        ContentEncoding = "application/json",
                        Data = Encoding.UTF8.GetBytes("{}"),
                        IpAddress = "127.0.0.1",
                        MediaType = "gzip",
                        ProjectId = i.ToString(),
                        UserAgent = "test"
                    };

                    await storage.SaveObjectAsync(Path.Combine(queueFolder, i + ".json"), ev);
                    queueItems.Add(i);
                });

                var result = await storage.GetFileListAsync();
                Assert.Equal(10, result.Files.Count);

                await Run.InParallelAsync(10, async i => {
                    string path = Path.Combine(queueFolder, queueItems.Random() + ".json");
                    var eventPost = await storage.GetEventPostAndSetActiveAsync(Path.Combine(queueFolder, RandomData.GetInt(0, 25) + ".json"), _logger);
                    if (eventPost == null)
                        return;

                    if (RandomData.GetBool()) {
                        await storage.CompleteEventPostAsync(path, eventPost.ProjectId, SystemClock.UtcNow, true, _logger);
                    } else
                        await storage.SetNotActiveAsync(path, _logger);
                });
            }
        }
        
        public virtual void CanUseDataDirectory() {
            const string DATA_DIRECTORY_QUEUE_FOLDER = @"|DataDirectory|\Queue";

            var storage = new FolderFileStorage(new FolderFileStorageOptions {
                Folder = DATA_DIRECTORY_QUEUE_FOLDER
            });
            Assert.NotNull(storage.Folder);
            Assert.NotEqual(DATA_DIRECTORY_QUEUE_FOLDER, storage.Folder);
            Assert.True(storage.Folder.EndsWith("Queue" + Path.DirectorySeparatorChar), storage.Folder);
        }
    }

    public class PostInfo {
        public int ApiVersion { get; set; }
        public string CharSet { get; set; }
        public string ContentEncoding { get; set; }
        public byte[] Data { get; set; }
        public string IpAddress { get; set; }
        public string MediaType { get; set; }
        public string ProjectId { get; set; }
        public string UserAgent { get; set; }
    }

    public static class StorageExtensions {
        public static async Task<PostInfo> GetEventPostAndSetActiveAsync(this IFileStorage storage, string path, ILogger logger = null) {
            PostInfo eventPostInfo = null;
            try {
                eventPostInfo = await storage.GetObjectAsync<PostInfo>(path);
                if (eventPostInfo == null)
                    return null;

                if (!await storage.ExistsAsync(path + ".x") && !await storage.SaveFileAsync(path + ".x", String.Empty))
                    return null;
            } catch (Exception ex) {
                if (logger != null && logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Error retrieving event post data {Path}: {Message}", path, ex.Message);
                return null;
            }

            return eventPostInfo;
        }

        public static async Task<bool> SetNotActiveAsync(this IFileStorage storage, string path, ILogger logger = null) {
            try {
                return await storage.DeleteFileAsync(path + ".x");
            } catch (Exception ex) {
                if (logger != null && logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Error deleting work marker {Path}: {Message}", path, ex.Message);
            }

            return false;
        }

        public static async Task<bool> CompleteEventPostAsync(this IFileStorage storage, string path, string projectId, DateTime created, bool shouldArchive = true, ILogger logger = null) {
            // don't move files that are already in the archive
            if (path.StartsWith("archive"))
                return true;

            string archivePath = $"archive\\{projectId}\\{created.ToString("yy\\\\MM\\\\dd")}\\{Path.GetFileName(path)}";

            try {
                if (shouldArchive) {
                    if (!await storage.RenameFileAsync(path, archivePath))
                        return false;
                } else {
                    if (!await storage.DeleteFileAsync(path))
                        return false;
                }
            } catch (Exception ex) {
                if (logger != null && logger.IsEnabled(LogLevel.Error))
                    logger?.LogError(ex, "Error archiving event post data {Path}: {Message}", path, ex.Message);
                return false;
            }

            await storage.SetNotActiveAsync(path);

            return true;
        }
    }
}