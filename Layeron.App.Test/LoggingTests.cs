using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Layeron.App.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layeron.App.Test;

/// <summary>
/// Tests for the logging core. They exercise the parts that are easy to get subtly wrong and impossible
/// to notice by eye: template arity, name/value extraction, the ring's eviction order, and the fact
/// that concurrent writers do not lose or duplicate records.
/// </summary>
[TestClass]
public sealed class LogTests
{
    [TestInitialize]
    public void ResetLog()
    {
        Log.Clear();
        Log.FileLevel = LogLevel.Debug;
    }

    [TestMethod]
    public void Template_FillsHolesAndKeepsNames()
    {
        Log.Info("Test", "Volume {SizeX}x{SizeY}x{SizeZ} loaded", 128, 64, 32);

        var record = AssertSingle();

        Assert.AreEqual("Volume 128x64x32 loaded", record.Message);
        Assert.AreEqual("Volume {SizeX}x{SizeY}x{SizeZ} loaded", record.Template);
        CollectionAssert.AreEqual(
            new[] { "SizeX", "SizeY", "SizeZ" },
            record.Properties.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "128", "64", "32" },
            record.Properties.Select(p => p.Value).ToArray());
    }

    [TestMethod]
    public void Template_HoleWithFormat_AppliesIt()
    {
        // A fixed-point format on purpose: the invariant-culture percent format ("42 %") differs from
        // what a human would assume, and asserting on it would test the culture rather than the code.
        Log.Info("Test", "Ratio {Ratio:F2}", 0.42);

        Assert.AreEqual("Ratio 0.42", AssertSingle().Message);
    }

    [TestMethod]
    public void Template_EscapedBraces_AreLiteral()
    {
        Log.Info("Test", "json {{ \"a\": 1 }} for {Name}", "x");

        var record = AssertSingle();
        Assert.AreEqual("json { \"a\": 1 } for x", record.Message);
        Assert.AreEqual("Name", record.Properties[0].Name);
    }

    [TestMethod]
    public void Template_MissingArgument_LeavesHoleVisible()
    {
        // The point is that an arity mistake shows up in the output instead of silently dropping a
        // value; a truncated sentence would look like a bug in the component, not in the call.
        Log.Info("Test", "Loaded {Path} with {Count} slices", "vol.b3d");

        var record = AssertSingle();
        Assert.AreEqual("Loaded vol.b3d with {Count} slices", record.Message);
        Assert.HasCount(1, record.Properties);
    }

    [TestMethod]
    public void Template_SurplusArguments_AreAppendedAndKept()
    {
        Log.Info("Test", "Count {N}", 1, "extra");

        var record = AssertSingle();
        Assert.AreEqual("Count 1 [extra]", record.Message);
        CollectionAssert.AreEqual(
            new[] { "N", "arg0" },
            record.Properties.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void Template_SingleHoleOnly_CarriesNoProperties()
    {
        // This is the shape a pre-formatted plugin message takes. Treating it as a structured event
        // would print "| Message=<the whole message again>" on every line.
        Log.Info("Plugin", "{Message}", "Plugin reported something");

        var record = AssertSingle();
        Assert.AreEqual("Plugin reported something", record.Message);
        Assert.IsEmpty(record.Properties);
        Assert.IsFalse(record.HasDetail);
    }

    [TestMethod]
    public void ToString_DoesNotRepeatValuesAlreadyInTheMessage()
    {
        // Both values are visible in the sentence, so repeating them as Bytes=... Path=... would only
        // make the line harder to read.
        Log.Debug("Dock", "Layout saved ({Bytes} bytes) to {Path}", 42, "layout.json");

        var text = AssertSingle().ToString();
        StringAssert.Contains(text, "Layout saved (42 bytes) to layout.json");
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
        Assert.AreEqual("Operation failed for x", record.Message);
        StringAssert.Contains(record.ToString(), "boom");
    }

    [TestMethod]
    public void FileLevel_DoesNotAffectWhatTheWindowCanShow()
    {
        // The file gate is about file noise, not about memory. A developer who raises it to Warn for a
        // quiet file must still be able to browse the session's Debug detail in the log window.
        Log.FileLevel = LogLevel.Warn;
        Log.Debug("Test", "quiet in the file");

        Assert.AreEqual("quiet in the file", AssertSingle().Message);
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
