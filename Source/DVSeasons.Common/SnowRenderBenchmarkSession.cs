using System;

namespace DVSeasons.Core
{
    public enum SnowRenderBenchmarkPhase
    {
        Baseline,
        WithoutVehicleSurfaces,
        WithoutShading,
        WithoutProceduralSnow,
        RestoredBaseline
    }

    public struct SnowRenderBenchmarkResult
    {
        public SnowRenderBenchmarkPhase Phase { get; private set; }
        public int FrameCount { get; private set; }
        public double MeasuredSeconds { get; private set; }
        public double Fps { get { return MeasuredSeconds > 0d ? FrameCount / MeasuredSeconds : 0d; } }
        public double MinimumFrameMilliseconds { get; private set; }
        public double MaximumFrameMilliseconds { get; private set; }
        public int MissingRenderFrames { get; private set; }
        public double MissingRenderSeconds { get; private set; }
        public bool Contaminated { get { return FrameCount == 0 || MissingRenderFrames != 0; } }

        internal SnowRenderBenchmarkResult(SnowRenderBenchmarkPhase phase, int count,
            double seconds, double minimumSeconds, double maximumSeconds,
            int missingFrames, double missingSeconds)
        {
            Phase = phase;
            FrameCount = count;
            MeasuredSeconds = seconds;
            MinimumFrameMilliseconds = count > 0 ? minimumSeconds * 1000d : 0d;
            MaximumFrameMilliseconds = maximumSeconds * 1000d;
            MissingRenderFrames = missingFrames;
            MissingRenderSeconds = missingSeconds;
        }
    }

    /// <summary>
    /// Explicitly started, frame-driven rendering experiment. Tick receives the
    /// elapsed time and render status of the preceding frame; callers must apply
    /// the resulting flags only to the next frame. No Unity or settings state is
    /// owned here, and there is no recurring or automatic start.
    /// </summary>
    public sealed class SnowRenderBenchmarkSession
    {
        public const int PhaseCount = 5;
        public const double StartDelaySeconds = 5d;
        public const double WarmupSeconds = 2d;
        public const double MeasurementSeconds = 8d;
        private const double PhaseDurationSeconds = WarmupSeconds + MeasurementSeconds;

        private readonly SnowRenderBenchmarkResult[] results = new SnowRenderBenchmarkResult[PhaseCount];
        private double delayElapsed, phaseElapsed, measuredSeconds, minimumSeconds, maximumSeconds;
        private double missingRenderSeconds;
        private int frameCount, missingRenderFrames, readResultIndex;

        public bool Active { get; private set; }
        public bool Waiting { get { return Active && PhaseIndex < 0; } }
        public bool WarmingUp { get { return Active && !Waiting && phaseElapsed < WarmupSeconds; } }
        public int PhaseIndex { get; private set; } = -1;
        public SnowRenderBenchmarkPhase Phase
        {
            get { return PhaseIndex >= 0 ? (SnowRenderBenchmarkPhase)PhaseIndex : SnowRenderBenchmarkPhase.Baseline; }
        }
        public int CompletedCount { get; private set; }
        public bool SkipAll
        {
            get { return Active && !Waiting && Phase == SnowRenderBenchmarkPhase.WithoutProceduralSnow; }
        }
        public bool SkipVehicleSurfaces
        {
            get { return Active && !Waiting && (Phase == SnowRenderBenchmarkPhase.WithoutVehicleSurfaces || SkipAll); }
        }
        public bool SkipShading
        {
            get { return Active && !Waiting && (Phase == SnowRenderBenchmarkPhase.WithoutShading || SkipAll); }
        }
        public double PhaseSecondsRemaining
        {
            get
            {
                if (!Active) return 0d;
                return Waiting ? Math.Max(0d, StartDelaySeconds - delayElapsed)
                    : Math.Max(0d, PhaseDurationSeconds - phaseElapsed);
            }
        }
        public double SecondsRemaining
        {
            get
            {
                if (!Active) return 0d;
                return PhaseSecondsRemaining + (Waiting ? PhaseCount : PhaseCount - PhaseIndex - 1) * PhaseDurationSeconds;
            }
        }

        public void Start()
        {
            Cancel();
            Active = true;
        }

        public void Cancel()
        {
            Active = false;
            PhaseIndex = -1;
            delayElapsed = 0d;
            CompletedCount = readResultIndex = 0;
            Array.Clear(results, 0, results.Length);
            ResetPhaseMeasurement();
        }

        public void Tick(double deltaSeconds, bool frameRendered)
        {
            if (!Active || double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0d)
                return;

            if (Waiting)
            {
                delayElapsed += deltaSeconds;
                if (delayElapsed >= StartDelaySeconds)
                {
                    PhaseIndex = 0;
                    ResetPhaseMeasurement();
                }
                // This entire interval ran before the new flags were published.
                // Never transfer a large delay or pause into a later phase.
                return;
            }

            if (phaseElapsed >= WarmupSeconds)
            {
                if (frameRendered)
                {
                    frameCount++;
                    measuredSeconds += deltaSeconds;
                    minimumSeconds = Math.Min(minimumSeconds, deltaSeconds);
                    maximumSeconds = Math.Max(maximumSeconds, deltaSeconds);
                }
                else
                {
                    missingRenderFrames++;
                    missingRenderSeconds += deltaSeconds;
                }
            }
            // A frame that began in warmup is excluded in its entirety. Splitting
            // it would manufacture a short frame and bias the measured FPS.
            phaseElapsed += deltaSeconds;
            if (phaseElapsed < PhaseDurationSeconds) return;

            results[CompletedCount++] = new SnowRenderBenchmarkResult(Phase, frameCount,
                measuredSeconds, minimumSeconds, maximumSeconds, missingRenderFrames, missingRenderSeconds);
            if (PhaseIndex == PhaseCount - 1)
            {
                // Keep results available after completion, but restore every flag.
                Active = false;
                PhaseIndex = -1;
            }
            else
            {
                PhaseIndex++;
                ResetPhaseMeasurement();
            }
        }

        public bool TryTakeCompletedResult(out SnowRenderBenchmarkResult result)
        {
            if (readResultIndex >= CompletedCount)
            {
                result = default(SnowRenderBenchmarkResult);
                return false;
            }
            result = results[readResultIndex++];
            return true;
        }

        public SnowRenderBenchmarkResult GetResult(int index)
        {
            if (index < 0 || index >= CompletedCount) throw new ArgumentOutOfRangeException(nameof(index));
            return results[index];
        }

        private void ResetPhaseMeasurement()
        {
            phaseElapsed = measuredSeconds = maximumSeconds = missingRenderSeconds = 0d;
            minimumSeconds = double.PositiveInfinity;
            frameCount = missingRenderFrames = 0;
        }
    }
}
