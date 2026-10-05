namespace Layeron.Plugins.ScriptHost;

/// <summary>
/// Identity of an open script editor, used by <see cref="ScriptEditorController"/> to match a closing tab
/// to the document registration it made.
/// <para>
/// It deliberately declares no operations: every command that acts on a script (save, save as, run, run
/// once) lives on the editor's own toolbar, so nothing outside an editor ever needs to call into one.
/// The interface exists rather than storing <c>object</c> for a loading reason - the controller is
/// constructed during activation in every host, and a field typed with the UI class would make the runtime
/// resolve the Avalonia stack at that moment. Naming this marker keeps the UI type reachable solely from
/// the factory that creates it, by which time a UI host is guaranteed.
/// </para>
/// </summary>
public interface IScriptEditor : IDisposable;
