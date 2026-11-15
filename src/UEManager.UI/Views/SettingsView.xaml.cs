using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace UEManager.UI.Views;

public partial class SettingsView : UserControl
{
    private bool isDraggingFontSize;
    private bool isFontSizePointerDown;

    public SettingsView() => InitializeComponent();

    private void OnFontSizeDragStarted(object sender, DragStartedEventArgs e) => isDraggingFontSize = true;

    private void OnFontSizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        isDraggingFontSize = false;
        isFontSizePointerDown = false;
        CommitFontSize();
    }

    private void OnFontSizePointerDown(object sender, MouseButtonEventArgs e) => isFontSizePointerDown = true;

    private void OnFontSizePointerUp(object sender, MouseButtonEventArgs e)
    {
        isFontSizePointerDown = false;
        if (!isDraggingFontSize)
            CommitFontSize();
    }

    private void OnFontSizeLostMouseCapture(object sender, MouseEventArgs e)
    {
        if ((!isDraggingFontSize && !isFontSizePointerDown) || FontSizeSlider.IsMouseCaptureWithin)
            return;

        isDraggingFontSize = false;
        isFontSizePointerDown = false;
        CommitFontSize();
    }

    private void OnFontSizeMouseLeave(object sender, MouseEventArgs e) => ClearStalePointerDown();

    private void OnFontSizePreviewKeyDown(object sender, KeyEventArgs e) => ClearStalePointerDown();

    private void OnFontSizeLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ClearStalePointerDown();

    private void ClearStalePointerDown()
    {
        if (isFontSizePointerDown && !FontSizeSlider.IsMouseCaptureWithin && Mouse.LeftButton == MouseButtonState.Released)
            isFontSizePointerDown = false;
    }

    private void OnFontSizeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isDraggingFontSize || isFontSizePointerDown)
            return;

        // Ignore binding-initialization coercion when Minimum rises above the initial default 0 before Value binds.
        if (FontSizeSlider.Minimum >= FontSizeSlider.Maximum || e.OldValue < FontSizeSlider.Minimum)
            return;

        CommitFontSize();
    }

    /// <summary>Pushes the slider's chosen font size to the view model so UI scaling and persistence run once.</summary>
    private void CommitFontSize()
    {
        if (FontSizeSlider.Minimum >= FontSizeSlider.Maximum)
            return;

        FontSizeSlider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
    }
}
