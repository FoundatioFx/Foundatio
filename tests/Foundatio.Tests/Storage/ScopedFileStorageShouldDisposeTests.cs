using Foundatio.Storage;
using Foundatio.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage;
    public class ScopedFileStorageShouldDisposeTests : TestWithLoggingBase
    {
        public ScopedFileStorageShouldDisposeTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Dispose_DefaultBehavior_DoesNotDisposeUnderlyingStorage()
        {
            // Arrange
            var innerStorage = new TrackableDisposableFileStorage();
            var scopedStorage = new ScopedFileStorage(innerStorage, "test");

            // Act
            scopedStorage.Dispose();

            // Assert
            Assert.False(innerStorage.WasDisposed);
        }

        [Fact]
        public void Dispose_ShouldDisposeTrue_DisposesUnderlyingStorage()
        {
            // Arrange
            var innerStorage = new TrackableDisposableFileStorage();
            var scopedStorage = new ScopedFileStorage(innerStorage, "test", shouldDispose: true);

            // Act
            scopedStorage.Dispose();

            // Assert
            Assert.True(innerStorage.WasDisposed);
        }

        [Fact]
        public void Dispose_WithUsingStatementAndShouldDisposeTrue_DisposesUnderlyingStorage()
        {
            // Arrange
            var innerStorage = new TrackableDisposableFileStorage();

            // Act
            using (new ScopedFileStorage(innerStorage, "test", shouldDispose: true))
            {
                // No operations needed
            }

            // Assert
            Assert.True(innerStorage.WasDisposed);
        }

        [Fact]
        public void Dispose_WithUsingStatementAndShouldDisposeFalse_DoesNotDisposeUnderlyingStorage()
        {
            // Arrange
            var innerStorage = new TrackableDisposableFileStorage();

            // Act
            using (new ScopedFileStorage(innerStorage, "test", shouldDispose: false))
            {
                // No operations needed
            }

            // Assert
            Assert.False(innerStorage.WasDisposed);
        }

        private class TrackableDisposableFileStorage : InMemoryFileStorage
        {
            public bool WasDisposed { get; private set; }

            public override void Dispose()
            {
                WasDisposed = true;
                base.Dispose();
            }
        }
    }
