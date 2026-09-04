using System.Diagnostics;

namespace Pulse.Services;

/// <summary>
/// Reads how much memory each graphics adapter is using, from Windows rather than from the
/// sensor library.
/// </summary>
/// <remarks>
/// These are the figures Task Manager shows under "Dedicated GPU memory" and "Shared GPU
/// memory", published per adapter and keyed by its locally unique identifier. Windows reports
/// them for every vendor, which is the point: the sensor library does not.
///
/// Two things forced this. Integrated graphics have essentially no dedicated memory, so the
/// dedicated figure is zero and the real usage sits in the shared pool, which the library
/// exposes under a name Pulse deliberately rejects. And the library does not reliably enumerate
/// an integrated GPU at all: on the machine this was written against it appeared on one run and
/// not on the two before it, with no pattern found. A reading that vanishes intermittently is
/// worse than one that comes from somewhere dull and dependable.
///
/// Measured at 0.09ms per poll with the counter objects kept, against 42ms for the same data
/// through WMI. At a half second polling interval that is the difference between free and not,
/// so the objects are cached and only rebuilt when the adapters change.
/// </remarks>
internal sealed class GpuMemoryCounters : IDisposable
{
    private const string Category = "GPU Adapter Memory";

    /// <summary>Bytes in use, as Windows reports them. Negative means "not known".</summary>
    public readonly record struct Usage(float DedicatedBytes, float SharedBytes)
    {
        public bool IsKnown => DedicatedBytes >= 0 || SharedBytes >= 0;
    }

    private readonly Dictionary<string, PerformanceCounter> _dedicated = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounter> _shared    = new(StringComparer.OrdinalIgnoreCase);

    /// The instance names Windows currently publishes, remembered so an adapter that has none
    /// can be answered without constructing a counter that would only throw.
    private string[] _instances = Array.Empty<string>();
    private bool _loaded;

    /// <summary>Set when the category could not be read at all, so callers can fall back.</summary>
    public bool Available { get; private set; }

    /// <summary>
    /// Forgets everything and reads the instance list again.
    /// </summary>
    /// <remarks>
    /// Called when the adapters change, and that is not merely tidiness: the identifiers these
    /// counters are keyed by are not stable. The same two adapters were observed under different
    /// identifiers an hour apart on one machine, so a cached mapping goes stale on its own and
    /// would quietly start reporting one adapter's memory against another's name.
    /// </remarks>
    public void Refresh()
    {
        Dispose();
        _loaded = false;
        Load();
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var category = new PerformanceCounterCategory(Category);
            _instances = category.GetInstanceNames();
            Available  = true;
        }
        catch (Exception ex)
        {
            // Absent on Windows builds older than these counters, and on a machine whose
            // performance counter registry has been damaged, which is common enough to have
            // its own repair command. Not an error: it means fall back to the sensor library.
            _instances = Array.Empty<string>();
            Available  = false;
            LogService.Info(nameof(GpuMemoryCounters),
                $"Windows GPU memory counters are unavailable ({ex.GetType().Name}); "
                + "video memory will be read from the sensor library instead.");
        }
    }

    /// <summary>
    /// Memory in use by the adapter with this counter key, or null when Windows has nothing
    /// for it.
    /// </summary>
    /// <remarks>
    /// Not every adapter has an instance. A duplicate entry for the same physical card, and
    /// some virtual display adapters, appear in the adapter list with no counters at all, so a
    /// missing instance is ordinary rather than a fault.
    /// </remarks>
    public Usage? Read(string counterKey)
    {
        if (string.IsNullOrEmpty(counterKey)) return null;

        Load();
        if (!Available) return null;

        // Matched without regard to case: Windows publishes the hexadecimal in upper case and
        // the identifier it is built from is naturally written in lower.
        string? instance = null;
        foreach (var name in _instances)
            if (string.Equals(name, counterKey, StringComparison.OrdinalIgnoreCase)) { instance = name; break; }

        if (instance == null) return null;

        float dedicated = ReadOne(_dedicated, instance, "Dedicated Usage");
        float shared    = ReadOne(_shared,    instance, "Shared Usage");

        var usage = new Usage(dedicated, shared);
        return usage.IsKnown ? usage : null;
    }

    private static float ReadOne(Dictionary<string, PerformanceCounter> cache, string instance, string counter)
    {
        try
        {
            if (!cache.TryGetValue(instance, out var reader))
            {
                reader = new PerformanceCounter(Category, counter, instance, readOnly: true);
                cache[instance] = reader;
            }

            // A raw byte count, so one sample is the answer. Rate counters would need two.
            return reader.NextValue();
        }
        catch
        {
            // The adapter went away between listing the instances and reading one, which on a
            // laptop that powers its discrete card down is routine. Dropped rather than logged:
            // the next poll rebuilds it, and the caller shows nothing meanwhile.
            cache.Remove(instance);
            return -1;
        }
    }

    public void Dispose()
    {
        foreach (var counter in _dedicated.Values) { try { counter.Dispose(); } catch { } }
        foreach (var counter in _shared.Values)    { try { counter.Dispose(); } catch { } }

        _dedicated.Clear();
        _shared.Clear();
    }
}
