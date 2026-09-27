using NeuroCamera.Engine;
using Xunit;

namespace NeuroCamera.Tests;

public class CalibrationSafeguardsTests
{
    // --- BackgroundSaturation -----------------------------------------------------------

    [Fact]
    public void Perfectly_neutral_gray_has_zero_saturation()
    {
        Assert.Equal(0.0, CalibrationSafeguards.BackgroundSaturation(120, 120, 120), 6);
    }

    [Fact]
    public void A_typical_room_wall_has_low_saturation()
    {
        // Beige/warm-toned walls under normal light: consistently well under the full-trust
        // threshold, which is exactly what should let ordinary rooms behave as before this fix.
        double beige = CalibrationSafeguards.BackgroundSaturation(110, 130, 150);
        double warmRoom = CalibrationSafeguards.BackgroundSaturation(100, 120, 140);

        Assert.True(beige < CalibrationSafeguards.BackgroundSaturationFullTrust, $"beige={beige}");
        Assert.True(warmRoom < CalibrationSafeguards.BackgroundSaturationFullTrust, $"warmRoom={warmRoom}");
    }

    [Fact]
    public void A_saturated_colored_backdrop_has_high_saturation()
    {
        // The reported case: a red streaming curtain. B,G,R chosen so red clearly dominates.
        double redCurtain = CalibrationSafeguards.BackgroundSaturation(25, 20, 90);

        Assert.True(redCurtain > CalibrationSafeguards.BackgroundSaturationNoTrust, $"redCurtain={redCurtain}");
        Assert.Equal(1.0, redCurtain, 6);
    }

    [Fact]
    public void Saturation_is_symmetric_regardless_of_which_channel_dominates()
    {
        double redDominant = CalibrationSafeguards.BackgroundSaturation(10, 10, 100);
        double greenDominant = CalibrationSafeguards.BackgroundSaturation(10, 100, 10);
        double blueDominant = CalibrationSafeguards.BackgroundSaturation(100, 10, 10);

        Assert.Equal(redDominant, greenDominant, 6);
        Assert.Equal(redDominant, blueDominant, 6);
    }

    [Fact]
    public void Saturation_is_always_within_zero_to_one()
    {
        Random random = new(778);
        for (int i = 0; i < 500; i++)
        {
            double b = random.NextDouble() * 255.0;
            double g = random.NextDouble() * 255.0;
            double r = random.NextDouble() * 255.0;

            double saturation = CalibrationSafeguards.BackgroundSaturation(b, g, r);

            Assert.InRange(saturation, 0.0, 1.0);
        }
    }

    [Fact]
    public void A_near_black_background_does_not_produce_nan_or_throw()
    {
        double saturation = CalibrationSafeguards.BackgroundSaturation(0.2, 0.1, 0.4);

        Assert.False(double.IsNaN(saturation));
        Assert.InRange(saturation, 0.0, 1.0);
    }

    // --- BackgroundColorTrust ------------------------------------------------------------

    [Fact]
    public void Trust_is_full_for_a_typical_room_background()
    {
        Assert.Equal(1.0, CalibrationSafeguards.BackgroundColorTrust(120, 120, 120), 6);
        Assert.Equal(1.0, CalibrationSafeguards.BackgroundColorTrust(110, 130, 150), 6);
        Assert.Equal(1.0, CalibrationSafeguards.BackgroundColorTrust(100, 120, 140), 6);
    }

    [Fact]
    public void Trust_is_zero_for_a_strongly_colored_backdrop()
    {
        // The exact case from the reported screenshot's likely raw background.
        Assert.Equal(0.0, CalibrationSafeguards.BackgroundColorTrust(25, 20, 90), 6);
    }

    [Fact]
    public void Trust_ramps_down_smoothly_between_the_two_thresholds()
    {
        double previous = 1.0;
        for (double saturation = CalibrationSafeguards.BackgroundSaturationFullTrust;
             saturation <= CalibrationSafeguards.BackgroundSaturationNoTrust;
             saturation += 0.02)
        {
            // Construct a background with exactly this saturation (max channel = mean*(1+sat), rest neutral).
            double mean = 100.0;
            double b = mean;
            double g = mean;
            double r = mean * (1.0 + saturation);

            double trust = CalibrationSafeguards.BackgroundColorTrust(b, g, r);

            Assert.InRange(trust, 0.0, 1.0);
            Assert.True(trust <= previous + 1e-9, $"trust increased at saturation={saturation}: {trust} > {previous}");
            previous = trust;
        }
    }

    [Fact]
    public void Trust_never_goes_outside_zero_to_one_for_arbitrary_backgrounds()
    {
        Random random = new(2026);
        for (int i = 0; i < 500; i++)
        {
            double b = random.NextDouble() * 255.0;
            double g = random.NextDouble() * 255.0;
            double r = random.NextDouble() * 255.0;

            double trust = CalibrationSafeguards.BackgroundColorTrust(b, g, r);

            Assert.InRange(trust, 0.0, 1.0);
        }
    }

    // --- RaiseBetaToProtectShadows --------------------------------------------------------

    // Regression test: an earlier version of this guard compared against an ABSOLUTE output
    // floor, which meant it fired even when Alpha=1/Beta=0/Gamma=1 - i.e. when nothing was
    // being corrected at all - because a raw dark pixel (level 20) is naturally below that
    // absolute floor. That version would have brightened an already-dark, completely untouched
    // scene the person never asked to change. The guard must be a genuine no-op whenever the
    // parameters it receives make no change at that level.
    [Fact]
    public void Fully_neutral_parameters_never_trigger_the_guard()
    {
        double beta = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma: 1.0, alpha: 1.0, beta: 0.0);

        Assert.Equal(0.0, beta, 6);
    }

    [Fact]
    public void Gamma_only_with_no_alpha_beta_darkening_never_triggers_the_guard()
    {
        // Any gamma, as long as alpha=1 and beta=0 (no additional contrast/brightness stretch),
        // must be a no-op: the guard only ever reacts to what alpha/beta add on top of gamma.
        foreach (double gamma in new[] { 0.65, 0.9, 1.0, 1.3, 1.9 })
        {
            double beta = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha: 1.0, beta: 0.0);
            Assert.Equal(0.0, beta, 6);
        }
    }

    [Fact]
    public void A_well_lit_high_contrast_face_never_triggers_the_guard()
    {
        // face luminance 140 (already at target), stddev 55 (already at target) -> alpha=1, beta=0,
        // gamma=1 -- the guard must leave this completely untouched.
        double beta = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma: 1.0, alpha: 1.0, beta: 0.0);

        Assert.Equal(0.0, beta, 6);
    }

    [Fact]
    public void The_reported_low_contrast_bright_face_scenario_meaningfully_improves_the_background_even_though_the_movement_cap_binds()
    {
        // gamma≈1.56 (face measured ~100, target 140), alpha=1.35 (low face contrast), the
        // resulting beta=-49 -- this is exactly the combination that, applied to a dim
        // background, was measured (via the ColorLut table directly) to crush it toward black.
        // Fully clearing the floor here would need a beta swing bigger than the movement cap
        // allows (by design - see ShadowFloorMaxBetaMovement's doc comment), so this checks what
        // the guard is actually specified to do: raise beta by up to the cap and meaningfully
        // improve the floor, even without fully solving it.
        double gamma = 1.56;
        double alpha = 1.35;
        double betaBefore = -49.0;

        double betaAfter = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, betaBefore);

        Assert.Equal(betaBefore + CalibrationSafeguards.ShadowFloorMaxBetaMovement, betaAfter, 6);

        byte[] beforeTable = ColorLut.BuildChannelTable(1.0, gamma, alpha, betaBefore);
        byte[] afterTable = ColorLut.BuildChannelTable(1.0, gamma, alpha, betaAfter);
        int floorLevel = (int)CalibrationSafeguards.ShadowFloorInputLevel;

        Assert.True(
            afterTable[floorLevel] > beforeTable[floorLevel] + 15,
            $"floor barely improved: before={beforeTable[floorLevel]}, after={afterTable[floorLevel]}");
    }

    [Fact]
    public void A_moderately_low_contrast_face_within_the_movement_cap_gets_the_shadow_floor_fully_protected()
    {
        // A milder version of the same scenario (alpha=1.2 rather than 1.35): the needed beta
        // swing fits within the movement cap, so this checks the guard's other documented
        // outcome - a full resolution, not just a partial one.
        double gamma = 1.0;
        double alpha = 1.2;
        double betaBefore = Math.Clamp(140.0 * (1.0 - alpha), -80.0, 80.0);

        double betaAfter = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, betaBefore);

        Assert.True(betaAfter > betaBefore, $"beta was not raised: before={betaBefore}, after={betaAfter}");
        Assert.True(
            betaAfter - betaBefore <= CalibrationSafeguards.ShadowFloorMaxBetaMovement + 1e-9,
            $"moved beta by more than the cap: before={betaBefore}, after={betaAfter}");

        byte[] gammaOnlyTable = ColorLut.BuildChannelTable(1.0, gamma, 1.0, 0.0);
        byte[] afterTable = ColorLut.BuildChannelTable(1.0, gamma, alpha, betaAfter);
        int floorLevel = (int)CalibrationSafeguards.ShadowFloorInputLevel;

        double minAllowed = Math.Max(0.0, gammaOnlyTable[floorLevel] - CalibrationSafeguards.ShadowFloorMaxExtraDarkening);
        Assert.True(
            afterTable[floorLevel] >= minAllowed,
            $"shadow floor still crushed: gammaOnly={gammaOnlyTable[floorLevel]}, minAllowed={minAllowed}, actual={afterTable[floorLevel]}");
    }

    [Fact]
    public void The_guard_never_lowers_beta_only_raises_it()
    {
        Random random = new(90210);
        for (int i = 0; i < 300; i++)
        {
            double gamma = 0.65 + (random.NextDouble() * 1.25); // ImageProcessor.SolveGammaForTargetLuminance's range
            double alpha = 0.85 + (random.NextDouble() * 0.5);  // the alpha clamp range
            double beta = -80.0 + (random.NextDouble() * 160.0); // the beta clamp range

            double adjusted = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, beta);

            Assert.True(adjusted >= beta - 1e-9, $"beta was lowered: gamma={gamma}, alpha={alpha}, beta={beta} -> {adjusted}");
        }
    }

    [Fact]
    public void The_guard_never_exceeds_the_same_plus_minus_eighty_range_every_other_beta_uses()
    {
        Random random = new(4242);
        for (int i = 0; i < 300; i++)
        {
            double gamma = 0.65 + (random.NextDouble() * 1.25);
            double alpha = 0.85 + (random.NextDouble() * 0.5);
            double beta = -80.0 + (random.NextDouble() * 160.0);

            double adjusted = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, beta);

            Assert.InRange(adjusted, -80.0, 80.0);
        }
    }

    [Fact]
    public void The_guard_never_moves_beta_by_more_than_its_documented_movement_cap()
    {
        Random random = new(13);
        for (int i = 0; i < 300; i++)
        {
            double gamma = 0.65 + (random.NextDouble() * 1.25);
            double alpha = 0.85 + (random.NextDouble() * 0.5);
            double beta = -80.0 + (random.NextDouble() * 160.0);

            double adjusted = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, beta);

            Assert.True(
                adjusted - beta <= CalibrationSafeguards.ShadowFloorMaxBetaMovement + 1e-9,
                $"moved beta by {adjusted - beta}, more than the {CalibrationSafeguards.ShadowFloorMaxBetaMovement} cap " +
                $"(gamma={gamma}, alpha={alpha}, beta={beta})");
        }
    }

    [Fact]
    public void When_the_guard_triggers_the_face_target_never_drifts_by_more_than_the_movement_cap_would_allow()
    {
        // The face mean, by construction of CalibrationEngine.BuildFinalParameters, equals
        // Alpha*TargetFaceLuminance + Beta exactly under the beta the face-targeting math
        // solved. Moving beta by at most the movement cap therefore moves the face mean by at
        // most the same amount - this is the guard's explicit trade-off, and it must hold.
        double[] gammas = { 0.65, 0.78, 1.0, 1.26, 1.56, 1.9 };
        double[] alphas = { 0.85, 1.0, 1.15, 1.35 };
        double[] betas = { -80, -49, -21, 0, 21 };

        foreach (double gamma in gammas)
        {
            foreach (double alpha in alphas)
            {
                foreach (double betaBefore in betas)
                {
                    double betaAfter = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, betaBefore);

                    Assert.True(
                        betaAfter - betaBefore <= CalibrationSafeguards.ShadowFloorMaxBetaMovement + 1e-9,
                        $"gamma={gamma}, alpha={alpha}, betaBefore={betaBefore} -> betaAfter={betaAfter}");
                }
            }
        }
    }

    [Fact]
    public void When_the_guard_triggers_it_always_improves_or_fully_fixes_the_shadow_floor()
    {
        // Sweep the same (gamma, alpha, beta) space used to diagnose the bug and confirm: every
        // time the guard changes beta at all, the floor level output strictly increases (i.e.
        // it never spends a beta movement for no measurable benefit).
        double[] gammas = { 0.65, 0.78, 1.0, 1.26, 1.56, 1.9 };
        double[] alphas = { 0.85, 1.0, 1.15, 1.35 };
        double[] betas = { -80, -49, -21, 0, 21 };
        int floorLevel = (int)CalibrationSafeguards.ShadowFloorInputLevel;

        foreach (double gamma in gammas)
        {
            foreach (double alpha in alphas)
            {
                foreach (double betaBefore in betas)
                {
                    double betaAfter = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, betaBefore);
                    if (betaAfter == betaBefore)
                    {
                        continue;
                    }

                    byte[] before = ColorLut.BuildChannelTable(1.0, gamma, alpha, betaBefore);
                    byte[] after = ColorLut.BuildChannelTable(1.0, gamma, alpha, betaAfter);

                    Assert.True(
                        after[floorLevel] > before[floorLevel],
                        $"guard moved beta but did not improve the floor: gamma={gamma}, alpha={alpha}, " +
                        $"betaBefore={betaBefore} (floor={before[floorLevel]}) -> betaAfter={betaAfter} (floor={after[floorLevel]})");
                }
            }
        }
    }

    // The movement cap is relative to whatever beta is PASSED IN, by design (see
    // RaiseBetaToProtectShadows's doc comment): CalibrationEngine's one call site always passes
    // the beta its own face-targeting math just solved, so this is the only usage this function
    // is specified for. A second call on the first call's own output is a different, unsupported
    // usage - it gets a fresh movement budget relative to the new starting point, so it is not
    // guaranteed to return the same value. This test documents that explicitly (rather than
    // silently assuming idempotency, which an earlier version of this test incorrectly did).
    [Fact]
    public void A_single_call_matching_the_real_call_site_is_well_behaved_and_a_second_call_is_not_assumed_idempotent()
    {
        double gamma = 1.56;
        double alpha = 1.35;
        double betaBefore = -49.0;

        double once = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, betaBefore);

        // The single, real usage: bounded by the movement cap, moved in the right direction.
        Assert.True(once > betaBefore, $"beta was not raised: before={betaBefore}, after={once}");
        Assert.True(
            once - betaBefore <= CalibrationSafeguards.ShadowFloorMaxBetaMovement + 1e-9,
            $"moved beta by more than the cap on the first call: before={betaBefore}, after={once}");

        // A second call is deliberately not asserted to equal `once` - see the comment above.
        // It must still individually satisfy the same per-call contract (never lowers beta,
        // never exceeds the +-80 range, never moves further than the cap from ITS OWN input).
        double twice = CalibrationSafeguards.RaiseBetaToProtectShadows(gamma, alpha, once);
        Assert.True(twice >= once - 1e-9, $"second call lowered beta: {once} -> {twice}");
        Assert.InRange(twice, -80.0, 80.0);
        Assert.True(
            twice - once <= CalibrationSafeguards.ShadowFloorMaxBetaMovement + 1e-9,
            $"second call moved beta by more than the cap relative to its own input: {once} -> {twice}");
    }
}
