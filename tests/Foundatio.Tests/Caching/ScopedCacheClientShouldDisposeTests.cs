using System;
using Foundatio.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;
    public class ScopedCacheClientShouldDisposeTests : IDisposable
    {
        private readonly ITestOutputHelper _output;

        public ScopedCacheClientShouldDisposeTests(ITestOutputHelper output)
        {
            _output = output;
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
            using (var scopedCache = new ScopedCacheClient(innerCache, "test", shouldDispose: true))
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
            using (var scopedCache = new ScopedCacheClient(innerCache, "test", shouldDispose: false))
            {
                // No operations needed
            }

            // Assert
            Assert.False(innerCache.WasDisposed);
        }

        public void Dispose()
        {
            // Cleanup if needed
        }

        private class TrackableDisposableCacheClient : InMemoryCacheClient
        {
            public bool WasDisposed { get; private set; }

            public new void Dispose()
            {
                WasDisposed = true;
                base.Dispose();
            }
        }
    }
