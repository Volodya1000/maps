namespace CableRoutePlanner.UI.Views;

public readonly record struct BottomSheetLayout(
    bool IsNarrow,
    bool IsExpanded,
    double? Height,
    double Padding,
    string HandleText)
{
    private const double MobileBreakpoint = 760;

    public static BottomSheetLayout Create(double width, double height, bool expanded) =>
        width < MobileBreakpoint
            ? new(
                IsNarrow: true,
                IsExpanded: expanded,
                Height: expanded ? Math.Min(height * 0.68, 560) : 52,
                Padding: expanded ? 10 : 0,
                HandleText: expanded
                    ? "Точки и маршруты · потяните вниз, чтобы свернуть"
                    : "↑ Показать точки и маршруты")
            : new(
                IsNarrow: false,
                IsExpanded: expanded,
                Height: null,
                Padding: 18,
                HandleText: "Точки и маршруты · потяните вниз, чтобы свернуть");
}
