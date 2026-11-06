namespace CableRoutePlanner.ViewModels;

public sealed record MapViewportState(
    double CenterX,
    double CenterY,
    double Resolution,
    double Rotation)
{
    public bool IsValid => double.IsFinite(CenterX)
        && double.IsFinite(CenterY)
        && double.IsFinite(Resolution)
        && Resolution > 0
        && double.IsFinite(Rotation);

    public static bool TryCreate(
        double centerX,
        double centerY,
        double resolution,
        double rotation,
        out MapViewportState? state)
    {
        double normalizedRotation = rotation % 360;
        if (normalizedRotation < 0)
        {
            normalizedRotation += 360;
        }

        MapViewportState candidate = new(centerX, centerY, resolution, normalizedRotation);
        if (!candidate.IsValid)
        {
            state = null;
            return false;
        }

        state = candidate;
        return true;
    }
}
