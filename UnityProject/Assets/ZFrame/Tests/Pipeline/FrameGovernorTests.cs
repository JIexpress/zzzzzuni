using System;
using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// EditMode coverage of the FrameGovernor pure core (TASK-A-R8-01 design
    /// point 3): hysteresis double windows (down M, up N &gt; M, no oscillation),
    /// fixed measure order, RenderScale step bounds, full bypass when
    /// disabled, and GPU/CPU telemetry selection. No UnityEngine usage — the
    /// engine never appears; time comes in as dt arguments and cost samples
    /// as a fake <see cref="IFrameTelemetry"/>.
    /// </summary>
    public sealed class FrameGovernorTests
    {
        private const float TargetMs = 1000f / 60f; // 16.67ms
        private const float BadMs = 30f;            // > TargetMs * 1.1
        private const float GoodMs = 10f;           // < TargetMs * 0.9
        private const float DeadbandMs = TargetMs;  // strictly inside the ±10% band

        private sealed class FakeTelemetry : IFrameTelemetry
        {
            public float GpuFrameMs { get; set; }
            public float CpuFrameMs { get; set; }
        }

        private static FrameGovernor NewGovernor(QualityTier tier = QualityTier.High, float originalScale = GovernorLadder.MaxRenderScale)
        {
            return new FrameGovernor(GovernorLadder.Create(tier, originalScale))
            {
                Enabled = true,
                TargetMs = TargetMs,
            };
        }

        private static FakeTelemetry Bad() => new FakeTelemetry { CpuFrameMs = BadMs };
        private static FakeTelemetry Good() => new FakeTelemetry { CpuFrameMs = GoodMs };

        // ---- tuning ------------------------------------------------------------

        [Test]
        public void Tuning_Default_SatisfiesAntiOscillationRule()
        {
            GovernorTuning tuning = GovernorTuning.Default;
            Assert.That(tuning.UpWindowSeconds, Is.GreaterThan(tuning.DownWindowSeconds),
                "N > M is the briefing anti-oscillation rule");
            Assert.That(tuning.UpThreshold, Is.EqualTo(0.10f).Within(1e-5f));
            Assert.That(tuning.DownThreshold, Is.EqualTo(0.10f).Within(1e-5f));
        }

        [Test]
        public void Tuning_UpWindowNotGreaterThanDownWindow_Throws()
        {
            Assert.Throws<ArgumentException>(() => new GovernorTuning(0.1f, 0.1f, upWindowSeconds: 2f, downWindowSeconds: 2f));
            Assert.Throws<ArgumentException>(() => new GovernorTuning(0.1f, 0.1f, upWindowSeconds: 1f, downWindowSeconds: 2f));
        }

        [Test]
        public void Tuning_ThresholdOutsideUnitInterval_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GovernorTuning(0f, 0.1f, 5f, 2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GovernorTuning(1f, 0.1f, 5f, 2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GovernorTuning(0.1f, -0.5f, 5f, 2f));
        }

        [Test]
        public void Governor_EnabledDefaultsFalse_AndTargetMsStartsUngoverned()
        {
            var governor = new FrameGovernor(GovernorLadder.Create(QualityTier.High));
            Assert.That(governor.Enabled, Is.False, "Enabled must default to OFF (briefing design point)");
            Assert.That(governor.TargetMs, Is.EqualTo(0f));
        }

        // ---- ladder ------------------------------------------------------------

        [Test]
        public void Ladder_High_MeasureOrderIsFixed()
        {
            GovernorLadder ladder = GovernorLadder.Create(QualityTier.High);

            // RenderScale band: 0.95 down to 0.75 by 0.05 (all strictly below the original 1.0).
            float[] expectedScales = { 0.95f, 0.90f, 0.85f, 0.80f, 0.75f };
            for (int i = 0; i < expectedScales.Length; i++)
            {
                Assert.That(ladder.Measures[i].Kind, Is.EqualTo(GovernorMeasureKind.RenderScale), $"rung {i}");
                Assert.That(ladder.Measures[i].Value, Is.EqualTo(expectedScales[i]).Within(1e-4f), $"rung {i}");
            }

            // Shadows band: exactly one rung per tier below the user tier (高→中→关 = 2 rungs from High).
            Assert.That(ladder.Measures[5].Kind, Is.EqualTo(GovernorMeasureKind.Shadows));
            Assert.That(ladder.Measures[5].Value, Is.EqualTo(1f));
            Assert.That(ladder.Measures[6].Kind, Is.EqualTo(GovernorMeasureKind.Shadows));
            Assert.That(ladder.Measures[6].Value, Is.EqualTo(2f));

            // PostFx band then terminal TargetFps rung.
            Assert.That(ladder.Measures[7].Kind, Is.EqualTo(GovernorMeasureKind.PostFx));
            Assert.That(ladder.Measures[7].Value, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(ladder.Measures[8].Kind, Is.EqualTo(GovernorMeasureKind.PostFx));
            Assert.That(ladder.Measures[8].Value, Is.EqualTo(0f));
            Assert.That(ladder.Measures[9].Kind, Is.EqualTo(GovernorMeasureKind.TargetFps));
            Assert.That(ladder.Count, Is.EqualTo(10));
        }

        [Test]
        public void Ladder_ShadowBand_DependsOnUserTier()
        {
            Assert.That(CountKind(GovernorLadder.Create(QualityTier.High), GovernorMeasureKind.Shadows), Is.EqualTo(2));
            Assert.That(CountKind(GovernorLadder.Create(QualityTier.Medium), GovernorMeasureKind.Shadows), Is.EqualTo(1));
            Assert.That(CountKind(GovernorLadder.Create(QualityTier.Low), GovernorMeasureKind.Shadows), Is.EqualTo(0),
                "Low is the floor — nothing below it to subtract");
        }

        [Test]
        public void Ladder_RenderScaleBounds_NeverBelowMin_NeverAboveUserOriginal()
        {
            GovernorLadder full = GovernorLadder.Create(QualityTier.High);
            Assert.That(full.Measures[0].Value, Is.EqualTo(0.95f).Within(1e-4f));
            Assert.That(full.Measures[4].Value, Is.EqualTo(GovernorLadder.MinRenderScale).Within(1e-5f),
                "the band floor is the briefing 0.75");

            // Original 0.8: only 0.75 remains strictly below.
            GovernorLadder narrow = GovernorLadder.Create(QualityTier.High, originalRenderScale: 0.8f);
            Assert.That(CountKind(narrow, GovernorMeasureKind.RenderScale), Is.EqualTo(1));
            Assert.That(narrow.Measures[0].Value, Is.EqualTo(0.75f).Within(1e-5f));

            // Original already at the floor: the RenderScale band disappears entirely.
            GovernorLadder empty = GovernorLadder.Create(QualityTier.High, originalRenderScale: GovernorLadder.MinRenderScale);
            Assert.That(CountKind(empty, GovernorMeasureKind.RenderScale), Is.EqualTo(0));
        }

        [Test]
        public void Ladder_StateAtIndex_AccumulatesMeasures_AndClampsAtTop()
        {
            GovernorLadder ladder = GovernorLadder.Create(QualityTier.High);

            GovernorState baseline = ladder.StateAtIndex(0);
            Assert.That(baseline.RenderScale, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(baseline.ShadowStepsDown, Is.EqualTo(0));
            Assert.That(baseline.PostFxWeight, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(baseline.GiveUpTarget, Is.False);

            GovernorState shadows = ladder.StateAtIndex(6); // first Shadows rung
            Assert.That(shadows.RenderScale, Is.EqualTo(0.75f).Within(1e-4f), "RenderScale measures accumulate");
            Assert.That(shadows.ShadowStepsDown, Is.EqualTo(1));
            Assert.That(shadows.PostFxWeight, Is.EqualTo(1f).Within(1e-5f));

            Assert.That(ladder.StateAtIndex(7).ShadowStepsDown, Is.EqualTo(2));
            Assert.That(ladder.StateAtIndex(8).PostFxWeight, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(ladder.StateAtIndex(9).PostFxWeight, Is.EqualTo(0f).Within(1e-5f));

            GovernorState top = ladder.StateAtIndex(10);
            Assert.That(top.GiveUpTarget, Is.True);
            Assert.That(top.ShadowStepsDown, Is.EqualTo(2));

            GovernorState beyond = ladder.StateAtIndex(99);
            Assert.That(beyond.GiveUpTarget, Is.True, "levels beyond the ladder clamp to the give-up rung");
            Assert.That(beyond.PostFxWeight, Is.EqualTo(0f).Within(1e-5f));
        }

        // ---- hysteresis: walking down ------------------------------------------

        [Test]
        public void Hysteresis_WalksDown_InFixedLadderOrder_ThenStaysAtGiveUp()
        {
            FrameGovernor governor = NewGovernor();
            GovernorLadder ladder = governor.Ladder;

            for (int level = 1; level <= ladder.Count; level++)
            {
                GovernorDecision moved = default;
                for (int tick = 0; tick < 4; tick++) // 4 × 0.5s = M = 2s sustained overload
                {
                    GovernorDecision d = governor.Tick(0.5f, Bad());
                    if (d.Changed)
                    {
                        moved = d;
                    }
                }

                Assert.That(moved.Changed, Is.True, $"level {level} must be reached after one full M window");
                Assert.That(moved.ToIndex, Is.EqualTo(level));
                Assert.That(moved.To.Kind, Is.EqualTo(ladder.Measures[level - 1].Kind), "fixed measure order");
                Assert.That(governor.DownTimer, Is.EqualTo(0f).Within(1e-5f), "timers restart after every move");
            }

            Assert.That(governor.Level, Is.EqualTo(ladder.Count), "give-up rung is the floor of the ladder");
            for (int tick = 0; tick < 10; tick++)
            {
                Assert.That(governor.Tick(0.5f, Bad()).Changed, Is.False, "no rung exists below the give-up rung");
            }
        }

        [Test]
        public void Hysteresis_WindowBoundary_FullWindowMoves_HalfWindowDoesNot()
        {
            FrameGovernor governor = NewGovernor();

            for (int tick = 0; tick < 3; tick++) // 1.5s < M
            {
                governor.Tick(0.5f, Bad());
            }

            Assert.That(governor.Level, Is.EqualTo(0), "below M nothing moves");
            Assert.That(governor.Tick(0.5f, Bad()).Changed, Is.True, "exactly M = 2.0s moves");
            Assert.That(governor.Level, Is.EqualTo(1));

            // Upgrade needs the FULL N = 5s window (one rung back up).
            for (int tick = 0; tick < 9; tick++) // 4.5s < N
            {
                governor.Tick(0.5f, Good());
            }

            Assert.That(governor.Level, Is.EqualTo(1), "below N nothing moves");
            Assert.That(governor.Tick(0.5f, Good()).Changed, Is.True, "exactly N = 5.0s moves");
            Assert.That(governor.Level, Is.EqualTo(0));
        }

        [Test]
        public void Hysteresis_AlternatingLoad_NeverOscillates()
        {
            FrameGovernor governor = NewGovernor();

            // Worst case burst pattern: sub-window bad / sub-window good forever.
            for (int i = 0; i < 500; i++)
            {
                for (int tick = 0; tick < 3; tick++)
                {
                    GovernorDecision d = governor.Tick(0.2f, Bad()); // 0.6s bad (< M)
                    Assert.That(d.Changed, Is.False, $"iteration {i}: alternating load must never move the level");
                }

                for (int tick = 0; tick < 3; tick++)
                {
                    GovernorDecision d = governor.Tick(0.2f, Good()); // 0.6s good (< N)
                    Assert.That(d.Changed, Is.False, $"iteration {i}: alternating load must never move the level");
                }
            }

            Assert.That(governor.Level, Is.EqualTo(0));
        }

        [Test]
        public void Hysteresis_Deadband_ResetsBothTimers()
        {
            FrameGovernor governor = NewGovernor();

            governor.Tick(1.0f, Bad()); // 1.0s toward M
            governor.Tick(0.5f, new FakeTelemetry { CpuFrameMs = DeadbandMs }); // inside the band
            Assert.That(governor.DownTimer, Is.EqualTo(0f).Within(1e-5f), "deadband resets the overload timer");

            for (int tick = 0; tick < 3; tick++) // another 1.5s < M
            {
                governor.Tick(0.5f, Bad());
            }

            Assert.That(governor.Level, Is.EqualTo(0), "the pre-deadband overload must not count toward the window");
        }

        [Test]
        public void Hysteresis_ThresholdBand_InsideDoesNotMove_OutsideCountsWindows()
        {
            FrameGovernor governor = NewGovernor();

            // +9% over target: inside the +10% deadband — never over budget.
            for (int tick = 0; tick < 20; tick++)
            {
                governor.Tick(0.5f, new FakeTelemetry { CpuFrameMs = TargetMs * 1.09f });
            }

            Assert.That(governor.Level, Is.EqualTo(0), "inside the +10% band is NOT over budget");

            // +11% over target: strictly beyond the band — the M window counts.
            GovernorDecision d = default;
            for (int tick = 0; tick < 4; tick++) // 2.0s == M
            {
                d = governor.Tick(0.5f, new FakeTelemetry { CpuFrameMs = TargetMs * 1.11f });
            }

            Assert.That(d.Changed, Is.True, "beyond +10% the sustained-overload window counts");
            Assert.That(governor.Level, Is.EqualTo(1));

            // -9% under target at level 1: inside the -10% deadband — never comfortable.
            for (int tick = 0; tick < 20; tick++)
            {
                governor.Tick(0.5f, new FakeTelemetry { CpuFrameMs = TargetMs * 0.91f });
            }

            Assert.That(governor.Level, Is.EqualTo(1), "inside the -10% band is NOT comfortable");

            // -11% under target: strictly beyond — the N window counts (N > M).
            for (int tick = 0; tick < 9; tick++) // 4.5s < N
            {
                governor.Tick(0.5f, new FakeTelemetry { CpuFrameMs = TargetMs * 0.89f });
            }

            Assert.That(governor.Level, Is.EqualTo(1), "below N nothing moves");
            d = governor.Tick(0.5f, new FakeTelemetry { CpuFrameMs = TargetMs * 0.89f }); // 5.0s == N
            Assert.That(d.Changed, Is.True, "exactly N = 5s moves");
            Assert.That(governor.Level, Is.EqualTo(0));
        }

        // ---- bypass -------------------------------------------------------------

        [Test]
        public void Disabled_FullBypass_NoMoveNoTelemetryProcessing()
        {
            var governor = new FrameGovernor(GovernorLadder.Create(QualityTier.High))
            {
                TargetMs = TargetMs, // Enabled stays false — the briefing default
            };

            for (int tick = 0; tick < 100; tick++)
            {
                GovernorDecision d = governor.Tick(1f, Bad());
                Assert.That(d.Changed, Is.False, "disabled governor must never decide");
            }

            Assert.That(governor.Level, Is.EqualTo(0));
            Assert.That(governor.LastSampleUsedGpu, Is.False, "a bypassed tick never touches telemetry");
        }

        [Test]
        public void Disabled_TimersAreColdWhenReenabled()
        {
            var governor = new FrameGovernor(GovernorLadder.Create(QualityTier.High))
            {
                TargetMs = TargetMs,
            };

            for (int tick = 0; tick < 10; tick++) // warm the machine while bypassed
            {
                governor.Tick(1f, Bad());
            }

            governor.Enabled = true;

            for (int tick = 0; tick < 4; tick++) // 4s of comfort < N
            {
                governor.Tick(1f, Good());
            }

            Assert.That(governor.Level, Is.EqualTo(0), "bypass time must not leak into the hysteresis windows");

            for (int tick = 0; tick < 4; tick++) // 4 × 0.5s (clamped) == M = 2s
            {
                governor.Tick(1f, Bad());
            }

            Assert.That(governor.Level, Is.EqualTo(1), "after re-enable the machine governs normally");
        }

        [Test]
        public void TargetMsNonPositive_FullBypass()
        {
            FrameGovernor governor = NewGovernor();
            governor.TargetMs = 0f;

            for (int tick = 0; tick < 20; tick++)
            {
                Assert.That(governor.Tick(1f, Bad()).Changed, Is.False, "no target = no governance");
            }

            Assert.That(governor.Level, Is.EqualTo(0));
        }

        [Test]
        public void NullSampleOrNonPositiveDt_BypassesAndResetsTimers()
        {
            FrameGovernor governor = NewGovernor();

            governor.Tick(1.5f, Bad()); // 1.5s toward M
            governor.Tick(0.5f, null);
            Assert.That(governor.DownTimer, Is.EqualTo(0f).Within(1e-5f), "a null sample resets the windows");
            Assert.That(governor.Tick(-1f, Bad()).Changed, Is.False, "non-positive dt bypasses");

            for (int tick = 0; tick < 3; tick++) // 1.5s < M again
            {
                governor.Tick(0.5f, Bad());
            }

            Assert.That(governor.Level, Is.EqualTo(0), "no stale accumulation across gaps");
        }

        // ---- telemetry selection -------------------------------------------------

        [Test]
        public void Telemetry_GpuPreferred_CpuFallback_UnusableIgnored()
        {
            // GPU available: GPU cost drives, even when CPU claims comfort.
            FrameGovernor gpuGovernor = NewGovernor();
            GovernorDecision d = default;
            for (int tick = 0; tick < 4; tick++)
            {
                d = gpuGovernor.Tick(0.5f, new FakeTelemetry { GpuFrameMs = BadMs, CpuFrameMs = GoodMs });
            }

            Assert.That(d.Changed, Is.True, "GPU cost decides when available");
            Assert.That(gpuGovernor.LastSampleUsedGpu, Is.True);

            // GPU unavailable (0 sentinel): CPU fallback drives.
            FrameGovernor cpuGovernor = NewGovernor();
            for (int tick = 0; tick < 4; tick++)
            {
                d = cpuGovernor.Tick(0.5f, new FakeTelemetry { GpuFrameMs = 0f, CpuFrameMs = BadMs });
            }

            Assert.That(d.Changed, Is.True, "CPU fallback must drive when GPU timing is unavailable");
            Assert.That(cpuGovernor.LastSampleUsedGpu, Is.False, "the fallback is visible to diagnostics");

            // Both zero: unusable sample — ignored, timers reset.
            FrameGovernor unusable = NewGovernor();
            unusable.Tick(1.5f, Bad());
            unusable.Tick(0.5f, new FakeTelemetry());
            Assert.That(unusable.DownTimer, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(unusable.Level, Is.EqualTo(0));
        }

        [Test]
        public void Telemetry_SingleHugeTick_CannotSatisfySustainedWindow()
        {
            FrameGovernor governor = NewGovernor();

            Assert.That(governor.Tick(10f, Bad()).Changed, Is.False,
                "one 10s hitch clamps to MaxDeltaSeconds and must not insta-downgrade");

            Assert.That(governor.Level, Is.EqualTo(0));
        }

        // ---- decision payload ----------------------------------------------------

        [Test]
        public void Decision_FirstMove_CarriesFromNoneToFirstRung()
        {
            FrameGovernor governor = NewGovernor();

            GovernorDecision d = default;
            for (int tick = 0; tick < 4; tick++)
            {
                d = governor.Tick(0.5f, Bad());
            }

            Assert.That(d.Changed, Is.True);
            Assert.That(d.FromIndex, Is.EqualTo(0));
            Assert.That(d.ToIndex, Is.EqualTo(1));
            Assert.That(d.From.Kind, Is.EqualTo(GovernorMeasureKind.None));
            Assert.That(d.To.Kind, Is.EqualTo(GovernorMeasureKind.RenderScale));
            Assert.That(d.To.Value, Is.EqualTo(0.95f).Within(1e-4f));
        }

        private static int CountKind(GovernorLadder ladder, GovernorMeasureKind kind)
        {
            int count = 0;
            for (int i = 0; i < ladder.Count; i++)
            {
                if (ladder.Measures[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
