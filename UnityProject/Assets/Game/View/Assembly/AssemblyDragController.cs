using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Player-facing grab/drag/release loop of the assembly demo
    /// (TASK-A-TECH-02), built in the InteractionScanner pattern: a per-frame
    /// ray from the camera picks the <see cref="AssemblyPart"/> under the
    /// cursor, the held part is steered by velocity (no direct transform
    /// teleporting while physics is live), a snap candidate is highlighted
    /// through <see cref="AssemblyPart.SetHighlighted"/> and release snaps
    /// via <see cref="AssemblyBuilder.Connect"/>.
    ///
    /// Controls: hold LMB to grab/drag (WASD nudges the held target in the
    /// camera plane, Space/Shift raise/lower it), release LMB to drop —
    /// onto a snap candidate = connect. F fires every thruster for its
    /// default burn, X breaks the held part's bonds (拆卸).
    ///
    /// Input note: legacy Input is used on purpose — the project's Active
    /// Input Handling is "Both" (ProjectSettings.asset activeInputHandler: 2),
    /// and batchmode runs never inject input anyway; tests drive the public
    /// TryGrab/UpdateDragTarget/TryRelease methods directly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AssemblyDragController : MonoBehaviour
    {
        [SerializeField] private Camera _camera;
        [SerializeField] private float _maxGrabDistance = 10f;
        [SerializeField] private float _snapRange = 0.75f;
        [SerializeField] private float _maxDragSpeed = 5f;
        [SerializeField] private float _dragStiffness = 12f;
        [SerializeField] private float _keyboardNudgeSpeed = 2.5f;
        [SerializeField] private KeyCode _fireKey = KeyCode.F;
        [SerializeField] private KeyCode _breakKey = KeyCode.X;

        /// <summary>Part currently held by the player, or null.</summary>
        public AssemblyPart HeldPart { get; private set; }

        /// <summary>World-space point the held part is steered towards.</summary>
        public Vector3 DragTarget { get; private set; }

        /// <summary>Free docking point the held part would snap to, or null.</summary>
        public AttachmentPoint SnapCandidatePoint { get; private set; }

        private Plane _dragPlane;
        private AssemblyPart _highlightedPart;

        private void Awake()
        {
            if (_camera == null)
            {
                _camera = Camera.main;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(_fireKey))
            {
                FireAllThrusters();
            }

            if (Input.GetKeyDown(_breakKey))
            {
                BreakHeldJoints();
            }

            if (Input.GetMouseButtonDown(0))
            {
                TryGrab(Input.mousePosition);
            }

            if (HeldPart != null)
            {
                if (Input.GetMouseButton(0))
                {
                    UpdateDragTargetFromMouse(Input.mousePosition);
                    ApplyKeyboardNudge();
                    RefreshSnapCandidate();
                }
                else
                {
                    TryRelease();
                }
            }
        }

        private void FixedUpdate()
        {
            AssemblyPart held = HeldPart;
            if (held == null || held.Body == null)
            {
                return;
            }

            Vector3 toTarget = DragTarget - held.Body.position;
            Vector3 velocity = toTarget * _dragStiffness;
            if (velocity.magnitude > _maxDragSpeed)
            {
                velocity = velocity.normalized * _maxDragSpeed;
            }

            held.Body.velocity = velocity;
            held.Body.angularVelocity *= 0.9f; // keep the piece from spinning up on contact
        }

        /// <summary>
        /// Grabs the topmost <see cref="AssemblyPart"/> under the given screen
        /// position (within <see cref="_maxGrabDistance"/>). Returns false when
        /// nothing was hit or a part is already held.
        /// </summary>
        public bool TryGrab(Vector3 screenPosition)
        {
            if (HeldPart != null || _camera == null)
            {
                return false;
            }

            Ray ray = _camera.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, _maxGrabDistance))
            {
                return false;
            }

            AssemblyPart part = hit.rigidbody != null
                ? hit.rigidbody.GetComponent<AssemblyPart>()
                : hit.collider.GetComponentInParent<AssemblyPart>();
            if (part == null || part.Body == null)
            {
                return false;
            }

            HeldPart = part;
            _dragPlane = new Plane(-ray.direction, hit.point);
            DragTarget = hit.point;
            part.Body.useGravity = false;
            return true;
        }

        /// <summary>Moves the drag target to the cursor ray on the grab plane.</summary>
        public void UpdateDragTargetFromMouse(Vector3 screenPosition)
        {
            if (_camera == null)
            {
                return;
            }

            Ray ray = _camera.ScreenPointToRay(screenPosition);
            if (_dragPlane.Raycast(ray, out float enter))
            {
                DragTarget = ray.GetPoint(enter);
            }
        }

        /// <summary>Shifts the drag target in world axes (keyboard nudge).</summary>
        public void NudgeDragTarget(Vector3 worldDelta)
        {
            DragTarget += worldDelta;
        }

        /// <summary>
        /// Re-evaluates the snap candidate for the held part and mirrors it
        /// onto the preview highlight. Returns true when a candidate exists.
        /// </summary>
        public bool RefreshSnapCandidate()
        {
            AssemblyPart held = HeldPart;
            if (held == null)
            {
                ClearSnapCandidate();
                return false;
            }

            bool found = AssemblyBuilder.TryFindSnap(
                held.Body.position, new AssemblyBuilder.AssemblyPartTypeFilter(held, null), _snapRange,
                out AssemblyBuilder.SnapCandidate candidate);

            SetSnapCandidate(found ? candidate.Point : null);
            return found;
        }

        /// <summary>
        /// Drops the held part; when a snap candidate is live the part is
        /// aligned and bonded onto it. Returns the created joint, or null.
        /// </summary>
        public AssemblyJoint TryRelease()
        {
            AssemblyPart held = HeldPart;
            HeldPart = null;
            if (held != null && held.Body != null)
            {
                held.Body.useGravity = true;
            }

            // Capture first, then clear (clearing nulls SnapCandidatePoint).
            AttachmentPoint target = SnapCandidatePoint;
            ClearSnapCandidate();
            if (held == null || target == null || target.IsOccupied)
            {
                return null;
            }

            AttachmentPoint mine = held.FindNearestFreePoint(target.WorldPosition, _snapRange, target.Type);
            if (mine == null)
            {
                return null;
            }

            return AssemblyBuilder.Connect(held, mine, target);
        }

        private void ApplyKeyboardNudge()
        {
            Vector3 nudge = Vector3.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
            {
                nudge += Vector3.forward;
            }

            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
            {
                nudge += Vector3.back;
            }

            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
            {
                nudge += Vector3.left;
            }

            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
            {
                nudge += Vector3.right;
            }

            if (Input.GetKey(KeyCode.Space))
            {
                nudge += Vector3.up;
            }

            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                nudge += Vector3.down;
            }

            if (nudge.sqrMagnitude > 0f)
            {
                NudgeDragTarget(nudge.normalized * (_keyboardNudgeSpeed * Time.deltaTime));
            }
        }

        private void SetSnapCandidate(AttachmentPoint point)
        {
            if (SnapCandidatePoint == point)
            {
                return;
            }

            SnapCandidatePoint = point;
            if (_highlightedPart != null && (point == null || _highlightedPart != point.Owner))
            {
                _highlightedPart.SetHighlighted(false);
                _highlightedPart = null;
            }

            if (point != null && point.Owner != null)
            {
                _highlightedPart = point.Owner;
                _highlightedPart.SetHighlighted(true);
            }
        }

        private void ClearSnapCandidate()
        {
            SetSnapCandidate(null);
        }

        private void FireAllThrusters()
        {
            Object[] thrusters = FindObjectsOfType(typeof(AssemblyThruster));
            for (int i = 0; i < thrusters.Length; i++)
            {
                ((AssemblyThruster)thrusters[i]).Fire();
            }
        }

        private void BreakHeldJoints()
        {
            AssemblyPart held = HeldPart;
            if (held == null)
            {
                return;
            }

            for (int i = held.Joints.Count - 1; i >= 0; i--)
            {
                AssemblyBuilder.Disconnect(held.Joints[i]);
            }
        }
    }
}
