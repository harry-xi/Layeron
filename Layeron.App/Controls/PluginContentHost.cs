using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;

namespace Layeron.App.Controls;

/// <summary>
/// Hosts the control a plugin produced for a tool panel or document tab.
/// <para>
/// A plugin supplies a visual, not a view model, so one view model owns exactly one control - and
/// Avalonia allows a control only one visual parent. Dock renders a dockable again in a new view when
/// the user drags its tab out into a floating window (or docks it back), and it keeps the previous
/// view alive until its deferred content presenter has swapped. Both views then try to parent the
/// same control and the second one fails during layout with
/// "The control ... already has a visual parent ...".
/// </para>
/// <para>
/// This host therefore takes ownership of the control when it is given one: if the control is still
/// attached to the view it is moving away from, it is detached from there first. The old view is a
/// transient duplicate of the same dockable and is discarded once Dock swaps its presenter, so
/// emptying it costs nothing; the alternative - building a second control - would mean a second copy
/// of the plugin's GPU resources and break its disposal contract.
/// </para>
/// </summary>
public sealed class PluginContentHost : ContentControl
{
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        // Before base: the base ContentControl adds the new content to its own logical children,
        // which reparents it here and hides which view previously owned it.
        if (change.Property == ContentControl.ContentProperty)
            DetachFromPreviousHost(change.NewValue as Control);

        base.OnPropertyChanged(change);
    }

    private static void DetachFromPreviousHost(Control? control)
    {
        if (control is null)
            return;

        var owner = control.Parent ?? control.GetVisualParent();

        switch (owner)
        {
            case ContentPresenter presenter when presenter.Child == control:
                // SetCurrentValue keeps the old view's binding in place but overrides its value, so
                // it cannot race this host by attaching the control again.
                presenter.SetCurrentValue(ContentPresenter.ContentProperty, null);
                presenter.UpdateChild();
                break;

            case ContentControl contentControl when contentControl.Content == control:
                contentControl.SetCurrentValue(ContentControl.ContentProperty, null);
                break;

            case Decorator decorator when decorator.Child == control:
                decorator.Child = null;
                break;

            case Panel panel:
                panel.Children.Remove(control);
                break;
        }
    }
}
