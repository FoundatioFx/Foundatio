using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Foundatio.Utility;

namespace Foundatio.Benchmarks;

/// <summary>
/// Benchmarks for DeepClone() performance across different object types and sizes.
/// Tests realistic scenarios based on actual Foundatio usage patterns:
/// - Cache entries (InMemoryCacheClient)
/// - Queue messages (InMemoryQueue)
/// - File storage specs (InMemoryFileStorage)
/// - Large nested objects (error/event processing systems)
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[BenchmarkCategory("DeepClone")]
public class DeepCloneBenchmarks
{
    // Small objects - typical cache entries
    private SmallObject _smallObject;
    private SmallObjectWithCollections _smallObjectWithCollections;

    // Medium objects - typical queue messages
    private MediumNestedObject _mediumNestedObject;
    private FileSpec _fileSpec;

    // Large objects - ~10MB realistic data
    private LargeEventDocument _largeEventDocument;
    private LargeLogBatch _largeLogBatch;

    // Dynamic type objects - object properties with various runtime types
    private ObjectWithDynamicProperties _dynamicWithDictionary;
    private ObjectWithDynamicProperties _dynamicWithNestedObject;
    private ObjectWithDynamicProperties _dynamicWithArray;

    // Arrays and collections
    private string[] _stringArray;
    private List<MediumNestedObject> _objectList;
    private Dictionary<string, LargeEventDocument> _objectDictionary;

    // Seed for deterministic data generation
    private const int Seed = 42;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(Seed);

        // Small objects
        _smallObject = CreateSmallObject(random);
        _smallObjectWithCollections = CreateSmallObjectWithCollections(random);

        // Medium objects
        _mediumNestedObject = CreateMediumNestedObject(random);
        _fileSpec = CreateFileSpec(random);

        // Large objects (~10MB each)
        _largeEventDocument = CreateLargeEventDocument(random, targetSizeBytes: 10 * 1024 * 1024);
        _largeLogBatch = CreateLargeLogBatch(random, targetSizeBytes: 10 * 1024 * 1024);

        // Dynamic type objects
        _dynamicWithDictionary = CreateDynamicWithDictionary(random);
        _dynamicWithNestedObject = CreateDynamicWithNestedObject(random);
        _dynamicWithArray = CreateDynamicWithArray(random);

        // Arrays and collections
        _stringArray = CreateStringArray(random, 1000);
        _objectList = CreateObjectList(random, 100);
        _objectDictionary = CreateObjectDictionary(random, 50);
    }

    [Benchmark]
    [BenchmarkCategory("Small", "KnownTypes")]
    public SmallObject DeepClone_SmallObject()
    {
        return _smallObject.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Small", "KnownTypes")]
    public SmallObjectWithCollections DeepClone_SmallObjectWithCollections()
    {
        return _smallObjectWithCollections.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Medium", "KnownTypes")]
    public MediumNestedObject DeepClone_MediumNestedObject()
    {
        return _mediumNestedObject.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Medium", "KnownTypes")]
    public FileSpec DeepClone_FileSpec()
    {
        return _fileSpec.DeepClone();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Large", "KnownTypes")]
    public LargeEventDocument DeepClone_LargeEventDocument_10MB()
    {
        return _largeEventDocument.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Large", "KnownTypes")]
    public LargeLogBatch DeepClone_LargeLogBatch_10MB()
    {
        return _largeLogBatch.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Dynamic")]
    public ObjectWithDynamicProperties DeepClone_DynamicWithDictionary()
    {
        return _dynamicWithDictionary.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Dynamic")]
    public ObjectWithDynamicProperties DeepClone_DynamicWithNestedObject()
    {
        return _dynamicWithNestedObject.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Dynamic")]
    public ObjectWithDynamicProperties DeepClone_DynamicWithArray()
    {
        return _dynamicWithArray.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Collections")]
    public string[] DeepClone_StringArray_1000()
    {
        return _stringArray.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Collections")]
    public List<MediumNestedObject> DeepClone_ObjectList_100()
    {
        return _objectList.DeepClone();
    }

    [Benchmark]
    [BenchmarkCategory("Collections")]
    public Dictionary<string, LargeEventDocument> DeepClone_ObjectDictionary_50()
    {
        return _objectDictionary.DeepClone();
    }

    private static SmallObject CreateSmallObject(Random random)
    {
        return new SmallObject
        {
            Id = random.Next(),
            Name = GenerateString(random, 50),
            CreatedAt = DateTime.UtcNow.AddDays(-random.Next(365)),
            IsActive = random.Next(2) == 1,
            Score = random.NextDouble() * 100
        };
    }

    private static SmallObjectWithCollections CreateSmallObjectWithCollections(Random random)
    {
        return new SmallObjectWithCollections
        {
            Id = random.Next(),
            Name = GenerateString(random, 50),
            Tags = GenerateStringList(random, 10, 20),
            Metadata = GenerateStringDictionary(random, 5, 30)
        };
    }

    private static MediumNestedObject CreateMediumNestedObject(Random random)
    {
        return new MediumNestedObject
        {
            Id = Guid.NewGuid(),
            Type = GenerateString(random, 30),
            Source = GenerateString(random, 100),
            Message = GenerateString(random, 500),
            Timestamp = DateTime.UtcNow.AddMinutes(-random.Next(10000)),
            Level = random.Next(1, 6),
            Tags = GenerateStringList(random, 5, 15),
            Properties = GenerateStringDictionary(random, 10, 50),
            User = new UserInfo
            {
                Id = Guid.NewGuid().ToString(),
                Name = GenerateString(random, 30),
                Email = $"{GenerateString(random, 10)}@example.com",
                Roles = GenerateStringList(random, 3, 20)
            },
            Request = new RequestInfo
            {
                Method = random.Next(4) switch { 0 => "GET", 1 => "POST", 2 => "PUT", _ => "DELETE" },
                Path = $"/api/{GenerateString(random, 20)}/{random.Next(1000)}",
                QueryString = GenerateStringDictionary(random, 3, 20),
                Headers = GenerateStringDictionary(random, 8, 100),
                ClientIp = $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}",
                UserAgent = GenerateString(random, 150)
            }
        };
    }

    private static FileSpec CreateFileSpec(Random random)
    {
        return new FileSpec
        {
            Path = $"/storage/{GenerateString(random, 20)}/{GenerateString(random, 30)}.dat",
            Created = DateTime.UtcNow.AddDays(-random.Next(365)),
            Modified = DateTime.UtcNow.AddHours(-random.Next(24)),
            Size = random.Next(1000, 10000000),
            Data = GenerateStringDictionary(random, 5, 50)
        };
    }

    private static LargeEventDocument CreateLargeEventDocument(Random random, int targetSizeBytes)
    {
        // For 10MB target: Create extended data entries with large strings
        // Each char in .NET is 2 bytes, plus string object overhead (~26 bytes)
        // We want the cloned object to be ~10MB

        var stackFrameCount = 100;
        var extendedDataCount = 200;
        // Calculate string length to achieve target size
        // Each string entry: ~50KB of chars = 25K chars
        var extendedDataStringLength = Math.Max(100, targetSizeBytes / extendedDataCount / 2);

        var stackFrames = new List<StackFrameInfo>(stackFrameCount);
        for (int i = 0; i < stackFrameCount; i++)
        {
            stackFrames.Add(new StackFrameInfo
            {
                FileName = $"/src/{GenerateString(random, 30)}/{GenerateString(random, 40)}.cs",
                LineNumber = random.Next(1, 5000),
                ColumnNumber = random.Next(1, 200),
                MethodName = GenerateString(random, 50),
                TypeName = $"{GenerateString(random, 30)}.{GenerateString(random, 40)}",
                Namespace = $"Company.{GenerateString(random, 20)}.{GenerateString(random, 20)}",
                Parameters = GenerateStringList(random, 5, 30),
                LocalVariables = GenerateStringDictionary(random, 3, 50)
            });
        }

        var extendedData = new Dictionary<string, object>(extendedDataCount);
        for (int i = 0; i < extendedDataCount; i++)
        {
            extendedData[$"data_{i}"] = GenerateString(random, extendedDataStringLength);
        }

        return new LargeEventDocument
        {
            Id = Guid.NewGuid().ToString(),
            OrganizationId = Guid.NewGuid().ToString(),
            ProjectId = Guid.NewGuid().ToString(),
            StackId = Guid.NewGuid().ToString(),
            Type = "error",
            Source = GenerateString(random, 200),
            Message = GenerateString(random, 2000),
            Date = DateTime.UtcNow.AddMinutes(-random.Next(10000)),
            Count = random.Next(1, 1000),
            IsFirstOccurrence = random.Next(2) == 1,
            IsFixed = random.Next(2) == 1,
            IsHidden = random.Next(2) == 1,
            Tags = GenerateStringList(random, 20, 30),
            Geo = $"{random.NextDouble() * 180 - 90},{random.NextDouble() * 360 - 180}",
            Value = random.NextDouble() * 10000,
            StackTrace = stackFrames,
            ExtendedData = extendedData,
            ReferenceIds = GenerateStringList(random, 10, 36),
            User = new UserInfo
            {
                Id = Guid.NewGuid().ToString(),
                Name = GenerateString(random, 50),
                Email = $"{GenerateString(random, 15)}@example.com",
                Roles = GenerateStringList(random, 5, 20)
            },
            Request = new RequestInfo
            {
                Method = "POST",
                Path = $"/api/{GenerateString(random, 30)}/{random.Next(10000)}",
                QueryString = GenerateStringDictionary(random, 10, 50),
                Headers = GenerateStringDictionary(random, 20, 200),
                ClientIp = $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}",
                UserAgent = GenerateString(random, 300)
            },
            Environment = new EnvironmentInfo
            {
                MachineName = GenerateString(random, 30),
                ProcessorCount = random.Next(1, 128),
                TotalPhysicalMemory = random.NextInt64(1024L * 1024 * 1024, 256L * 1024 * 1024 * 1024),
                AvailablePhysicalMemory = random.NextInt64(1024L * 1024 * 1024, 64L * 1024 * 1024 * 1024),
                OsName = "Windows 11",
                OsVersion = "10.0.22631",
                Architecture = "x64",
                RuntimeVersion = ".NET 8.0.0",
                ProcessName = GenerateString(random, 30),
                ProcessId = random.Next(1, 65535),
                CommandLine = GenerateString(random, 500),
                EnvironmentVariables = GenerateStringDictionary(random, 30, 100)
            }
        };
    }

    private static LargeLogBatch CreateLargeLogBatch(Random random, int targetSizeBytes)
    {
        // Each log entry is roughly 2000-5000 bytes
        // Target ~10MB total
        var entryCount = targetSizeBytes / 3000;

        var entries = new List<LogEntry>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            entries.Add(new LogEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow.AddMilliseconds(-random.Next(1000000)),
                Level = random.Next(6) switch { 0 => "Trace", 1 => "Debug", 2 => "Info", 3 => "Warn", 4 => "Error", _ => "Fatal" },
                Category = $"{GenerateString(random, 20)}.{GenerateString(random, 30)}",
                Message = GenerateString(random, 500),
                Exception = random.Next(10) == 0 ? GenerateString(random, 2000) : null,
                Properties = GenerateStringDictionary(random, 5, 100),
                Scopes = GenerateStringList(random, 3, 50),
                TraceId = Guid.NewGuid().ToString("N"),
                SpanId = random.NextInt64().ToString("x16"),
                ParentSpanId = random.Next(2) == 1 ? random.NextInt64().ToString("x16") : null
            });
        }

        return new LargeLogBatch
        {
            BatchId = Guid.NewGuid(),
            Source = GenerateString(random, 100),
            CreatedAt = DateTime.UtcNow,
            Entries = entries,
            Metadata = GenerateStringDictionary(random, 10, 50)
        };
    }

    private static ObjectWithDynamicProperties CreateDynamicWithDictionary(Random random)
    {
        return new ObjectWithDynamicProperties
        {
            Id = random.Next(),
            Name = GenerateString(random, 50),
            DynamicData = GenerateStringDictionary(random, 20, 100),
            NestedDynamic = new Dictionary<string, object>
            {
                ["nested1"] = GenerateStringDictionary(random, 5, 50),
                ["nested2"] = GenerateStringList(random, 10, 30),
                ["nested3"] = random.NextDouble()
            }
        };
    }

    private static ObjectWithDynamicProperties CreateDynamicWithNestedObject(Random random)
    {
        return new ObjectWithDynamicProperties
        {
            Id = random.Next(),
            Name = GenerateString(random, 50),
            DynamicData = CreateSmallObject(random),
            NestedDynamic = CreateMediumNestedObject(random)
        };
    }

    private static ObjectWithDynamicProperties CreateDynamicWithArray(Random random)
    {
        var array = new object[100];
        for (int i = 0; i < array.Length; i++)
        {
            int mod = i % 3;
            array[i] = mod switch
            {
                0 => (object)GenerateString(random, 100),
                1 => (object)random.NextDouble(),
                _ => (object)CreateSmallObject(random)
            };
        }

        return new ObjectWithDynamicProperties
        {
            Id = random.Next(),
            Name = GenerateString(random, 50),
            DynamicData = array,
            NestedDynamic = new object[] { CreateSmallObject(random), GenerateStringList(random, 5, 20), random.Next() }
        };
    }

    private static string[] CreateStringArray(Random random, int count)
    {
        var array = new string[count];
        for (int i = 0; i < count; i++)
        {
            array[i] = GenerateString(random, 50 + random.Next(100));
        }
        return array;
    }

    private static List<MediumNestedObject> CreateObjectList(Random random, int count)
    {
        var list = new List<MediumNestedObject>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(CreateMediumNestedObject(random));
        }
        return list;
    }

    private static Dictionary<string, LargeEventDocument> CreateObjectDictionary(Random random, int count)
    {
        var dict = new Dictionary<string, LargeEventDocument>(count);
        for (int i = 0; i < count; i++)
        {
            // Create medium-sized event documents for the dictionary (~10KB each)
            dict[$"event_{i}"] = CreateMediumEventDocument(random);
        }
        return dict;
    }

    private static LargeEventDocument CreateMediumEventDocument(Random random)
    {
        // Create a medium-sized event document (~10KB) for collection benchmarks
        var stackFrames = new List<StackFrameInfo>(5);
        for (int i = 0; i < 5; i++)
        {
            stackFrames.Add(new StackFrameInfo
            {
                FileName = $"/src/{GenerateString(random, 20)}/{GenerateString(random, 20)}.cs",
                LineNumber = random.Next(1, 1000),
                ColumnNumber = random.Next(1, 100),
                MethodName = GenerateString(random, 30),
                TypeName = GenerateString(random, 40),
                Namespace = GenerateString(random, 30),
                Parameters = GenerateStringList(random, 3, 20),
                LocalVariables = GenerateStringDictionary(random, 2, 30)
            });
        }

        var extendedData = new Dictionary<string, object>(10);
        for (int i = 0; i < 10; i++)
        {
            extendedData[$"data_{i}"] = GenerateString(random, 200);
        }

        return new LargeEventDocument
        {
            Id = Guid.NewGuid().ToString(),
            OrganizationId = Guid.NewGuid().ToString(),
            ProjectId = Guid.NewGuid().ToString(),
            StackId = Guid.NewGuid().ToString(),
            Type = "error",
            Source = GenerateString(random, 50),
            Message = GenerateString(random, 200),
            Date = DateTime.UtcNow.AddMinutes(-random.Next(10000)),
            Count = random.Next(1, 100),
            IsFirstOccurrence = random.Next(2) == 1,
            IsFixed = random.Next(2) == 1,
            IsHidden = random.Next(2) == 1,
            Tags = GenerateStringList(random, 5, 20),
            Geo = $"{random.NextDouble() * 180 - 90},{random.NextDouble() * 360 - 180}",
            Value = random.NextDouble() * 1000,
            StackTrace = stackFrames,
            ExtendedData = extendedData,
            ReferenceIds = GenerateStringList(random, 3, 36),
            User = new UserInfo
            {
                Id = Guid.NewGuid().ToString(),
                Name = GenerateString(random, 30),
                Email = $"{GenerateString(random, 10)}@example.com",
                Roles = GenerateStringList(random, 2, 15)
            },
            Request = new RequestInfo
            {
                Method = "POST",
                Path = $"/api/{GenerateString(random, 20)}",
                QueryString = GenerateStringDictionary(random, 3, 30),
                Headers = GenerateStringDictionary(random, 5, 50),
                ClientIp = $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}",
                UserAgent = GenerateString(random, 100)
            },
            Environment = new EnvironmentInfo
            {
                MachineName = GenerateString(random, 20),
                ProcessorCount = random.Next(1, 32),
                TotalPhysicalMemory = random.NextInt64(1024L * 1024 * 1024, 64L * 1024 * 1024 * 1024),
                AvailablePhysicalMemory = random.NextInt64(1024L * 1024 * 1024, 32L * 1024 * 1024 * 1024),
                OsName = "Windows 11",
                OsVersion = "10.0.22631",
                Architecture = "x64",
                RuntimeVersion = ".NET 8.0.0",
                ProcessName = GenerateString(random, 20),
                ProcessId = random.Next(1, 65535),
                CommandLine = GenerateString(random, 100),
                EnvironmentVariables = GenerateStringDictionary(random, 10, 50)
            }
        };
    }

    private static string GenerateString(Random random, int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 _-";
        var result = new char[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[random.Next(chars.Length)];
        }
        return new string(result);
    }

    private static List<string> GenerateStringList(Random random, int count, int stringLength)
    {
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(GenerateString(random, stringLength));
        }
        return list;
    }

    private static Dictionary<string, string> GenerateStringDictionary(Random random, int count, int valueLength)
    {
        var dict = new Dictionary<string, string>(count);
        for (int i = 0; i < count; i++)
        {
            dict[$"key_{i}"] = GenerateString(random, valueLength);
        }
        return dict;
    }
}

/// <summary>
/// Small object representing a typical cache entry.
/// </summary>
public class SmallObject
{
    public int Id { get; set; }
    public string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public double Score { get; set; }
}

/// <summary>
/// Small object with collections - typical for configuration or metadata caching.
/// </summary>
public class SmallObjectWithCollections
{
    public int Id { get; set; }
    public string Name { get; set; }
    public List<string> Tags { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}

/// <summary>
/// Medium-sized nested object representing a typical queue message or event.
/// </summary>
public class MediumNestedObject
{
    public Guid Id { get; set; }
    public string Type { get; set; }
    public string Source { get; set; }
    public string Message { get; set; }
    public DateTime Timestamp { get; set; }
    public int Level { get; set; }
    public List<string> Tags { get; set; }
    public Dictionary<string, string> Properties { get; set; }
    public UserInfo User { get; set; }
    public RequestInfo Request { get; set; }
}

/// <summary>
/// File specification - used in InMemoryFileStorage.
/// </summary>
public class FileSpec
{
    public string Path { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public long Size { get; set; }
    public Dictionary<string, string> Data { get; set; }
}

/// <summary>
/// User information - common nested object in events.
/// </summary>
public class UserInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
    public List<string> Roles { get; set; }
}

/// <summary>
/// HTTP request information - common in error tracking systems.
/// </summary>
public class RequestInfo
{
    public string Method { get; set; }
    public string Path { get; set; }
    public Dictionary<string, string> QueryString { get; set; }
    public Dictionary<string, string> Headers { get; set; }
    public string ClientIp { get; set; }
    public string UserAgent { get; set; }
}

/// <summary>
/// Stack frame information for error tracking.
/// </summary>
public class StackFrameInfo
{
    public string FileName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string MethodName { get; set; }
    public string TypeName { get; set; }
    public string Namespace { get; set; }
    public List<string> Parameters { get; set; }
    public Dictionary<string, string> LocalVariables { get; set; }
}

/// <summary>
/// Environment information for error tracking.
/// </summary>
public class EnvironmentInfo
{
    public string MachineName { get; set; }
    public int ProcessorCount { get; set; }
    public long TotalPhysicalMemory { get; set; }
    public long AvailablePhysicalMemory { get; set; }
    public string OsName { get; set; }
    public string OsVersion { get; set; }
    public string Architecture { get; set; }
    public string RuntimeVersion { get; set; }
    public string ProcessName { get; set; }
    public int ProcessId { get; set; }
    public string CommandLine { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; }
}

/// <summary>
/// Large event document - represents error/exception events in systems like Exceptionless.
/// Designed to be ~10MB when fully populated.
/// </summary>
public class LargeEventDocument
{
    public string Id { get; set; }
    public string OrganizationId { get; set; }
    public string ProjectId { get; set; }
    public string StackId { get; set; }
    public string Type { get; set; }
    public string Source { get; set; }
    public string Message { get; set; }
    public DateTime Date { get; set; }
    public int Count { get; set; }
    public bool IsFirstOccurrence { get; set; }
    public bool IsFixed { get; set; }
    public bool IsHidden { get; set; }
    public List<string> Tags { get; set; }
    public string Geo { get; set; }
    public double Value { get; set; }
    public List<StackFrameInfo> StackTrace { get; set; }
    public Dictionary<string, object> ExtendedData { get; set; }
    public List<string> ReferenceIds { get; set; }
    public UserInfo User { get; set; }
    public RequestInfo Request { get; set; }
    public EnvironmentInfo Environment { get; set; }
}

/// <summary>
/// Log entry for batch logging scenarios.
/// </summary>
public class LogEntry
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; }
    public string Category { get; set; }
    public string Message { get; set; }
    public string Exception { get; set; }
    public Dictionary<string, string> Properties { get; set; }
    public List<string> Scopes { get; set; }
    public string TraceId { get; set; }
    public string SpanId { get; set; }
    public string ParentSpanId { get; set; }
}

/// <summary>
/// Large log batch - represents a batch of log entries for bulk processing.
/// Designed to be ~10MB when fully populated.
/// </summary>
public class LargeLogBatch
{
    public Guid BatchId { get; set; }
    public string Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<LogEntry> Entries { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}

/// <summary>
/// Object with dynamic (object) properties to test cloning of unknown types at compile time.
/// This simulates scenarios where JSON deserialization produces Dictionary&lt;string, object&gt; or JToken.
/// </summary>
public class ObjectWithDynamicProperties
{
    public int Id { get; set; }
    public string Name { get; set; }
    public object DynamicData { get; set; }
    public object NestedDynamic { get; set; }
}
