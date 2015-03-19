using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using NLog.Fluent;

namespace Foundatio.Tests.Storage {
    public abstract class FileStorageTestsBase {
        protected virtual IFileStorage GetStorage() {
            return null;
        }

        public virtual void CanGetEmptyFileListOnMissingDirectory() {
            Reset();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                Assert.Equal(0, storage.GetFileList(Guid.NewGuid() + "\\*").Count());
            }
        }

        public virtual void CanGetFileListForSingleFolder() {
            Reset();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                storage.SaveFile(@"archived\archived.txt", "archived");
                storage.SaveFile(@"q\new.txt", "new");
                Assert.Equal(2, storage.GetFileList().Count());
                Assert.Equal(1, storage.GetFileList(limit: 1).Count());
                Assert.Equal(1, storage.GetFileList(@"archived\*").Count());
                Assert.Equal(1, storage.GetFileList(@"q\*").Count());

                var file = storage.GetFileList(@"archived\*").FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("archived", storage.GetFileContents(@"archived\archived.txt"));


                file = storage.GetFileList(@"q\*").FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("new", storage.GetFileContents(@"q\new.txt"));
            }
        }

        public virtual void CanManageFiles() {
            Reset();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                storage.SaveFile("test.txt", "test");
                Assert.Equal(1, storage.GetFileList().Count());
                var file = storage.GetFileList().FirstOrDefault();
                Assert.NotNull(file);
                Assert.Equal("test.txt", file.Path);
                string content = storage.GetFileContents("test.txt");
                Assert.Equal("test", content);
                storage.RenameFile("test.txt", "new.txt");
                Assert.True(storage.GetFileList().Any(f => f.Path == "new.txt"));
                storage.DeleteFile("new.txt");
                Assert.Equal(0, storage.GetFileList().Count());
            }
        }

        protected void Reset() {
            var storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                var files = storage.GetFileList().ToList();
                if (files.Any())
                    Debug.WriteLine("Got files");
                else
                    Debug.WriteLine("No files");
                storage.DeleteFiles(files);
                Assert.Equal(0, storage.GetFileList().Count());
            }
        }

        public virtual void CanConcurrentlyManageFiles() {
            Reset();

            IFileStorage storage = GetStorage();
            if (storage == null)
                return;

            using (storage) {
                const string queueFolder = "q";
                var queueItems = new BlockingCollection<int>();

                var info = storage.GetFileInfo("nope");
                Assert.Null(info);

                Parallel.For(0, 10, i => {
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
                    storage.SaveObject(Path.Combine(queueFolder, i + ".json"), ev);
                    queueItems.Add(i);
                });
                Assert.Equal(10, storage.GetFileList().Count());

                Parallel.For(0, 10, i => {
                    string path = Path.Combine(queueFolder, queueItems.Random() + ".json");
                    var eventPost = storage.GetEventPostAndSetActive(Path.Combine(queueFolder, RandomData.GetInt(0, 25) + ".json"));
                    if (eventPost == null)
                        return;

                    if (RandomData.GetBool()) {
                        storage.CompleteEventPost(path, eventPost.ProjectId, DateTime.UtcNow, true);
                    } else
                        storage.SetNotActive(path);
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
        public static PostInfo GetEventPostAndSetActive(this IFileStorage storage, string path) {
            PostInfo eventPostInfo = null;
            try {
                eventPostInfo = storage.GetObject<PostInfo>(path);
                if (eventPostInfo == null)
                    return null;

                if (!storage.Exists(path + ".x") && !storage.SaveFile(path + ".x", String.Empty))
                    return null;
            } catch (Exception ex) {
                Log.Error().Exception(ex).Message("Error retrieving event post data \"{0}\".", path).Write();
                return null;
            }

            return eventPostInfo;
        }

        public static bool SetNotActive(this IFileStorage storage, string path) {
            try {
                return storage.DeleteFile(path + ".x");
            } catch (Exception ex) {
                Log.Error().Exception(ex).Message("Error deleting work marker \"{0}\".", path + ".x").Write();
            }

            return false;
        }

        public static bool CompleteEventPost(this IFileStorage storage, string path, string projectId, DateTime created, bool shouldArchive = true) {
            // don't move files that are already in the archive
            if (path.StartsWith("archive"))
                return true;

            string archivePath = String.Format("archive\\{0}\\{1}\\{2}", projectId, created.ToString("yy\\\\MM\\\\dd"), Path.GetFileName(path));

            try {
                if (shouldArchive) {
                    if (!storage.RenameFile(path, archivePath))
                        return false;
                } else {
                    if (!storage.DeleteFile(path))
                        return false;
                }
            } catch (Exception ex) {
                Log.Error().Exception(ex).Message("Error archiving event post data \"{0}\".", path).Write();
                return false;
            }

            storage.SetNotActive(path);

            return true;
        }

    }
}
