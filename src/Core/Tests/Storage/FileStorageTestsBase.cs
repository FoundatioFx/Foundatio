using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage {
    public abstract class FileStorageTestsBase : CaptureTests {
        protected FileStorageTestsBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected virtual IFileStorage GetStorage() {
            return null;
        }

        public virtual async Task CanGetEmptyFileListOnMissingDirectory() {
            await ResetAsync().AnyContext();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                Assert.Equal(0, (await storage.GetFileListAsync(Guid.NewGuid() + "\\*").AnyContext()).Count());
            }
        }

        public virtual async Task CanGetFileListForSingleFolder() {
            await ResetAsync().AnyContext();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync(@"archived\archived.txt", "archived").AnyContext();
                await storage.SaveFileAsync(@"q\new.txt", "new").AnyContext();
                Assert.Equal(2, (await storage.GetFileListAsync().AnyContext()).Count());
                Assert.Equal(1, (await storage.GetFileListAsync(limit: 1).AnyContext()).Count());
                Assert.Equal(1, (await storage.GetFileListAsync(@"archived\*").AnyContext()).Count());
                Assert.Equal(1, (await storage.GetFileListAsync(@"q\*").AnyContext()).Count());

                var file = (await storage.GetFileListAsync(@"archived\*").AnyContext()).FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("archived", await storage.GetFileContentsAsync(@"archived\archived.txt").AnyContext());


                file = (await storage.GetFileListAsync(@"q\*").AnyContext()).FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("new", await storage.GetFileContentsAsync(@"q\new.txt").AnyContext());
            }
        }

        public virtual async Task CanManageFiles() {
            await ResetAsync().AnyContext();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                await storage.SaveFileAsync("test.txt", "test").AnyContext();
                Assert.Equal(1, (await storage.GetFileListAsync().AnyContext()).Count());
                var file = (await storage.GetFileListAsync().AnyContext()).FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("test.txt", file.Path);
                string content = await storage.GetFileContentsAsync("test.txt").AnyContext();
                Assert.Equal("test", content);
                await storage.RenameFileAsync("test.txt", "new.txt").AnyContext();
                Assert.True((await storage.GetFileListAsync().AnyContext()).Any(f => f.Path == "new.txt"));
                await storage.DeleteFileAsync("new.txt").AnyContext();
                Assert.Equal(0, (await storage.GetFileListAsync().AnyContext()).Count());
            }
        }

        public virtual async Task CanSaveFilesAsync() {
            await ResetAsync().AnyContext();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            string readmeFile = Path.GetFullPath(@"..\..\..\..\..\README.md");

            using (storage) {
                using (var stream = new NonSeekableStream(File.Open(readmeFile, FileMode.Open, FileAccess.Read))) {
                    bool result = await storage.SaveFileAsync("README.md", stream).AnyContext();
                    Assert.True(result);
                }

                Assert.Equal(1, (await storage.GetFileListAsync().AnyContext()).Count());

                using (var stream = await storage.GetFileStreamAsync("README.md").AnyContext()) {
                    string result = await new StreamReader(stream).ReadToEndAsync().AnyContext();
                    Assert.Equal(File.ReadAllText(readmeFile), result);
                }
            }
        }

        protected async Task ResetAsync() {
            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                var files = (await storage.GetFileListAsync().AnyContext()).ToList();
                if (files.Any())
                    Debug.WriteLine("Got files");
                else
                    Debug.WriteLine("No files");
                await storage.DeleteFilesAsync(files).AnyContext();
                Assert.Equal(0, (await storage.GetFileListAsync().AnyContext()).Count());
            }
        }

        public virtual async Task CanConcurrentlyManageFiles() {
            await ResetAsync().AnyContext();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                const string queueFolder = "q";
                var queueItems = new BlockingCollection<int>();

                var info = await storage.GetFileInfoAsync("nope").AnyContext();
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

                    await storage.SaveObjectAsync(Path.Combine(queueFolder, i + ".json"), ev).AnyContext();
                    queueItems.Add(i);
                }).AnyContext();
                
                Assert.Equal(10, (await storage.GetFileListAsync().AnyContext()).Count());

                await Run.InParallel(10, async i => {
                    string path = Path.Combine(queueFolder, queueItems.Random() + ".json");
                    var eventPost = await storage.GetEventPostAndSetActiveAsync(Path.Combine(queueFolder, RandomData.GetInt(0, 25) + ".json")).AnyContext();
                    if (eventPost == null)
                        return;

                    if (RandomData.GetBool()) {
                        await storage.CompleteEventPost(path, eventPost.ProjectId, DateTime.UtcNow, true).AnyContext();
                    } else
                        await storage.SetNotActiveAsync(path).AnyContext();
                }).AnyContext();
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
        public static async Task<PostInfo> GetEventPostAndSetActiveAsync(this IFileStorage storage, string path) {
            PostInfo eventPostInfo = null;
            try {
                eventPostInfo = await storage.GetObjectAsync<PostInfo>(path).AnyContext();
                if (eventPostInfo == null)
                    return null;

                if (!await storage.ExistsAsync(path + ".x").AnyContext() && !await storage.SaveFileAsync(path + ".x", String.Empty).AnyContext())
                    return null;
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error retrieving event post data \"{0}\".", path).Write();
                return null;
            }

            return eventPostInfo;
        }

        public static async Task<bool> SetNotActiveAsync(this IFileStorage storage, string path) {
            try {
                return await storage.DeleteFileAsync(path + ".x").AnyContext();
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error deleting work marker \"{0}\".", path + ".x").Write();
            }

            return false;
        }

        public static async Task<bool> CompleteEventPost(this IFileStorage storage, string path, string projectId, DateTime created, bool shouldArchive = true) {
            // don't move files that are already in the archive
            if (path.StartsWith("archive"))
                return true;

            string archivePath = String.Format("archive\\{0}\\{1}\\{2}", projectId, created.ToString("yy\\\\MM\\\\dd"), Path.GetFileName(path));

            try {
                if (shouldArchive) {
                    if (!await storage.RenameFileAsync(path, archivePath).AnyContext())
                        return false;
                } else {
                    if (!await storage.DeleteFileAsync(path).AnyContext())
                        return false;
                }
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error archiving event post data \"{0}\".", path).Write();
                return false;
            }

            await storage.SetNotActiveAsync(path).AnyContext();

            return true;
        }
    }
}