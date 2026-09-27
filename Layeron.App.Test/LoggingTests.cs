using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Layeron.App.Logging;
using Layeron.App.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog.Events;
namespace Layeron.App.Test;
/// <summary>
/// Tests for the two parts of the pipeline that are ours: the memory sink, which hands the window the
/// events Serilog built, and the rendering the window does with them. Serilog's own templating and
/// file output are its job and are deliberately not re-tested here - these pin what the window
/// actually depends on: that nothing is lost or pre-rendered on the way in, and that what the window
/// derives from an event is what the columns, the filter and the copy command show.
/// </summary>
[TestClass]
public sealed class LogTests
{
    [TestInitialize]
    public void ResetLog() => Log.Clear();
    [TestMethod]
    public void Sink_KeepsTheEventItselfAndRendersNothing()
    {
        Log.Info("Test", "Volume {SizeX}x{SizeY} loaded", 128, 64);
        var logEvent = AssertSingle();
        // The event is the pipeline's own object, handed over untouched: the message template and its
        // values are still structured, not flattened into a sentence by the sink.
        Assert.AreEqual("Volume {SizeX}x{SizeY} loaded", logEvent.MessageTemplate.Text);
        Assert.AreEqual(LogEventLevel.Information, logEvent.Level);
        CollectionAssert.AreEqual(
            new[] { "SizeX", "SizeY" },
            logEvent.Properties.Keys.Where(k => k != "SourceContext").ToArray());
    }
    [TestMethod]
    public void Views_RenderMessageCategoryAndValuesFromTheEvent()
    {
        Log.Info("Test", "Volume {SizeX}x{SizeY} loaded", 128, 64);
        var logEvent = AssertSingle();
        Assert.AreEqual("Volume 128x64 loaded", logEvent.Message());
        Assert.AreEqual("Test", logEvent.Category());
        CollectionAssert.AreEqual(
            new[] { "SizeX", "SizeY" },
            logEvent.Values().Select(v => v.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "128", "64" },
            logEvent.Values().Select(v => v.Value).ToArray());
    }
    [TestMethod]
    public void Category_IsNotRepeatedAmongTheValues()
    {
        Log.Info("Dock", "Layout saved");
        var logEvent = AssertSingle();
        // The category has a column of its own, so the detail pane must not show it a second time.
        Assert.AreEqual("Dock", logEvent.Category());
        Assert.IsEmpty(logEvent.Values());
        Assert.IsFalse(logEvent.HasDetail());
    }
    [TestMethod]
    public void Writers_CoverEverySerilogLevel()
    {
        Log.Verbose("Test", "one");
        Log.Debug("Test", "two");
        Log.Info("Test", "three");
        Log.Warn("Test", "four");
        Log.Error("Test", "five");
        Log.Fatal("Test", "six");
        CollectionAssert.AreEqual(
            new[]
            {
                LogEventLevel.Verbose,
                LogEventLevel.Debug,
                LogEventLevel.Information,
                LogEventLevel.Warning,
                LogEventLevel.Error,
                LogEventLevel.Fatal
            },
            Log.Snapshot().Select(e => e.Level).ToArray());
    }
    [TestMethod]
    public void ErrorAndFatal_CarryAnExceptionOnTheirOwnOverload()
    {
        var failed = new InvalidOperationException("failed");
        var lost = new OutOfMemoryException("lost");
        Log.Error("Test", failed, "while {Doing}", "loading");
        Log.Fatal("Test", lost, "gave up");
        var events = Log.Snapshot();
        Assert.AreSame(failed, events[0].Exception);
        Assert.AreSame(lost, events[1].Exception);
        Assert.AreEqual(LogEventLevel.Fatal, events[1].Level);
    }
    [TestMethod]
    public void ShortName_IsShortAndCoversEveryLevel()
    {
        // One name per level, in the library's own order: the column, the picker and the counts all
        // read through this function, so a missing case would show up as a wrong label.
        CollectionAssert.AreEqual(
            new[] { "Trace", "Debug", "Info", "Warn", "Error", "Fatal" },
            LogEventView.AllLevels.Select(LogEventView.ShortName).ToArray());
        Assert.AreEqual("Trace", LogEventView.ShortName(LogEventLevel.Verbose));
        Assert.AreEqual("Info", LogEventView.ShortName(LogEventLevel.Information));
        Assert.AreEqual("Warn", LogEventView.ShortName(LogEventLevel.Warning));
    }
    [TestMethod]
    public void Line_DoesNotRepeatValuesAlreadyInTheMessage()
    {
        // Both values are visible in the sentence, so repeating them as Bytes=... Path=... would only
        // make the copied line harder to read.
        Log.Debug("Dock", "Layout saved ({Bytes} bytes) to {Path}", 42, "layout.json");
        // Serilog's default rendering quotes string values. What matters here is that values already
        // in the sentence are not repeated as properties on the line, not how the library renders one.
        var text = AssertSingle().Line();
        StringAssert.Contains(text, "Layout saved (42 bytes) to \"layout.json\"");
        Assert.IsFalse(text.Contains("Bytes=", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Path=", StringComparison.Ordinal));
        // The line leads with its timestamp and carries the short level and the category.
        StringAssert.Contains(text, " Debug Dock: Layout saved (42 bytes)");
    }
    [TestMethod]
    public void DetailText_JoinsValuesForTheWindow()
    {
        Log.Info("Test", "Sized {W}x{H}", 4, 8);
        var text = AssertSingle().DetailText();
        StringAssert.Contains(text, "W=4");
        StringAssert.Contains(text, "H=8");
    }
    [TestMethod]
    public void Template_IsShownOnlyWhenItCarriesHoles()
    {
        Log.Info("Test", "Sized {W}x{H}", 4, 8);
        Log.Info("Test", "plain");
        var events = Log.Snapshot();
        // The first has holes, so the template says something the sentence does not; the second would
        // only repeat itself.
        Assert.IsTrue(events[0].HasTemplate());
        Assert.IsFalse(events[1].HasTemplate());
    }
    [TestMethod]
    public void Error_CarriesExceptionWithoutFlatteningIt()
    {
        var ex = new InvalidOperationException("boom");
        Log.Error("Test", ex, "Operation failed for {Name}", "x");
        var logEvent = AssertSingle();
        Assert.AreSame(ex, logEvent.Exception);
        // Library default: a string argument renders quoted; the exception stays a separate field.
        Assert.AreEqual("Operation failed for \"x\"", logEvent.Message());
        StringAssert.Contains(logEvent.Line(), "boom");
    }
    [TestMethod]
    public void Snapshot_IsOldestFirst()
    {
        Log.Info("Test", "one");
        Log.Info("Test", "two");
        Log.Info("Test", "three");
        CollectionAssert.AreEqual(
            new[] { "one", "two", "three" },
            Log.Snapshot().Select(e => e.Message()).ToArray());
    }
    [TestMethod]
    public void Clear_EmptiesTheBuffer()
    {
        Log.Info("Test", "gone");
        Log.Clear();
        Assert.IsEmpty(Log.Snapshot());
    }
    [TestMethod]
    public void RecordAdded_ReceivesEveryEvent()
    {
        var received = new List<LogEvent>();
        void Handler(LogEvent logEvent) => received.Add(logEvent);
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
        Assert.AreEqual("watched", received[0].Message());
        // The subscriber gets the very instance the ring holds, which is what lets the window keep a
        // selection across a filter rebuild by reference.
        Assert.AreSame(received[0], AssertSingle());
    }
    [TestMethod]
    public void ConcurrentWriters_ProduceOneEventEach()
    {
        const int writers = 8;
        const int perWriter = 50;
        Parallel.For(0, writers, writer =>
        {
            for (var i = 0; i < perWriter; i++)
                Log.Info("Test", "w{Writer} i{Index}", writer, i);
        });
        var events = Log.Snapshot();
        Assert.HasCount(writers * perWriter, events);
        // Every event is distinct: the whole pipeline keeps no identity of its own, so two rows being
        // the same object would mean one was dropped or handed out twice.
        Assert.AreEqual(events.Count, events.Distinct().Count());
    }
    private static LogEvent AssertSingle()
    {
        var events = Log.Snapshot();
        Assert.HasCount(1, events, "expected exactly one buffered event");
        return events[0];
    }
}