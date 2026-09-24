using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Layeron.App.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layeron.App.Test;

/// <summary>
/// Tests for the one part of the pipeline that is ours: the memory sink feeding the log window.
/// Rendering, templating and file output are Serilog's job and are deliberately not re-tested here -
/// these tests pin what the window actually depends on: category extraction, template-order
/// properties, exception passthrough, buffer ordering, and no loss under concurrent writers.
/// </summary>
[TestClass]
public sealed class LogTests
{
    [TestInitialize]
    public void ResetLog() => Log.Clear();

    [TestMethod]
    public void Sink_CapturesMessagePropertiesAndCategory()
    {
        Log.Info("Test", "Volume {SizeX}x{SizeY} loaded", 128, 64);

        var record = AssertSingle();
        Assert.AreEqual("Volume 128x64 loaded", record.Message);
        Assert.AreEqual("Volume {SizeX}x{SizeY} loaded", record.Template);
        Assert.AreEqual("Test", record.Category);
        CollectionAssert.AreEqual(
            new[] { "SizeX", "SizeY" },
            record.Properties.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "128", "64" },
            record.Properties.Select(p => p.Value).ToArray());
    }

    [TestMethod]
    public void ToString_DoesNotRepeatValuesAlreadyInTheMessage()
    {
        // Both values are visible in the sentence, so repeating them as Bytes=... Path=... would only
        // make the copied line harder to read.
        Log.Debug("Dock", "Layout saved ({Bytes} bytes) to {Path}", 42, "layout.json");

        // Serilog's default rendering quotes string values. What matters here is that values already
        // in the sentence are not repeated as properties on the line, not how the library renders one.
        var text = AssertSingle().ToString();
        StringAssert.Contains(text, "Layout saved (42 bytes) to \"layout.json\"");
        Assert.IsFalse(text.Contains("Bytes=", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Path=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToString_KeepsPropertiesOutOfTheLineButOnTheRecord()
    {
        Log.Debug("Dock", "Layout saved ({Bytes} bytes)", 42);

        var record = AssertSingle();

        // The line stays a sentence; the value is still machine-readable on the record, and the
        // window's detail pane renders it through DetailText.
        Assert.AreEqual("Layout saved (42 bytes)", record.Message);
        Assert.IsFalse(record.ToString().Contains("Bytes=", StringComparison.Ordinal));
        Assert.AreEqual("Bytes", record.Properties[0].Name);
        Assert.AreEqual("42", record.Properties[0].Value);
    }

    [TestMethod]
    public void DetailText_JoinsPropertiesForTheWindow()
    {
        Log.Info("Test", "Sized {W}x{H}", 4, 8);

        StringAssert.Contains(AssertSingle().DetailText, "W=4");
        StringAssert.Contains(AssertSingle().DetailText, "H=8");
    }

    [TestMethod]
    public void Error_CarriesExceptionWithoutFlatteningIt()
    {
        var ex = new InvalidOperationException("boom");
        Log.Error("Test", ex, "Operation failed for {Name}", "x");

        var record = AssertSingle();
        Assert.AreSame(ex, record.Exception);

        // Library default: a string argument renders quoted; the exception stays a separate field.
        Assert.AreEqual("Operation failed for \"x\"", record.Message);
        StringAssert.Contains(record.ToString(), "boom");
    }

    [TestMethod]
    public void Snapshot_IsOldestFirst()
    {
        Log.Info("Test", "one");
        Log.Info("Test", "two");
        Log.Info("Test", "three");

        CollectionAssert.AreEqual(
            new[] { "one", "two", "three" },
            Log.Snapshot().Select(r => r.Message).ToArray());
    }

    [TestMethod]
    public void Sequence_IsStrictlyIncreasing()
    {
        Log.Info("Test", "a");
        Log.Info("Test", "b");

        var records = Log.Snapshot();
        Assert.IsGreaterThan(records[0].Sequence, records[1].Sequence);
    }

    [TestMethod]
    public void Clear_EmptiesTheBuffer()
    {
        Log.Info("Test", "gone");
        Log.Clear();

        Assert.IsEmpty(Log.Snapshot());
    }

    [TestMethod]
    public void RecordAdded_ReceivesEveryRecord()
    {
        var received = new List<LogRecord>();
        void Handler(LogRecord record) => received.Add(record);

        Log.RecordAdded += Handler;
        try
        {
            Log.Info("Test", "watched");
        }
        finally
        {
            Log.RecordAdded -= Handler;
        }

        Assert.HasCount(1, received);
        Assert.AreEqual("watched", received[0].Message);
    }

    [TestMethod]
    public void ConcurrentWriters_ProduceOneRecordEachWithUniqueSequence()
    {
        const int writers = 8;
        const int perWriter = 50;

        Parallel.For(0, writers, writer =>
        {
            for (var i = 0; i < perWriter; i++)
                Log.Info("Test", "w{Writer} i{Index}", writer, i);
        });

        var records = Log.Snapshot();
        Assert.HasCount(writers * perWriter, records);
        Assert.AreEqual(records.Count, records.Select(r => r.Sequence).Distinct().Count());
    }

    private static LogRecord AssertSingle()
    {
        var records = Log.Snapshot();
        Assert.HasCount(1, records, "expected exactly one buffered record");
        return records[0];
    }
}
