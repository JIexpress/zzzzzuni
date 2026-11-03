using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Domain.Interaction;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ZFrame.Data.Save;

namespace Game.Bootstrap
{
    /// <summary>
    /// The two <see cref="ISaveable"/> modules of the M4 vertical slice
    /// (TASK-A-M4-02): the shared <see cref="Inventory"/> and the player
    /// transform. Registered into <see cref="ISaveSystem"/> by
    /// <see cref="GameBootstrap"/> when SCN_Game activates and saved via the
    /// <see cref="SceneLoader"/> OnBeforeSceneUnload hook when the slice is
    /// left (回主菜单自动存档，slot 0；主菜单"继续"读档).
    /// </summary>
    public sealed class InventorySaveable : ISaveable
    {
        /// <summary>Stable save key of this module.</summary>
        public const string Key = "slice-inventory";

        private readonly Inventory _inventory;

        /// <summary>Creates the module over the live inventory instance.</summary>
        public InventorySaveable(Inventory inventory)
        {
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        }

        /// <inheritdoc />
        public string SaveKey => Key;

        /// <inheritdoc />
        public void Save(SaveWriter writer)
        {
            var items = new JArray();
            foreach (KeyValuePair<string, int> pair in _inventory.Items)
            {
                items.Add(new JObject
                {
                    ["id"] = pair.Key,
                    ["count"] = pair.Value
                });
            }

            writer.Set("items", items);
        }

        /// <inheritdoc />
        public void Load(SaveReader reader)
        {
            _inventory.Clear();
            if (!reader.TryGet("items", out JToken token) || !(token is JArray items))
            {
                return; // empty section (module added after the save was written)
            }

            foreach (JToken entry in items)
            {
                string id = entry?["id"]?.ToString();
                int count = entry?["count"]?.Value<int>() ?? 0;
                if (!string.IsNullOrEmpty(id) && count > 0)
                {
                    _inventory.Add(id, count);
                }
            }
        }
    }

    /// <summary>
    /// Player position module. On load it teleports the referenced transform
    /// (CharacterController tolerates a direct transform move as a teleport).
    /// </summary>
    public sealed class PlayerTransformSaveable : ISaveable
    {
        /// <summary>Stable save key of this module.</summary>
        public const string Key = "slice-player";

        private readonly Transform _target;

        /// <summary>Creates the module over the player transform (null tolerated: position is then saved as origin).</summary>
        public PlayerTransformSaveable(Transform target)
        {
            _target = target;
        }

        /// <inheritdoc />
        public string SaveKey => Key;

        /// <inheritdoc />
        public void Save(SaveWriter writer)
        {
            Vector3 position = _target != null ? _target.position : Vector3.zero;
            writer.Set("x", position.x);
            writer.Set("y", position.y);
            writer.Set("z", position.z);
        }

        /// <inheritdoc />
        public void Load(SaveReader reader)
        {
            if (_target == null)
            {
                return;
            }

            float x = reader.GetFloat("x");
            float y = reader.GetFloat("y");
            float z = reader.GetFloat("z");
            _target.position = new Vector3(x, y, z);
        }
    }
}
