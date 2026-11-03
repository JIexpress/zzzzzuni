using System.Collections.Generic;
using Game.Domain.Enemy;
using UnityEngine;
using ZFrame.Gameplay;

namespace Game.View.Enemy
{
    /// <summary>
    /// Engine-side shell of the reference enemy (TASK-A-M4-01). It owns one
    /// <see cref="EnemyFsm"/>, pumps the player position into it every frame,
    /// writes the resulting 2D position back onto the transform and — the part
    /// the task brief asks for — recolours the body and updates a small state
    /// label whenever <see cref="EnemyFsm.EnemyStateChanged"/> fires.
    ///
    /// All AI rules stay in the Domain; this class contains no thresholds of
    /// its own beyond the serialized tuning block it hands over.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyView : MonoBehaviour
    {
        [SerializeField] private string _enemyId = "enemy-1";
        [SerializeField] private Transform _player;
        [SerializeField] private Vector3[] _waypoints = new Vector3[0];
        [SerializeField] private float _chaseRange = 4f;
        [SerializeField] private float _loseRange = 6f;
        [SerializeField] private float _patrolSpeed = 1.5f;
        [SerializeField] private float _chaseSpeed = 3f;
        [SerializeField] private float _idleSeconds = 1f;
        [SerializeField] private Renderer _renderer;
        [SerializeField] private TextMesh _stateLabel;

        private EnemyFsm _fsm;

        /// <summary>The Domain brain (null before Awake).</summary>
        public EnemyFsm Fsm => _fsm;

        /// <summary>State the View is currently displaying.</summary>
        public EnemyStateId DisplayedState { get; private set; } = EnemyStateId.Idle;

        private void Awake()
        {
            if (_renderer == null)
            {
                _renderer = GetComponentInChildren<Renderer>();
            }

            EnsureLabel();

            var route = new List<Vec2>(_waypoints.Length);
            for (int i = 0; i < _waypoints.Length; i++)
            {
                route.Add(new Vec2(_waypoints[i].x, _waypoints[i].z));
            }

            var tuning = new EnemyTuning(_chaseRange, _loseRange, _patrolSpeed, _chaseSpeed, _idleSeconds);
            _fsm = new EnemyFsm(_enemyId, new Vec2(transform.position.x, transform.position.z), route, tuning);
            _fsm.EnemyStateChanged += OnEnemyStateChanged;

            ApplyVisual(_fsm.StateId); // the initial transition fired inside the constructor
        }

        private void OnDestroy()
        {
            if (_fsm != null)
            {
                _fsm.EnemyStateChanged -= OnEnemyStateChanged;
            }
        }

        private void Update()
        {
            if (_fsm == null)
            {
                return;
            }

            if (_player != null)
            {
                _fsm.SetPlayerPosition(new Vec2(_player.position.x, _player.position.z));
            }

            _fsm.Tick(Time.deltaTime);

            Vector3 position = transform.position;
            transform.position = new Vector3(_fsm.Position.X, position.y, _fsm.Position.Y);
        }

        private void OnEnemyStateChanged(EnemyStateChanged evt)
        {
            ApplyVisual(evt.Current);
        }

        private void ApplyVisual(EnemyStateId state)
        {
            DisplayedState = state;

            if (_renderer != null)
            {
                _renderer.material.color = ColorFor(state);
            }

            if (_stateLabel != null)
            {
                _stateLabel.text = state.ToString();
            }
        }

        private static Color ColorFor(EnemyStateId state)
        {
            switch (state)
            {
                case EnemyStateId.Patrol:
                    return new Color(0.95f, 0.8f, 0.2f);
                case EnemyStateId.Chase:
                    return new Color(0.9f, 0.2f, 0.2f);
                case EnemyStateId.Dead:
                    return new Color(0.15f, 0.15f, 0.15f);
                default:
                    return new Color(0.55f, 0.6f, 0.7f);
            }
        }

        private void EnsureLabel()
        {
            if (_stateLabel != null)
            {
                return;
            }

            var go = new GameObject("StateLabel");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.4f, 0f);

            _stateLabel = go.AddComponent<TextMesh>();
            _stateLabel.anchor = TextAnchor.MiddleCenter;
            _stateLabel.alignment = TextAlignment.Center;
            _stateLabel.fontSize = 48;
            _stateLabel.characterSize = 0.1f;
            _stateLabel.color = Color.white;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                _stateLabel.font = font;
                var meshRenderer = go.GetComponent<MeshRenderer>();
                if (meshRenderer != null)
                {
                    meshRenderer.sharedMaterial = font.material;
                }
            }
        }
    }
}
