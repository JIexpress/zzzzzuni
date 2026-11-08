using System.Collections.Generic;
using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Explosive-bolt separator module of the assembly system
    /// (TASK-A-TECH-05): <see cref="Detach"/> breaks every bond on the host
    /// part and applies a separation impulse pair — the host body is pushed
    /// away from each former partner, the partner pushed equally and
    /// oppositely along the same axis (Newton's third law), the "explosive"
    /// in 爆燃分离. The stage split is physics-first: the graph is refreshed
    /// by <see cref="AssemblyGraphSync"/> (joint-broken event), the module
    /// itself never touches graph data.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class AssemblySeparator : MonoBehaviour
    {
        [SerializeField] private float _impulseNs = 1.5f;

        private Rigidbody _body;

        /// <summary>Separation impulse per severed bond, in newton-seconds.</summary>
        public float ImpulseNs => _impulseNs;

        /// <summary>Sets the separation impulse (factory/test configuration).</summary>
        public void SetImpulse(float newtonSeconds)
        {
            _impulseNs = Mathf.Max(0f, newtonSeconds);
        }

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
        }

        /// <summary>
        /// Fires the separation: breaks every unbroken joint on the host
        /// part and applies the impulse pair for each. Returns the severed
        /// joints (empty when there was nothing to sever — a safe no-op).
        /// </summary>
        public List<AssemblyJoint> Detach(float? impulseNs = null)
        {
            var severed = new List<AssemblyJoint>();
            AssemblyPart host = GetComponent<AssemblyPart>();
            if (host == null)
            {
                return severed;
            }

            float charge = impulseNs ?? _impulseNs;
            if (charge <= 0f)
            {
                // Zero charge still cuts the bolts, just without the push.
                for (int i = host.Joints.Count - 1; i >= 0; i--)
                {
                    AssemblyJoint joint = host.Joints[i];
                    if (joint != null && !joint.IsBroken)
                    {
                        severed.Add(joint);
                        joint.Break();
                    }
                }

                return severed;
            }

            if (_body == null)
            {
                _body = GetComponent<Rigidbody>();
            }

            for (int i = host.Joints.Count - 1; i >= 0; i--)
            {
                AssemblyJoint joint = host.Joints[i];
                if (joint == null || joint.IsBroken)
                {
                    continue;
                }

                // Capture the partner BEFORE Break: Break clears bindings.
                Rigidbody partner = joint.OwnerPart == host
                    ? (joint.OtherPart != null ? joint.OtherPart.Body : null)
                    : (joint.OwnerPart != null ? joint.OwnerPart.Body : null);

                severed.Add(joint);
                joint.Break();

                if (_body == null || partner == null)
                {
                    continue;
                }

                Vector3 direction = _body.position - partner.position;
                float length = direction.magnitude;
                direction = length > 1e-4f ? direction / length : transform.forward;
                _body.AddForce(direction * charge, ForceMode.Impulse);
                partner.AddForce(-direction * charge, ForceMode.Impulse);
            }

            return severed;
        }
    }
}
