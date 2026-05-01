using System;

namespace InsanityRevive;

// Per-bot network state machine. Driven once per server tick.
// Owns its own RNG (xorshift64*, seeded distinctly from Profile so spike
// timing is independent of profile-shape choices).
public sealed class NetworkSimulator
{
    private const double TickHz = 64.0;
    private const double MsPerTick = 1000.0 / TickHz;

    public NetworkProfile Profile { get; }
    public int CurrentLatencyMs { get; private set; }
    public bool LossThisTick    { get; private set; }
    public bool InSpike         { get; private set; }
    public int  ActiveSpikePeakMs { get; private set; }

    private enum SpikeState { Idle, Spiking, Cooldown }
    private SpikeState _state;
    private int _spikeTicksRemaining;
    private int _cooldownTicksRemaining;
    private int _spikePeakMs;
    private int _spikeDurationMs;

    private ulong _rng;

    // event payload for telemetry; cleared each tick
    public bool SpikeStartedThisTick { get; private set; }
    public bool SpikeEndedThisTick   { get; private set; }

    public NetworkSimulator(NetworkProfile p, ulong rngSeed)
    {
        Profile = p;
        _rng = rngSeed == 0 ? 0xA5A5A5A5A5A5A5A5UL : rngSeed;
        CurrentLatencyMs = p.BaseLatencyMs;
        _state = SpikeState.Idle;
    }

    public int GetCurrentLatencyTicks()
    {
        var ms = CurrentLatencyMs;
        if (ms < 0) ms = 0;
        return (int)Math.Round(ms / MsPerTick);
    }

    public bool ShouldDropThisTick() { return LossThisTick; }

    public void Tick()
    {
        SpikeStartedThisTick = false;
        SpikeEndedThisTick = false;

        // Loss first: independent Bernoulli per tick.
        LossThisTick = NextDouble() < Profile.LossRate;

        // Spike state machine.
        switch (_state)
        {
            case SpikeState.Idle:
                {
                    var perTick = Profile.SpikeChancePerSec / TickHz;
                    if (NextDouble() < perTick)
                    {
                        _spikeDurationMs = NextInt(Profile.SpikeDurationMinMs, Profile.SpikeDurationMaxMs);
                        _spikePeakMs     = NextInt(Profile.SpikePeakMinMs,     Profile.SpikePeakMaxMs);
                        _spikeTicksRemaining = (int)Math.Round(_spikeDurationMs / MsPerTick);
                        if (_spikeTicksRemaining < 1) _spikeTicksRemaining = 1;
                        _state = SpikeState.Spiking;
                        InSpike = true;
                        ActiveSpikePeakMs = _spikePeakMs;
                        SpikeStartedThisTick = true;
                    }
                    break;
                }

            case SpikeState.Spiking:
                {
                    _spikeTicksRemaining--;
                    if (_spikeTicksRemaining <= 0)
                    {
                        _state = SpikeState.Cooldown;
                        _cooldownTicksRemaining = (int)Math.Round(_spikeDurationMs / MsPerTick);
                        InSpike = false;
                        ActiveSpikePeakMs = 0;
                        SpikeEndedThisTick = true;
                    }
                    break;
                }

            case SpikeState.Cooldown:
                {
                    _cooldownTicksRemaining--;
                    if (_cooldownTicksRemaining <= 0) _state = SpikeState.Idle;
                    break;
                }
        }

        // Compose current latency: base + jitter +/- + active-spike contribution
        var jitter = NextInt(-Profile.JitterRangeMs, Profile.JitterRangeMs);
        var lat = Profile.BaseLatencyMs + jitter + (InSpike ? _spikePeakMs : 0);
        if (lat < 1) lat = 1;
        if (lat > 999) lat = 999;
        CurrentLatencyMs = lat;
    }

    public int LastSpikeDurationMs => _spikeDurationMs;
    public int LastSpikePeakMs     => _spikePeakMs;

    private ulong NextU64()
    {
        // xorshift64*
        ulong x = _rng;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        _rng = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    private double NextDouble() { return (NextU64() >> 11) * (1.0 / (1UL << 53)); }

    private int NextInt(int lo, int hi)
    {
        if (hi <= lo) return lo;
        var range = (uint)(hi - lo + 1);
        return lo + (int)(NextU64() % range);
    }
}
