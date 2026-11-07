using System;
using System.Collections.Generic;
using Game.Domain.AI.Utility;
using UnityEngine;
using ZFrame.Gameplay;

namespace Game.View.Npc
{
    /// <summary>
    /// Engine-side shell of a utility-driven NPC (TASK-A-TECH-01), the
    /// EnemyView counterpart for the new brain: owns one
    /// <see cref="NpcUtilityAgent"/>, pumps the player position and the
    /// nearest surviving drop into it every frame, writes the agent's plane
    /// position back onto the transform, and recolours the body plus a small
    /// state label whenever <see cref="NpcUtilityAgent.ActionChosen"/> fires.
    ///
    /// All decisions stay in the Domain (utility ranking over the pumped
    /// <see cref="NPCWorldState"/>); this class holds no thresholds of its own
    /// beyond the serialized start state (fatigue, personality bias) it hands
    /// over. The scene keeps three of these at the SAME spawn point with
    /// different biases/start fatigue so their paths visibly diverge.
    ///
    /// <see cref="ManualDrive"/> lets tests step the agent deterministically
    /// (<see cref="Step"/>) without the frame-rate-dependent Update path.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NpcUtilityView : MonoBehaviour
    {
        /// <summary>Personality presets of the demo scene (desire weight biases).</summary>
        public enum NpcBias
        {
            /// <summary>All desires weight 1.</summary>
            Balanced = 0,

            /// <summary>Rest desire 1.6x — a tired NPC.</summary>
            Restful = 1,

            /// <summary>Pickup desire 1.6x — a collector.</summary>
            Collector = 2,

            /// <summary>Patrol desire 3x — a wayfarer.</summary>
            Wayfarer = 3,
        }

        [SerializeField] private string _npcId = "npc-a";
        [SerializeField] private Transform _player;
        [SerializeField] private Vector3 _restPoint = new Vector3(-6f, 0f, -4f);
        [SerializeField] private Vector3[] _waypoints = new Vector3[0];
        [SerializeField] private Transform[] _drops = new Transform[0];
        [SerializeField] private float _startFatigue = 0f;
        [SerializeField] private float _fatiguePerSecond = 0.02f;
        [SerializeField] private NpcBias _bias = NpcBias.Balanced;
        [SerializeField] private Renderer _renderer;
        [SerializeField] private TextMesh _stateLabel;

        private NpcUtilityAgent _agent;
        private readonly List<string> _selectionHistory = new List<string>();

        /// <summary>The Domain brain (built on demand; null before first use).</summary>
        public NpcUtilityAgent Agent => _agent;

        /// <summary>Stable identity the label shows.</summary>
        public string NpcId => _npcId;

        /// <summary>Action the View is currently displaying.</summary>
        public string DisplayedAction { get; private set; } = UtilityBrain.IdleActionName;

        /// <summary>Every distinct action chosen so far, in order (autonomy evidence for tests).</summary>
        public IReadOnlyList<string> SelectionHistory => _selectionHistory;

        /// <summary>When true, Update() does nothing and tests drive <see cref="Step"/> deterministically.</summary>
        public bool ManualDrive { get; set; }

        private void Awake()
        {
            EnsureAgent();
        }

        private void Update()
        {
            if (ManualDrive)
            {
                return;
            }

            Step(Time.deltaTime);
        }

        /// <summary>
        /// Ensures the Domain agent exists (idempotent — PlayMode reloads and
        /// edit-mode tooling may skip Awake). Builds tuning from the serialized
        /// start state and subscribes the visual callbacks.
        /// </summary>
        public void EnsureAgent()
        {
            if (_agent != null)
            {
                return;
            }

            var tuning = new NpcUtilityAgent.Tuning { FatiguePerSecond = _fatiguePerSecond };
            var route = new List<Vec2>(_waypoints.Length);
            for (int i = 0; i < _waypoints.Length; i++)
            {
                route.Add(new Vec2(_waypoints[i].x, _waypoints[i].z));
            }

            _agent = new NpcUtilityAgent(
                _npcId,
                new Vec2(transform.position.x, transform.position.z),
                new Vec2(_restPoint.x, _restPoint.z),
                route,
                tuning,
                PersonalityFor(_bias));
            _agent.SetFatigue(_startFatigue);
            _agent.ActionChosen += OnActionChosen;
            _agent.DropCollected += OnDropCollected;

            if (_renderer == null)
            {
                _renderer = GetComponentInChildren<Renderer>();
            }

            EnsureLabel();
            ApplyVisual(DisplayedAction);
        }

        /// <summary>
        /// Deterministic tick for tests: pumps the world, advances the agent
        /// by <paramref name="deltaTime"/> and applies the resulting transform.
        /// </summary>
        public string Step(float deltaTime)
        {
            EnsureAgent();
            PumpWorld();
            string chosen = _agent.Tick(deltaTime);
            ApplyTransform();
            return chosen;
        }

        private void OnDestroy()
        {
            if (_agent != null)
            {
                _agent.ActionChosen -= OnActionChosen;
                _agent.DropCollected -= OnDropCollected;
            }
        }

        private void PumpWorld()
        {
            if (_player != null)
            {
                _agent.SetPlayerPosition(new Vec2(_player.position.x, _player.position.z));
            }

            Transform nearest = null;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < _drops.Length; i++)
            {
                Transform drop = _drops[i];
                if (drop == null || !drop.gameObject.activeInHierarchy)
                {
                    continue;
                }

                float distance = Vector3.SqrMagnitude(drop.position - transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = drop;
                }
            }

            _agent.SetDropTarget(nearest != null ? new Vec2(nearest.position.x, nearest.position.z) : (Vec2?)null);
        }

        private void ApplyTransform()
        {
            Vector3 position = transform.position;
            transform.position = new Vector3(_agent.Position.X, position.y, _agent.Position.Y);
        }

        private void OnActionChosen(string action)
        {
            _selectionHistory.Add(action);
            ApplyVisual(action);
        }

        private void OnDropCollected(Vec2 position)
        {
            for (int i = 0; i < _drops.Length; i++)
            {
                Transform drop = _drops[i];
                if (drop == null || !drop.gameObject.activeInHierarchy)
                {
                    continue;
                }

                float dx = drop.position.x - position.X;
                float dz = drop.position.z - position.Y;
                if ((dx * dx) + (dz * dz) <= 0.64f)
                {
                    drop.gameObject.SetActive(false);
                    return;
                }
            }
        }

        private void ApplyVisual(string action)
        {
            DisplayedAction = action;

            if (_renderer != null)
            {
                _renderer.material.color = ColorFor(action);
            }

            if (_stateLabel != null)
            {
                _stateLabel.text = $"{_npcId}:{action}";
            }
        }

        private static NpcUtilityAgent.Personality PersonalityFor(NpcBias bias)
        {
            switch (bias)
            {
                case NpcBias.Restful: return new NpcUtilityAgent.Personality { Rest = 1.6f };
                case NpcBias.Collector: return new NpcUtilityAgent.Personality { Pickup = 1.6f };
                case NpcBias.Wayfarer: return new NpcUtilityAgent.Personality { Patrol = 3f };
                default: return new NpcUtilityAgent.Personality();
            }
        }

        private static Color ColorFor(string action)
        {
            switch (action)
            {
                case NpcUtilityAgent.RestActionName: return new Color(0.25f, 0.5f, 0.95f);
                case NpcUtilityAgent.PickupActionName: return new Color(0.95f, 0.55f, 0.1f);
                case NpcUtilityAgent.FollowActionName: return new Color(0.2f, 0.85f, 0.9f);
                case NpcUtilityAgent.PatrolActionName: return new Color(0.95f, 0.8f, 0.2f);
                default: return new Color(0.55f, 0.6f, 0.7f);
            }
        }

        private void EnsureLabel()
        {
            if (_stateLabel != null)
            {
                return;
            }

            var host = new GameObject("StateLabel");
            host.transform.SetParent(transform, false);
            host.transform.localPosition = new Vector3(0f, 1.4f, 0f);

            _stateLabel = host.AddComponent<TextMesh>();
            _stateLabel.anchor = TextAnchor.MiddleCenter;
            _stateLabel.alignment = TextAlignment.Center;
            _stateLabel.fontSize = 48;
            _stateLabel.characterSize = 0.1f;
            _stateLabel.color = Color.white;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                _stateLabel.font = font;
                var meshRenderer = host.GetComponent<MeshRenderer>();
                if (meshRenderer != null)
                {
                    meshRenderer.sharedMaterial = font.material;
                }
            }
        }
    }
}
