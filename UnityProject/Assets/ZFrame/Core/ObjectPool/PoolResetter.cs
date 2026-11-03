using UnityEngine;

namespace ZFrame.Core
{
    /// <summary>
    /// Resets common per-instance residue (physics velocities, trails,
    /// particles) before a pooled object is stored, and restores renderer
    /// visibility on spawn. Pools own one disabled instance of this component
    /// as a stateless helper; it never runs Update logic of its own.
    /// </summary>
    public sealed class PoolResetter : MonoBehaviour
    {
        /// <summary>Clears volatile state on <paramref name="target"/> and hides trail/line renderers.</summary>
        public void ResetForReuse(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            var rigidbody = target.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.velocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
            }

            var trail = target.GetComponent<TrailRenderer>();
            if (trail != null)
            {
                trail.Clear();
                trail.enabled = false;
            }

            var line = target.GetComponent<LineRenderer>();
            if (line != null)
            {
                line.enabled = false;
            }

            var particles = target.GetComponent<ParticleSystem>();
            if (particles != null)
            {
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        /// <summary>Re-enables renderers hidden by <see cref="ResetForReuse"/> when the instance spawns.</summary>
        public void RestoreVisibility(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            var trail = target.GetComponent<TrailRenderer>();
            if (trail != null)
            {
                trail.enabled = true;
            }

            var line = target.GetComponent<LineRenderer>();
            if (line != null)
            {
                line.enabled = true;
            }
        }
    }
}
