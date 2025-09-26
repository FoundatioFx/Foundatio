using Foundatio.Caching;
using Foundatio.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;
    public class ScopedCacheClientShouldDisposeTests : TestWithLoggingBase
    {
        public ScopedCacheClientShouldDisposeTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Dispose_DefaultBehavior_DoesNotDisposeUnderlyingCache()
        {
            // Arrange
            var innerCache = new TrackableDisposableCacheClient();
            var scopedCache = new ScopedCacheClient(innerCache, "test");

            // Act
            scopedCache.Dispose();

            // Assert
            Assert.False(innerCache.WasDisposed);
        }

        [Fact]
        public void Dispose_ShouldDisposeTrue_DisposesUnderlyingCache()
        {
            // Arrange
            var innerCache = new TrackableDisposableCacheClient();
            var scopedCache = new ScopedCacheClient(innerCache, "test", shouldDispose: true);

            // Act
            scopedCache.Dispose();

            // Assert
            Assert.True(innerCache.WasDisposed);
        }

        [Fact]
        public void Dispose_WithUsingStatementAndShouldDisposeTrue_DisposesUnderlyingCache()
        {
            // Arrange
            var innerCache = new TrackableDisposableCacheClient();

            // Act
            using (new ScopedCacheClient(innerCache, "test", shouldDispose: true))
            {
                // No operations needed
            }

            // Assert
            Assert.True(innerCache.WasDisposed);
        }

        [Fact]
        public void Dispose_WithUsingStatementAndShouldDisposeFalse_DoesNotDisposeUnderlyingCache()
        {
            // Arrange
            var innerCache = new TrackableDisposableCacheClient();

            // Act
            using (new ScopedCacheClient(innerCache, "test", shouldDispose: false))
            {
                // No operations needed
            }

            // Assert
            Assert.False(innerCache.WasDisposed);
        }

        private class TrackableDisposableCacheClient : InMemoryCacheClient
        {
            public bool WasDisposed { get; private set; }

            public override void Dispose()
            {
                WasDisposed = true;
                base.Dispose();
            }
        }
    }
