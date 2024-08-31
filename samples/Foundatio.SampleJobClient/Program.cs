using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.SampleJobClient;

public class Program
{
    private static IQueue<PingRequest> _queue;
    private static IMessageBus _messageBus;
    private static TestLogger _loggerFactory;
    private static ILogger _logger;
    private static bool _isRunning = true;
    private static CancellationTokenSource _continuousEnqueueTokenSource = new();

    public static void Main(string[] args)
    {
        _loggerFactory = new TestLogger();
        _loggerFactory.SetLogLevel<RedisMessageBus>(LogLevel.Trace);
        _loggerFactory.MaxLogEntriesToStore = Console.WindowHeight - (OPTIONS_MENU_LINE_COUNT + SEPERATOR_LINE_COUNT) - 1;
        _logger = _loggerFactory.CreateLogger<Program>();

        var muxer = ConnectionMultiplexer.Connect("localhost", o => o.LoggerFactory = _loggerFactory);
        _queue = new RedisQueue<PingRequest>(new RedisQueueOptions<PingRequest> { ConnectionMultiplexer = muxer });
        _messageBus = new RedisMessageBus(o => o.Subscriber(muxer.GetSubscriber()).LoggerFactory(_loggerFactory).MapMessageTypeToClassName<EchoMessage>());

        MonitorKeyPress();
        DrawLoop();
    }

    private static void EnqueuePing(int count)
    {
        for (int i = 0; i < count; i++)
            _queue.EnqueueAsync(new PingRequest { Data = "b", PercentChanceOfException = 0 }).GetAwaiter().GetResult();

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Enqueued {Count} ping requests", count);
    }

    private static void EnqueueContinuousPings(int count, CancellationToken token)
    {
        do
        {
            for (int i = 0; i < count; i++)
                _queue.EnqueueAsync(new PingRequest { Data = "b", PercentChanceOfException = 0 }).GetAwaiter().GetResult();

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Enqueued {Count} ping requests", count);
        } while (!token.IsCancellationRequested);
    }

    private static void HandleKey(ConsoleKey key)
    {
        if (key == ConsoleKey.D1)
        {
            EnqueuePing(1);
        }
        else if (key == ConsoleKey.D2)
        {
            EnqueuePing(100);
        }
        else if (key == ConsoleKey.D3)
        {
            if (_continuousEnqueueTokenSource.IsCancellationRequested)
                _continuousEnqueueTokenSource = new CancellationTokenSource();

            _logger.LogWarning("Starting continuous ping...");
            Task.Run(() => EnqueueContinuousPings(25, _continuousEnqueueTokenSource.Token), _continuousEnqueueTokenSource.Token);
        }
        else if (key == ConsoleKey.M)
        {
            _messageBus.PublishAsync(new EchoMessage { Message = "Hello World!" }).GetAwaiter().GetResult();
        }
        else if (key == ConsoleKey.Q)
        {
            _isRunning = false;
        }
        else if (key == ConsoleKey.S)
        {
            _logger.LogWarning("Cancelling continuous ping.");
            _continuousEnqueueTokenSource.Cancel();
        }
    }

    private static void MonitorKeyPress()
    {
        Task.Run(() =>
        {
            while (_isRunning)
            {
                while (!Console.KeyAvailable)
                {
                    Thread.Sleep(250);
                }
                var key = Console.ReadKey(true).Key;

                HandleKey(key);
            }
        });
    }

    private static void DrawLoop()
    {
        Console.CursorVisible = false;

        while (_isRunning)
        {
            ClearConsoleLines(0, OPTIONS_MENU_LINE_COUNT + SEPERATOR_LINE_COUNT + _loggerFactory.MaxLogEntriesToStore);

            DrawOptionsMenu();
            DrawLogMessages();

            Console.SetCursorPosition(0, OPTIONS_MENU_LINE_COUNT + 1);

            Thread.Sleep(250);
        }
    }

    private const int OPTIONS_MENU_LINE_COUNT = 5;
    private const int SEPERATOR_LINE_COUNT = 2;
    private static void DrawOptionsMenu()
    {
        Console.SetCursorPosition(0, 0);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("1: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("Enqueue 1");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" | ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("2: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("Enqueue 100");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" | ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("3: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Enqueue continuous");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("M: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Send echo message");

        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("S: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Stop");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Q: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Quit");
    }

    private static void DrawLogMessages()
    {
        Console.SetCursorPosition(0, OPTIONS_MENU_LINE_COUNT + SEPERATOR_LINE_COUNT);
        foreach (var logEntry in _loggerFactory.LogEntries.ToArray())
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = GetColor(logEntry);
            Console.WriteLine(logEntry);
            Console.ForegroundColor = originalColor;
        }
    }

    private static void ClearConsoleLines(int startLine = 0, int endLine = -1)
    {
        if (endLine < 0)
            endLine = Console.WindowHeight - 2;

        int currentLine = Console.CursorTop;
        int currentPosition = Console.CursorLeft;

        for (int i = startLine; i <= endLine; i++)
        {
            Console.SetCursorPosition(0, i);
            Console.Write(new string(' ', Console.WindowWidth));
        }

        Console.SetCursorPosition(currentPosition, currentLine);
    }

    private static ConsoleColor GetColor(LogEntry logEntry)
    {
        switch (logEntry.LogLevel)
        {
            case LogLevel.Debug:
                return ConsoleColor.Gray;
            case LogLevel.Error:
                return ConsoleColor.Yellow;
            case LogLevel.Information:
                return ConsoleColor.White;
            case LogLevel.Trace:
                return ConsoleColor.DarkGray;
            case LogLevel.Warning:
                return ConsoleColor.Magenta;
            case LogLevel.Critical:
                return ConsoleColor.Red;
        }

        return ConsoleColor.White;
    }
}

public class EchoMessage
{
    public string Message { get; set; }
}

public class PingRequest
{
    public string Data { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int PercentChanceOfException { get; set; } = 0;
}
