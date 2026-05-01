using System;

namespace InsanityRevive;

// Immutable per-bot network DNA. Generated once at spawn from the bot's
// fake SteamID64 (so the same identity always produces the same network
// feel — useful when reproducing bug reports). Fields chosen to span the
// realistic MM ping distribution: most bots hover at 25-60ms with the
// occasional outlier on a poor route.
public sealed class NetworkProfile
{
    public int    BaseLatencyMs        { get; init; }
    public int    JitterRangeMs        { get; init; }
    public double SpikeChancePerSec    { get; init; }
    public int    SpikeDurationMinMs   { get; init; }
    public int    SpikeDurationMaxMs   { get; init; }
    public int    SpikePeakMinMs       { get; init; }
    public int    SpikePeakMaxMs       { get; init; }
    public double LossRate             { get; init; }
    public ulong  Seed                 { get; init; }

    public static NetworkProfile Generate(ulong seed)
    {
        // We hash the seed once into a deterministic 64-bit stream and
        // peel ranges off it — no shared RNG, no order coupling.
        unchecked
        {
            ulong h = seed * 0x9E3779B97F4A7C15UL ^ 0xDEADBEEFCAFEBABEUL;
            h = (h ^ (h >> 33)) * 0xFF51AFD7ED558CCDUL;
            h = (h ^ (h >> 33)) * 0xC4CEB9FE1A85EC53UL;
            h ^= (h >> 33);

            int Pick(int lo, int hi, int slot)
            {
                ulong b = (h >> (slot * 8)) & 0xFFFF;
                return lo + (int)(b % (ulong)(hi - lo + 1));
            }

            var baseLat   = Pick(20, 80, 0);
            var jitter    = Pick(5, 15, 1);
            var spikeMin  = Pick(100, 250, 2);
            var spikeMax  = spikeMin + Pick(50, 250, 3);
            var peakMin   = Pick(50, 100, 4);
            var peakMax   = peakMin + Pick(30, 100, 5);
            var lossPct1k = Pick(1, 10, 6); // 0.1% .. 1.0%
            var spikePerMin = 0.5 + ((h >> 56) & 0xFF) / 255.0 * 1.5; // 0.5..2.0/min

            return new NetworkProfile
            {
                Seed                = seed,
                BaseLatencyMs       = baseLat,
                JitterRangeMs       = jitter,
                SpikeChancePerSec   = spikePerMin / 60.0,
                SpikeDurationMinMs  = spikeMin,
                SpikeDurationMaxMs  = spikeMax,
                SpikePeakMinMs      = peakMin,
                SpikePeakMaxMs      = peakMax,
                LossRate            = lossPct1k / 1000.0,
            };
        }
    }
}
