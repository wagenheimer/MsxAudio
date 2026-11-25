namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Driver RAM observations supplied by a cartridge-specific profile.</summary>
public sealed class DriverObservationOptions
{
    /// <summary>Channel activity bytes. All zero means the requested sound has ended.</summary>
    public List<int> ActiveAddresses { get; set; } = new();
    /// <summary>Complete sequencer state, including pointers, waits, envelopes and repetition stacks.</summary>
    public List<DriverMemoryRange> StateRanges { get; set; } = new();
    /// <summary>Reject a full capture unless the driver stopped or a stable cycle was observed.</summary>
    public bool Required { get; set; }
}

/// <summary>A RAM range used in a driver's deterministic state signature.</summary>
public sealed class DriverMemoryRange
{
    public int Address { get; set; }
    public int Length { get; set; }
}

/// <summary>A repeated driver state, measured on the capture's 44.1 kHz clock.</summary>
public readonly record struct DriverLoop(long StartSample, long DurationSamples);

/// <summary>Result of observing a sound driver after one tick.</summary>
public readonly record struct DriverObservation(bool Finished, DriverLoop? Loop);

/// <summary>
/// Observes profile-declared RAM without knowing a game's code. A loop requires the same complete
/// state three times at equal intervals. Profiles are responsible for declaring all relevant state;
/// matching register values or sequence pointers alone is insufficient.
/// </summary>
public sealed class DriverStateObserver
{
    private readonly int[] _active;
    private readonly int[] _state;
    private readonly Dictionary<string, (long Last, long Period)> _seen = new(StringComparer.Ordinal);
    private long _lastSample = -1;

    public DriverStateObserver(DriverObservationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ActiveAddresses.Any(a => a is < 0 or > 65535) ||
            options.StateRanges.Any(r => r.Address < 0 || r.Length <= 0 || r.Address > 65536 - r.Length) ||
            options.StateRanges.Sum(r => (long)r.Length) > 4096)
            throw new InvalidDataException("Invalid driver observation RAM range.");
        _active = options.ActiveAddresses.ToArray();
        _state = options.StateRanges.SelectMany(r => Enumerable.Range(r.Address, r.Length)).ToArray();
    }

    public DriverObservation Observe(Func<int, byte> read, long sample)
    {
        if (sample <= _lastSample) throw new ArgumentOutOfRangeException(nameof(sample));
        _lastSample = sample;
        if (_active.Length > 0 && _active.All(a => read(a) == 0)) return new(true, null);
        if (_state.Length == 0) return default;
        // Exact bytes, not a hash: collisions cannot create a false cycle.
        string signature = Convert.ToBase64String(_state.Select(read).ToArray());
        if (_seen.TryGetValue(signature, out var previous))
        {
            long period = sample - previous.Last;
            _seen[signature] = (sample, period);
            if (period == previous.Period) return new(false, new(previous.Last - period, period));
        }
        else _seen[signature] = (sample, 0);
        return default;
    }
}
