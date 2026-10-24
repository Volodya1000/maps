using CableRoutePlanner.UI.Views;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class BottomSheetLayoutTests
{
    [Fact]
    public void NarrowExpandedLayoutUsesCappedHeightAndContentPadding()
    {
        var layout = BottomSheetLayout.Create(600, 1000, true);

        Assert.True(layout.IsNarrow);
        Assert.True(layout.IsExpanded);
        Assert.Equal(560, layout.Height);
        Assert.Equal(10, layout.Padding);
    }

    [Fact]
    public void NarrowCollapsedLayoutKeepsOnlyTheHandleVisible()
    {
        var layout = BottomSheetLayout.Create(600, 800, false);

        Assert.Equal(52, layout.Height);
        Assert.Equal(0, layout.Padding);
        Assert.Contains("Показать", layout.HandleText);
    }

    [Fact]
    public void WideLayoutDoesNotSetAnExplicitHeight()
    {
        var layout = BottomSheetLayout.Create(900, 800, false);

        Assert.False(layout.IsNarrow);
        Assert.Null(layout.Height);
        Assert.Equal(18, layout.Padding);
    }
}
