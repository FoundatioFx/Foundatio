using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Foundatio.Utility;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage {
    public abstract class FileStorageTestsBase : TestWithLoggingBase {
        protected FileStorageTestsBase(ITestOutputHelper output) : base(output) {}

        protected virtual IFileStorage GetStorage() {
            return null;
        }

        public virtual async Task CanGetEmptyFileListOnMissingDirectory() {
            await ResetAsync();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                Assert.Equal(0, (await storage.GetFileListAsync(Guid.NewGuid() + "\\*")).Count());
            }
        }

        public virtual async Task CanGetFileListForSingleFolder() {
            await ResetAsync();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"archived\archived.txt", "archived");
                await storage.SaveFileAsync(@"q\new.txt", "new");
                Assert.Equal(2, (await storage.GetFileListAsync()).Count());
                Assert.Equal(1, (await storage.GetFileListAsync(limit: 1)).Count());
                Assert.Equal(1, (await storage.GetFileListAsync(@"archived\*")).Count());
                Assert.Equal(1, (await storage.GetFileListAsync(@"q\*")).Count());

                var file = (await storage.GetFileListAsync(@"archived\*")).FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("archived", await storage.GetFileContentsAsync(@"archived\archived.txt"));


                file = (await storage.GetFileListAsync(@"q\*")).FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("new", await storage.GetFileContentsAsync(@"q\new.txt"));
            }
        }

        public virtual async Task CanManageFiles() {
            await ResetAsync();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync("test.txt", "test");
                Assert.Equal(1, (await storage.GetFileListAsync()).Count());
                var file = (await storage.GetFileListAsync()).FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("test.txt", file.Path);
                string content = await storage.GetFileContentsAsync("test.txt");
                Assert.Equal("test", content);
                await storage.RenameFileAsync("test.txt", "new.txt");
                Assert.True((await storage.GetFileListAsync()).Any(f => f.Path == "new.txt"));
                await storage.DeleteFileAsync("new.txt");
                Assert.Equal(0, (await storage.GetFileListAsync()).Count());
            }
        }

        public virtual async Task CanSaveFilesAsync() {
            await ResetAsync();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            string readmeFile = Path.GetFullPath(@"..\..\..\..\..\README.md");

            using (storage) {
                Assert.False(await storage.ExistsAsync("README.md"));

                using (var stream = new NonSeekableStream(File.Open(readmeFile, FileMode.Open, FileAccess.Read))) {
                    bool result = await storage.SaveFileAsync("README.md", stream);
                    Assert.True(result);
                }

                Assert.Equal(1, (await storage.GetFileListAsync()).Count());
                Assert.True(await storage.ExistsAsync("README.md"));

                using (var stream = await storage.GetFileStreamAsync("README.md")) {
                    string result = await new StreamReader(stream).ReadToEndAsync();
                    Assert.Equal(File.ReadAllText(readmeFile), result);
                }
            }
        }

        protected async Task ResetAsync() {
            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                var files = (await storage.GetFileListAsync()).ToList();
                if (files.Any())
                    Debug.WriteLine("Got files");
                else
                    Debug.WriteLine("No files");
                await storage.DeleteFilesAsync(files);
                Assert.Equal(0, (await storage.GetFileListAsync()).Count());
            }
        }

        public virtual async Task CanConcurrentlyManageFiles() {
            await ResetAsync();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                const string queueFolder = "q";
                var queueItems = new BlockingCollection<int>();

                var info = await storage.GetFileInfoAsync("nope");
                Assert.Null(info);

                await Run.InParallel(10, async i => {
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
                
                Assert.Equal(10, (await storage.GetFileListAsync()).Count());

                await Run.InParallel(10, async i => {
                    string path = Path.Combine(queueFolder, queueItems.Random() + ".json");
                    var eventPost = await storage.GetEventPostAndSetActiveAsync(Path.Combine(queueFolder, RandomData.GetInt(0, 25) + ".json"), _logger);
                    if (eventPost == null)
                        return;

                    if (RandomData.GetBool()) {
                        await storage.CompleteEventPost(path, eventPost.ProjectId, DateTime.UtcNow, true, _logger);
                    } else
                        await storage.SetNotActiveAsync(path, _logger);
                });
            }
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
                logger?.Error(ex, () => $"Error retrieving event post data \"{path}\": {ex.Message}");
                return null;
            }

            return eventPostInfo;
        }

        public static async Task<bool> SetNotActiveAsync(this IFileStorage storage, string path, ILogger logger = null) {
            try {
                return await storage.DeleteFileAsync(path + ".x");
            } catch (Exception ex) {
                logger?.Error(ex, () => $"Error deleting work marker \"{path}.x\": {ex.Message}");
            }

            return false;
        }

        public static async Task<bool> CompleteEventPost(this IFileStorage storage, string path, string projectId, DateTime created, bool shouldArchive = true, ILogger logger = null) {
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
                logger?.Error(ex, () => $"Error archiving event post data \"{path}\": {ex.Message}");
                return false;
            }

            await storage.SetNotActiveAsync(path);

            return true;
        }
    }
}