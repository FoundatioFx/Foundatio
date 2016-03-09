using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.SampleJobClient {
    public class Program {
        private static readonly object _writeLock = new object();

        public static void Main(string[] args) {
            //Console.CursorVisible = false;
            //StartDisplayingLogMessages();

            //var tokenSource = new CancellationTokenSource();
            //CancellationToken token = tokenSource.Token;

            //WriteOptionsMenu();

            //while (true) {
            //    Console.SetCursorPosition(0, OPTIONS_MENU_LINE_COUNT + 1);
            //    ConsoleKeyInfo keyInfo = Console.ReadKey(true);

            //    if (keyInfo.Key == ConsoleKey.D1)
            //        SendEvent();
            //    else if (keyInfo.Key == ConsoleKey.D2)
            //        SendContinuousEvents(50, token, 100);
            //    else if (keyInfo.Key == ConsoleKey.D3)
            //        SendContinuousEvents(_delays[_delayIndex], token);
            //    else if (keyInfo.Key == ConsoleKey.D4) {
            //        ExceptionlessClient.Default.SubmitSessionStart();
            //    } else if (keyInfo.Key == ConsoleKey.D5) {
            //        ExceptionlessClient.Default.Configuration.UseSessions(false, null, true);
            //        ExceptionlessClient.Default.SubmitSessionStart();
            //    } else if (keyInfo.Key == ConsoleKey.D6)
            //        ExceptionlessClient.Default.SubmitSessionHeartbeat();
            //    else if (keyInfo.Key == ConsoleKey.D7)
            //        ExceptionlessClient.Default.SubmitSessionEnd();
            //    else if (keyInfo.Key == ConsoleKey.D8)
            //        ExceptionlessClient.Default.Configuration.SetUserIdentity(Guid.NewGuid().ToString("N"));
            //    else if (keyInfo.Key == ConsoleKey.P) {
            //        Console.SetCursorPosition(0, OPTIONS_MENU_LINE_COUNT + 2);
            //        Console.WriteLine("Telling client to process the queue...");

            //        ExceptionlessClient.Default.ProcessQueue();

            //        ClearOutputLines();
            //    } else if (keyInfo.Key == ConsoleKey.F) {
            //        SendAllCapturedEventsFromDisk();
            //        ClearOutputLines();
            //    } else if (keyInfo.Key == ConsoleKey.D) {
            //        _dateSpanIndex++;
            //        if (_dateSpanIndex == _dateSpans.Length)
            //            _dateSpanIndex = 0;
            //        WriteOptionsMenu();
            //    } else if (keyInfo.Key == ConsoleKey.T) {
            //        _delayIndex++;
            //        if (_delayIndex == _delays.Length)
            //            _delayIndex = 0;
            //        WriteOptionsMenu();
            //    } else if (keyInfo.Key == ConsoleKey.Q)
            //        break;
            //    else if (keyInfo.Key == ConsoleKey.S) {
            //        tokenSource.Cancel();
            //        tokenSource = new CancellationTokenSource();
            //        token = tokenSource.Token;
            //        ClearOutputLines();
            //    }
            //}
        }

        //private const int OPTIONS_MENU_LINE_COUNT = 15;
        //private static void WriteOptionsMenu() {
        //    lock (_writeLock) {
        //        Console.SetCursorPosition(0, 0);
        //        ClearConsoleLines(0, OPTIONS_MENU_LINE_COUNT - 1);
        //        Console.WriteLine("1: Send 1");
        //        Console.WriteLine("2: Send 100");
        //        Console.WriteLine("3: Send continuous");
        //        Console.WriteLine("4: Send session start");
        //        Console.WriteLine("5: Send session start (manual)");
        //        Console.WriteLine("6: Send heart beat");
        //        Console.WriteLine("7: Send session end");
        //        Console.WriteLine("8: Change user identity");
        //        Console.WriteLine("P: Process queue");
        //        Console.WriteLine("F: Process event files directory");
        //        Console.WriteLine("D: Change date range (" + _dateSpans[_dateSpanIndex].ToWords() + ")");
        //        Console.WriteLine("T: Change continuous delay (" + _delays[_delayIndex].ToString("N0") + ")");
        //        Console.WriteLine();
        //        Console.WriteLine("Q: Quit");
        //    }
        //}

        //private static void ClearOutputLines(int delay = 1000) {
        //    Task.Run(() => {
        //        Thread.Sleep(delay);
        //        ClearConsoleLines(OPTIONS_MENU_LINE_COUNT, OPTIONS_MENU_LINE_COUNT + 4);
        //    });
        //}

        //private const int LOG_LINE_COUNT = 10;
        //private static void StartDisplayingLogMessages() {
        //    Task.Factory.StartNew(() => {
        //        while (true) {
        //            var logEntries = _log.GetLogEntries(LOG_LINE_COUNT);
        //            lock (_writeLock) {
        //                ClearConsoleLines(OPTIONS_MENU_LINE_COUNT + 5, OPTIONS_MENU_LINE_COUNT + 6 + LOG_LINE_COUNT);
        //                Console.SetCursorPosition(0, OPTIONS_MENU_LINE_COUNT + 6);
        //                foreach (var logEntry in logEntries) {
        //                    var originalColor = Console.ForegroundColor;
        //                    Console.ForegroundColor = GetColor(logEntry);
        //                    Console.WriteLine(logEntry);
        //                    Console.ForegroundColor = originalColor;
        //                }
        //            }
        //            Thread.Sleep(250);
        //        }
        //    });
        //}

        //private static ConsoleColor GetColor(InMemoryExceptionlessLog.LogEntry logEntry) {
        //    switch (logEntry.Level) {
        //        case LogLevel.Debug:
        //            return ConsoleColor.Gray;
        //        case LogLevel.Error:
        //            return ConsoleColor.Yellow;
        //        case LogLevel.Info:
        //            return ConsoleColor.White;
        //        case LogLevel.Trace:
        //            return ConsoleColor.DarkGray;
        //        case LogLevel.Warn:
        //            return ConsoleColor.Magenta;
        //    }

        //    return ConsoleColor.White;
        //}

        //private static void ClearConsoleLines(int startLine = 0, int endLine = -1) {
        //    if (endLine < 0)
        //        endLine = Console.WindowHeight - 2;

        //    lock (_writeLock) {
        //        int currentLine = Console.CursorTop;
        //        int currentPosition = Console.CursorLeft;

        //        for (int i = startLine; i <= endLine; i++) {
        //            Console.SetCursorPosition(0, i);
        //            Console.Write(new string(' ', Console.WindowWidth));
        //        }
        //        Console.SetCursorPosition(currentPosition, currentLine);
        //    }
        //}
    }
}
