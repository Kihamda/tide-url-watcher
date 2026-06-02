using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Tide.App;

public sealed class StableButton : Button
{
    public StableButton()
    {
        PointerEntered += (_, _) => Opacity = 0.82;
        PointerExited += (_, _) => ResetVisualState();
        PointerCanceled += (_, _) => ResetVisualState();
        PointerCaptureLost += (_, _) => ResetVisualState();
    }

    private void ResetVisualState()
    {
        Opacity = 1;
        VisualStateManager.GoToState(this, "Normal", true);
    }
}
