using Avalonia.Media;

namespace Ogs.Telemetry.Monitor;

/// <summary>
/// One geometry kept between draws, rebuilt only when the numbers it was built
/// from change. A <see cref="StreamGeometry"/> wraps native Skia paths that are
/// freed only when the garbage collector finalizes them, so building one per
/// frame (~30 a second per view) keeps the finalizer thread busy; a dial's
/// fixed shapes depend on its size alone and need building once.
/// UI thread only.
/// </summary>
public sealed class GeometrySlot<TKey> where TKey : struct, IEquatable<TKey>
{
    TKey _key;
    Geometry? _geometry;

    public Geometry Get(TKey key, Func<TKey, Geometry> build)
    {
        if (_geometry is null || !_key.Equals(key))
        {
            _geometry = build(key);
            _key = key;
        }
        return _geometry;
    }
}
