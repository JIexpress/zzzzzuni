using System;
using Game.Domain.AI.Utility;
using Game.Domain.Interaction;
using ZFrame.Gameplay;

namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// Trigger-source wiring of the memory system (TASK-A-AI-01, brief item 4):
    /// converts EXISTING gameplay events into memory writes on one NPC's
    /// <see cref="NpcMemory"/>, so "被玩家攻击过" actually leaves a trace and
    /// friendly player activity builds trust. Pure domain — the owner (scene
    /// controller / View / bus subscriber) forwards the event structs; this
    /// class only does the sight test and the stamping:
    ///
    /// - <see cref="Observe(EnemyAttackRequested)"/>: an enemy swing
    ///   (<c>EnemyAttackRequested</c>, fired by the behaviour-tree enemy)
    ///   inside the NPC's sight radius ⇒ one <see cref="MemoryEventType.SawDeath"/>
    ///   episode attributed to the enemy — witnessed violence feeds fear.
    /// - <see cref="Observe(ItemPickedUp)"/>: a player pickup
    ///   (<c>ItemPickedUp</c>) inside the sight radius ⇒ one
    ///   <see cref="MemoryEventType.PlayerNear"/> episode attributed to the
    ///   picker — 信任微增 (small gain by profile design).
    ///
    /// Receiving an attack DIRECTLY (the player hits THIS NPC — the 受伤 hook)
    /// is one call on the agent itself: <see cref="NpcUtilityAgent.NoteAttackedBy"/>.
    /// Timestamps are stamped from the agent's own clock, so decay stays in one
    /// deterministic time domain.
    /// </summary>
    public sealed class NpcMemoryFeed
    {
        /// <summary>Default sight radius (metres on the 2D plane) for witnessed events.</summary>
        public const float DefaultSightRadius = 12f;

        /// <summary>Valence of one witnessed enemy swing (bad, but less personal than being hit).</summary>
        public const float SawDeathValence = -0.8f;

        private readonly NpcUtilityAgent _agent;
        private readonly float _sightRadius;

        /// <summary>
        /// Creates a feed for one agent. <paramref name="sightRadius"/> must be
        /// > 0 (an NPC that sees nothing is a wiring bug, fail fast).
        /// </summary>
        public NpcMemoryFeed(NpcUtilityAgent agent, float sightRadius = DefaultSightRadius)
        {
            if (agent == null)
            {
                throw new ArgumentNullException(nameof(agent));
            }

            if (sightRadius <= 0f || float.IsNaN(sightRadius) || float.IsInfinity(sightRadius))
            {
                throw new ArgumentOutOfRangeException(nameof(sightRadius), "NpcMemoryFeed: sight radius must be > 0.");
            }

            _agent = agent;
            _sightRadius = sightRadius;
        }

        /// <summary>The wired agent (memory owner).</summary>
        public NpcUtilityAgent Agent => _agent;

        /// <summary>Sight radius in metres (events farther away are not perceived).</summary>
        public float SightRadius => _sightRadius;

        /// <summary>
        /// Feeds one enemy swing: when it lands within sight, the NPC remembers
        /// witnessing violence (<see cref="MemoryEventType.SawDeath"/>,
        /// valence <see cref="SawDeathValence"/>, attributed to the enemy id).
        /// Out of sight ⇒ no memory (returns false).
        /// </summary>
        public bool Observe(EnemyAttackRequested attack)
        {
            Vec2 enemyPosition = attack.EnemyPosition;
            if (Vec2.Distance(_agent.Position, enemyPosition) > _sightRadius)
            {
                return false;
            }

            _agent.NoteSawDeath(new EntityRef(attack.EnemyId), SawDeathValence);
            return true;
        }

        /// <summary>
        /// Feeds one pickup: when the player picks something up within sight,
        /// the NPC remembers friendly presence
        /// (<see cref="MemoryEventType.PlayerNear"/>, valence +1, attributed to
        /// the actor) — the trust micro-increase. Out of sight ⇒ no memory
        /// (returns false).
        /// </summary>
        public bool Observe(ItemPickedUp pickup)
        {
            if (pickup.Actor.IsNone)
            {
                return false;
            }

            Vec2 position = pickup.Position;
            if (Vec2.Distance(_agent.Position, position) > _sightRadius)
            {
                return false;
            }

            _agent.NotePlayerNear(EntityRef.FromActor(pickup.Actor), 1f);
            return true;
        }
    }
}
