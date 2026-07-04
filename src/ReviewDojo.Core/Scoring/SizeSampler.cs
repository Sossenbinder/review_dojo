namespace ReviewDojo.Core.Scoring;

public class SizeSampler
{
    private readonly Random _rng;
    public SizeSampler(int seed) => _rng = new Random(seed);

    static double Median(DifficultyTier t) => t switch
    { DifficultyTier.Easy => 80, DifficultyTier.Medium => 150, DifficultyTier.Hard => 300, _ => 150 };

    public int Sample(DifficultyTier tier)
    {
        // Standard normal via Box-Muller, then exp(mu + sigma*z).
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        double mu = Math.Log(Median(tier));
        double val = Math.Exp(mu + 0.6 * z);
        return Math.Clamp((int)Math.Round(val), 10, 800);
    }
}
