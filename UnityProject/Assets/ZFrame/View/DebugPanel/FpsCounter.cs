using System;

namespace ZFrame.View
{
    /// <summary>
    /// Sliding-window frame-time aggregator. Pure C# (no UnityEngine) so it
    /// is unit-testable: the owner pumps measured frame durations via
    /// <see cref="Tick"/> and reads FPS / average ms / worst-frame ms.
    /// </summary>
    public sealed class FpsCounter
    {
        private readonly double[] samples;
        private int count;
        private int head;
        private double total;

        /// <param name="windowSize">Number of most-recent frames averaged.</param>
        public FpsCounter(int windowSize)
        {
            if (windowSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }

            samples = new double[windowSize];
        }

        /// <summary>Frames currently in the window (saturates at window size).</summary>
        public int SampleCount => count;

        /// <summary>Pumps one measured frame duration (seconds) into the window.</summary>
        public void Tick(double deltaSeconds)
        {
            if (deltaSeconds < 0)
            {
                deltaSeconds = 0;
            }

            if (count == samples.Length)
            {
                // Window full: subtract the outgoing sample before overwrite.
                total -= samples[head];
            }
            else
            {
                count++;
            }

            samples[head] = deltaSeconds;
            total += deltaSeconds;
            head = (head + 1) % samples.Length;
        }

        /// <summary>Average frame time in milliseconds; 0 when no samples yet.</summary>
        public double AverageMs
        {
            get
            {
                if (count == 0)
                {
                    return 0;
                }

                return total / count * 1000.0;
            }
        }

        /// <summary>Frames per second over the window (1 / average); 0 when no samples yet.</summary>
        public double Fps
        {
            get
            {
                var avgMs = AverageMs;
                return avgMs <= 0 ? 0 : 1000.0 / avgMs;
            }
        }

        /// <summary>Worst (longest) frame time in the window, in milliseconds.</summary>
        public double WorstMs
        {
            get
            {
                if (count == 0)
                {
                    return 0;
                }

                var worst = samples[0];
                for (int i = 1; i < count; i++)
                {
                    if (samples[i] > worst)
                    {
                        worst = samples[i];
                    }
                }

                return worst * 1000.0;
            }
        }

        /// <summary>Clears all samples.</summary>
        public void Reset()
        {
            count = 0;
            head = 0;
            total = 0;
            Array.Clear(samples, 0, samples.Length);
        }
    }
}
