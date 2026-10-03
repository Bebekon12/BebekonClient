using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class LoggingTests
{
    [Fact]
    public async Task ParallelLoggerInstancesSerializeWritesToOneFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N"));
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            {
                var log = new SafeLog(root, "core");
                for (var line = 0; line < 100; line++) log.Write($"Worker {index}, line {line}.");
            })));
            var lines = File.ReadAllLines(Path.Combine(root, "core.log"));
            Assert.Equal(1600, lines.Length); Assert.Equal(1600, lines.Distinct().Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void ExternalFileLockCannotThrowFromLoggingAndLaterWritesRecover()
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var log = new SafeLog(root, "core");
            using (var locked = File.Open(Path.Combine(root, "core.log"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                Assert.Null(Record.Exception(() => log.Write("Core started.")));
            log.Write("Recovered."); Assert.Contains("Recovered.", File.ReadAllText(Path.Combine(root, "core.log")));
        }
        finally { Directory.Delete(root, true); }
    }
}
