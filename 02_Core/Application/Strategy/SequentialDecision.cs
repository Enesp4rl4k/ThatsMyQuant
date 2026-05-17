using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// 2026-05-07 (Sprint 2 — Master Plan v3) — sequential alternatives to N=50
/// frequentist stopping + Bonferroni m=3 path-dependent family-wise control.
///
/// Premortem #6 (Pre-Registration Trap) identified that Bonferroni's family-wise
/// α-control is path-dependent — extending the family (adding a new symbol or
/// hypothesis mid-stream) invalidates accumulated samples. Combined with N=50
/// stopping, retail-cadence (4h) strategies in compressed regimes never
/// accumulate enough trades to test inside a single regime window. Result:
/// 6 months, 0 approved strategies, 0 deployed edges, 0 PnL.
///
/// This module ships TWO complementary path-independent decision rules that
/// the new <see cref="FormalPromotionGate.EvaluateSequentialPromotion"/> path
/// uses to decide promote / continue / kill at every observation, without
/// path dependence. Operator can extend the universe mid-stream because each
/// hypothesis is tested in isolation against its own SPRT thresholds.
///
/// Tools provided:
/// 1. <see cref="SprtSharpeTest"/> — Wald's Sequential Probability Ratio Test
///    under H0: per-trade Sharpe ≤ <c>NullSharpe</c>, H1: ≥ <c>TargetSharpe</c>.
///    Walds' bounds A = log((1-β)/α), B = log(β/(1-α)) bound type-I/II error
///    rates exactly without needing a pre-specified N.
/// 2. <see cref="BayesianSharpeEstimator"/> — Conjugate Normal-Normal posterior
///    on per-trade return mean. Maintains posterior probability that Sharpe
///    exceeds a threshold; feeds the gate's "P(Sharpe > 0.5) ≥ 0.7" criterion.
///
/// Both maintain online state — call <c>Update(r)</c> per closed trade. State
/// can be persisted between worker restarts (operator just records the running
/// statistics in the strategy's hybrid state file).
/// </summary>
public enum SprtVerdict
{
    /// <summary>Insufficient evidence either way. Continue accumulating trades.</summary>
    Continue,

    /// <summary>Likelihood ratio crossed lower bound — accept H0 (no edge). Kill the strategy.</summary>
    AcceptNoEdge_Kill,

    /// <summary>Likelihood ratio crossed upper bound — accept H1 (has edge). Promote.</summary>
    AcceptHasEdge_Promote
}

/// <summary>
/// Wald's SPRT testing whether per-trade returns come from the H0 distribution
/// (no edge, Sharpe ≤ <c>NullSharpe</c>) or H1 (real edge, Sharpe ≥ <c>TargetSharpe</c>).
///
/// <para>
/// Assumes per-trade returns are iid normal with known volatility (estimated
/// from running observations after a small warmup, with a robust floor). For
/// crypto perpetual carry strategies the per-trade-return volatility is
/// typically 0.3-1.0% — we estimate adaptively.
/// </para>
///
/// <para>
/// Operating characteristics: type-I error α = 0.05 (false promotion), type-II
/// error β = 0.20 (missed real edge). Expected sample size at the boundary is
/// roughly half of an equivalent fixed-N test, with the bonus of being able to
/// stop early on extreme evidence in either direction.
/// </para>
///
/// <para>
/// Path-independent: extending the strategy universe mid-stream does not
/// invalidate the per-strategy statistic. Each strategy's SPRT is its own
/// trial, isolated.
/// </para>
/// </summary>
public sealed class SprtSharpeTest
{
    private readonly double _nullSharpe;       // H0 Sharpe (per-trade, NOT annualized)
    private readonly double _targetSharpe;     // H1 Sharpe
    private readonly double _logA;             // upper boundary (accept H1)
    private readonly double _logB;             // lower boundary (accept H0)
    private readonly double _stdFloor;         // minimum vol estimate to avoid div/0 / inflated LR

    private double _logLikelihoodRatio;
    private double _sumReturns;
    private double _sumSquaredReturns;
    private int _n;

    /// <param name="nullSharpe">H0 per-trade Sharpe. Default 0 = "no edge".</param>
    /// <param name="targetSharpe">H1 per-trade Sharpe. Default 0.15 ≈ Sharpe ~1.0 annualized for daily-cadence; for 4h cadence ≈ 0.5 annualized.</param>
    /// <param name="alpha">Type-I error (false promote). Default 0.05.</param>
    /// <param name="beta">Type-II error (missed edge). Default 0.20.</param>
    /// <param name="stdFloor">Minimum sigma to avoid pathological LR explosion when first 2-3 trades happen to be near-zero. Default 0.005 (0.5% per-trade).</param>
    public SprtSharpeTest(
        double nullSharpe = 0.0,
        double targetSharpe = 0.15,
        double alpha = 0.05,
        double beta = 0.20,
        double stdFloor = 0.005)
    {
        if (targetSharpe <= nullSharpe) throw new ArgumentException("targetSharpe must exceed nullSharpe");
        if (alpha <= 0 || alpha >= 1) throw new ArgumentException("alpha in (0,1)");
        if (beta <= 0 || beta >= 1) throw new ArgumentException("beta in (0,1)");
        _nullSharpe = nullSharpe;
        _targetSharpe = targetSharpe;
        _logA = Math.Log((1 - beta) / alpha);
        _logB = Math.Log(beta / (1 - alpha));
        _stdFloor = stdFloor;
    }

    /// <summary>Per-trade return (e.g. 0.005 = +0.5% on the trade). Returns the verdict AFTER incorporating this observation.</summary>
    public SprtVerdict Update(double tradeReturn)
    {
        _n++;
        _sumReturns += tradeReturn;
        _sumSquaredReturns += tradeReturn * tradeReturn;

        // Estimate sigma from sample after a small warmup, with floor for stability.
        double sigma;
        if (_n < 3)
        {
            sigma = _stdFloor;
        }
        else
        {
            var mean = _sumReturns / _n;
            var variance = (_sumSquaredReturns / _n) - (mean * mean);
            sigma = Math.Max(_stdFloor, Math.Sqrt(Math.Max(0.0, variance)));
        }

        // Convert Sharpe (per-trade) to expected return under H0/H1 using the SAME sigma.
        // Sharpe_pt = E[r] / sigma  ⇒  E[r] = Sharpe_pt * sigma.
        var muH0 = _nullSharpe * sigma;
        var muH1 = _targetSharpe * sigma;

        // Log-likelihood ratio increment under N(μ, σ²):
        //   log f1(x) - log f0(x) = (μ1-μ0)(x - (μ0+μ1)/2) / σ²
        var logLrIncrement = (muH1 - muH0) * (tradeReturn - (muH0 + muH1) / 2.0) / (sigma * sigma);
        _logLikelihoodRatio += logLrIncrement;

        if (_logLikelihoodRatio >= _logA) return SprtVerdict.AcceptHasEdge_Promote;
        if (_logLikelihoodRatio <= _logB) return SprtVerdict.AcceptNoEdge_Kill;
        return SprtVerdict.Continue;
    }

    public int Observations => _n;
    public double LogLikelihoodRatio => _logLikelihoodRatio;
    public double UpperBoundLogA => _logA;
    public double LowerBoundLogB => _logB;
    public double SampleMean => _n == 0 ? 0.0 : _sumReturns / _n;
    public double SampleSharpe
    {
        get
        {
            if (_n < 2) return 0.0;
            var mean = _sumReturns / _n;
            var variance = (_sumSquaredReturns / _n) - (mean * mean);
            var sigma = Math.Sqrt(Math.Max(1e-12, variance));
            return mean / sigma;
        }
    }
}

/// <summary>
/// Bayesian sequential update on the unknown per-trade-return mean μ under a
/// conjugate normal prior. Reports posterior probability that Sharpe exceeds
/// a threshold at any point during evaluation — feeds the gate's "P(Sharpe ≥
/// 0.5) ≥ 0.7" decision criterion.
///
/// <para>
/// Conjugate prior: μ ~ N(<paramref name="priorMean"/>, <paramref name="priorVariance"/>).
/// Per-trade likelihood: r_i ~ N(μ, σ²) where σ is estimated from sample
/// (frequentist plug-in; full hierarchical NIG is overkill for our N).
/// Posterior after n observations: μ | data ~ N(μ_n, σ_n²) with closed-form
/// updates. P(Sharpe ≥ s) = P(μ ≥ s·σ) is then a normal CDF lookup.
/// </para>
///
/// <para>
/// Default prior mean 0 with variance 0.25 (i.e. SD 0.5) is mildly skeptical
/// — a priori 95% CI on per-trade Sharpe is roughly [-1, +1], which is wide
/// enough to be Bayesian-honest without being uninformative. As N grows the
/// data dominates and the prior is forgotten in 30-50 observations.
/// </para>
/// </summary>
public sealed class BayesianSharpeEstimator
{
    private readonly double _priorMean;
    private readonly double _priorVariance;
    private readonly double _stdFloor;

    private double _sumReturns;
    private double _sumSquaredReturns;
    private int _n;

    public BayesianSharpeEstimator(
        double priorMean = 0.0,
        double priorVariance = 0.25,  // SD 0.5
        double stdFloor = 0.005)
    {
        if (priorVariance <= 0) throw new ArgumentException("priorVariance > 0");
        _priorMean = priorMean;
        _priorVariance = priorVariance;
        _stdFloor = stdFloor;
    }

    public void Update(double tradeReturn)
    {
        _n++;
        _sumReturns += tradeReturn;
        _sumSquaredReturns += tradeReturn * tradeReturn;
    }

    /// <summary>Estimated per-trade volatility (sample SD with floor).</summary>
    public double EstimatedSigma()
    {
        if (_n < 2) return _stdFloor;
        var mean = _sumReturns / _n;
        var variance = (_sumSquaredReturns / _n) - (mean * mean);
        return Math.Max(_stdFloor, Math.Sqrt(Math.Max(0.0, variance)));
    }

    /// <summary>Posterior mean of μ (per-trade return).</summary>
    public double PosteriorMean()
    {
        if (_n == 0) return _priorMean;
        var sigma = EstimatedSigma();
        var sigma2 = sigma * sigma;
        // Normal-normal conjugate: posterior precision = prior precision + n / σ²
        var posteriorPrecision = (1.0 / _priorVariance) + (_n / sigma2);
        var posteriorMean = ((_priorMean / _priorVariance) + (_sumReturns / sigma2)) / posteriorPrecision;
        return posteriorMean;
    }

    /// <summary>Posterior variance of μ.</summary>
    public double PosteriorVariance()
    {
        if (_n == 0) return _priorVariance;
        var sigma = EstimatedSigma();
        var sigma2 = sigma * sigma;
        var posteriorPrecision = (1.0 / _priorVariance) + (_n / sigma2);
        return 1.0 / posteriorPrecision;
    }

    /// <summary>
    /// Posterior probability that per-trade Sharpe exceeds <paramref name="sharpeThreshold"/>.
    /// Sharpe = μ / σ; with σ estimated, P(Sharpe ≥ s) = P(μ ≥ s·σ) under posterior on μ.
    /// </summary>
    public double PosteriorProbSharpeAtLeast(double sharpeThreshold)
    {
        var sigma = EstimatedSigma();
        var threshold = sharpeThreshold * sigma;
        var postMean = PosteriorMean();
        var postSd = Math.Sqrt(PosteriorVariance());
        if (postSd <= 0) return postMean >= threshold ? 1.0 : 0.0;
        // P(μ ≥ threshold) = 1 - Φ((threshold - postMean) / postSd)
        var z = (threshold - postMean) / postSd;
        return 1.0 - NormalCdf(z);
    }

    public int Observations => _n;

    /// <summary>Standard normal CDF via Abramowitz & Stegun 26.2.17 (max abs error ~7.5e-8).</summary>
    private static double NormalCdf(double z)
    {
        // Use error function approximation via tanh-based formula. For a self-contained
        // implementation without MathNet dependency, polynomial approx is fine for our
        // precision needs (we report to 2-3 decimal places).
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;
        var sign = z < 0 ? -1.0 : 1.0;
        var x = Math.Abs(z) / Math.Sqrt(2.0);
        var t = 1.0 / (1.0 + p * x);
        var erf = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + sign * erf);
    }
}

/// <summary>
/// Combined sequential promotion verdict. Combines SPRT (Wald) + Bayesian
/// posterior into a single recommendation:
/// - PROMOTE if SPRT accepts H1 AND posterior P(Sharpe ≥ 0.5) ≥ 0.7
/// - KILL if SPRT accepts H0 OR posterior P(Sharpe ≥ 0.0) &lt; 0.3
/// - CONTINUE otherwise
///
/// The dual-criterion design is conservative: both frequentist and Bayesian
/// must agree before promoting. Either alone can flag a kill.
/// </summary>
public static class SequentialDecisionRule
{
    public sealed record Verdict(
        SprtVerdict SprtResult,
        double PosteriorProbSharpeAtLeastHalf,
        double PosteriorProbSharpePositive,
        int Observations,
        string Recommendation);

    public static Verdict Evaluate(SprtSharpeTest sprt, BayesianSharpeEstimator bayes)
    {
        var pHalf = bayes.PosteriorProbSharpeAtLeast(0.5);
        var pPositive = bayes.PosteriorProbSharpeAtLeast(0.0);
        var sprtResult = sprt.LogLikelihoodRatio >= sprt.UpperBoundLogA
            ? SprtVerdict.AcceptHasEdge_Promote
            : sprt.LogLikelihoodRatio <= sprt.LowerBoundLogB
                ? SprtVerdict.AcceptNoEdge_Kill
                : SprtVerdict.Continue;

        string rec;
        if (sprtResult == SprtVerdict.AcceptHasEdge_Promote && pHalf >= 0.7)
            rec = "PROMOTE";
        else if (sprtResult == SprtVerdict.AcceptNoEdge_Kill || pPositive < 0.3)
            rec = "KILL";
        else
            rec = "CONTINUE";

        return new Verdict(sprtResult, pHalf, pPositive, sprt.Observations, rec);
    }
}
