using System.Collections.Generic;
using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// One buildable piece of the modular assembly slice (TASK-A-TECH-02): a
    /// dynamic rigid body carrying any number of <see cref="AttachmentPoint"/>
    /// children plus its visual highlight. Parts register themselves in a
    /// static registry on enable so <see cref="AssemblyBuilder"/> can search
    /// snap candidates without scene-wide scans; the registry is play-mode
    /// only (edit mode never raises OnEnable).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class AssemblyPart : MonoBehaviour
    {
        private static readonly List<AssemblyPart> Registered = new List<AssemblyPart>();
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorId = Shader.PropertyToID("_Color");
        private static readonly Color HighlightColor = new Color(1f, 0.82f, 0.25f, 1f);

        [SerializeField] private string _partId = "part";
        [SerializeField] private Color _baseColor = new Color(0.72f, 0.72f, 0.75f, 1f);

        private readonly List<AttachmentPoint> _points = new List<AttachmentPoint>();
        private readonly List<AssemblyJoint> _joints = new List<AssemblyJoint>();
        private Rigidbody _body;
        private MeshRenderer _renderer;
        private Material _material;
        private Color _colorBeforeHighlight;
        private bool _highlighted;

        /// <summary>Every enabled part in the play-mode scene.</summary>
        public static IReadOnlyList<AssemblyPart> AllParts => Registered;

        /// <summary>Stable identifier (diagnostics, demo HUD, tests).</summary>
        public string PartId => _partId;

        /// <summary>The rigid body of this piece.</summary>
        public Rigidbody Body
        {
            get
            {
                if (_body == null)
                {
                    _body = GetComponent<Rigidbody>();
                }

                return _body;
            }
        }

        /// <summary>Connection points currently on this part (children, cached).</summary>
        public IReadOnlyList<AttachmentPoint> Points
        {
            get
            {
                if (_points.Count == 0)
                {
                    CollectPoints();
                }

                return _points;
            }
        }

        /// <summary>Bonds attached to this part (managed by <see cref="AssemblyJoint"/>).</summary>
        public IReadOnlyList<AssemblyJoint> Joints => _joints;

        /// <summary>True while the snap preview highlight is on.</summary>
        public bool IsHighlighted => _highlighted;

        /// <summary>Current base colour of the visual material.</summary>
        public Color CurrentColor
        {
            get
            {
                Material material = EnsureMaterial();
                if (material == null)
                {
                    return _baseColor;
                }

                return material.HasProperty(BaseColorId)
                    ? material.GetColor(BaseColorId)
                    : material.GetColor(LegacyColorId);
            }
        }

        private void Awake()
        {
            // Base colour is applied lazily through EnsureMaterial; Awake only
            // caches the body so physics consumers can start immediately.
            _ = Body;
        }

        private void OnEnable()
        {
            Registered.Add(this);
        }

        private void OnDisable()
        {
            Registered.Remove(this);
        }

        /// <summary>Re-scans child attachment points (call after manual hierarchy edits).</summary>
        public void RefreshPoints()
        {
            CollectPoints();
        }

        /// <summary>
        /// Creates a child point at <paramref name="localPosition"/> whose
        /// forward (outward face normal) is <paramref name="localForward"/>,
        /// with a canonical up vector so docking alignment is deterministic.
        /// </summary>
        public AttachmentPoint AddAttachmentPoint(AttachmentPointType type, Vector3 localPosition, Vector3 localForward)
        {
            var node = new GameObject($"Attach_{type}");
            node.transform.SetParent(transform, false);
            node.transform.localPosition = localPosition;
            node.transform.localRotation = Quaternion.LookRotation(
                localForward.normalized, AssemblyBuilder.PickPerpendicularUp(localForward));

            var point = node.AddComponent<AttachmentPoint>();
            point.SetType(type);
            _points.Add(point);
            return point;
        }

        /// <summary>
        /// Nearest FREE point of this part whose type is compatible with
        /// <paramref name="wanted"/> within <paramref name="maxDistance"/> of
        /// <paramref name="worldPosition"/>, or null.
        /// </summary>
        public AttachmentPoint FindNearestFreePoint(Vector3 worldPosition, float maxDistance, AttachmentPointType? wanted)
        {
            AttachmentPoint best = null;
            float bestSqr = maxDistance * maxDistance;
            IReadOnlyList<AttachmentPoint> points = Points;
            for (int i = 0; i < points.Count; i++)
            {
                AttachmentPoint point = points[i];
                if (point.IsOccupied
                    || (wanted.HasValue && !AttachmentPoint.AreCompatible(point.Type, wanted.Value)))
                {
                    continue;
                }

                float sqr = (point.WorldPosition - worldPosition).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = point;
                }
            }

            return best;
        }

        /// <summary>
        /// Tints the visual material with the snap-preview highlight colour
        /// and restores the previous colour on unhighlight (idempotent).
        /// </summary>
        public void SetHighlighted(bool highlighted)
        {
            Material material = EnsureMaterial();
            if (material == null)
            {
                return;
            }

            if (highlighted == _highlighted)
            {
                return;
            }

            int id = material.HasProperty(BaseColorId) ? BaseColorId : LegacyColorId;
            if (highlighted)
            {
                _colorBeforeHighlight = material.GetColor(id);
                material.SetColor(id, HighlightColor);
            }
            else
            {
                material.SetColor(id, _colorBeforeHighlight);
            }

            _highlighted = highlighted;
        }

        internal void BindJoint(AssemblyJoint joint)
        {
            if (!_joints.Contains(joint))
            {
                _joints.Add(joint);
            }
        }

        internal void UnbindJoint(AssemblyJoint joint)
        {
            _joints.Remove(joint);
        }

        private void CollectPoints()
        {
            _points.Clear();
            GetComponentsInChildren(true, _points);
        }

        private Material EnsureMaterial()
        {
            if (_material == null)
            {
                _renderer = _renderer != null ? _renderer : GetComponentInChildren<MeshRenderer>();
                if (_renderer == null)
                {
                    return null;
                }

                // Instancing the material keeps highlights per-part (the
                // primitive's default material is shared scene-wide).
                _material = _renderer.material;
            }

            return _material;
        }
    }
}
