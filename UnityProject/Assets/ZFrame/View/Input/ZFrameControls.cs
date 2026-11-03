using System;
using UnityEngine;
using UnityEngine.InputSystem;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Committed convenience wrapper around the committed
    /// <c>ZFrameControls.inputactions</c> asset (TASK-A-M3-03) — the
    /// hand-maintained equivalent of the Input System editor's "Generate C#
    /// Class" output, minus its embedded JSON copy: <see cref="FromResources"/>
    /// loads the committed asset from Resources and CLONES it through
    /// <c>InputActionAsset.FromJson(ToJson())</c>, so the .inputactions file
    /// stays the single source of truth (no driftable duplicate) and binding
    /// overrides applied at runtime never touch the shared imported asset.
    ///
    /// Action maps (mirrors the asset):
    /// - "Player": Move (WASD composite / leftStick), Interact (E /
    ///   ButtonSouth), Pause (Esc / Start) — gated by <see cref="PlayerInputGate"/>.
    /// - "UI": Navigate / Submit / Cancel / Point / Click with default-style
    ///   bindings — kept always-enabled for the future
    ///   InputSystemUIInputModule migration (A-M4+).
    /// </summary>
    public sealed class ZFrameControls : IDisposable
    {
        /// <summary>Resource key of the committed asset (Assets/ZFrame/View/Input/Resources).</summary>
        public const string AssetResourceKey = "ZFrameControls";

        /// <summary>Action map that carries gameplay input (gated while <see cref="InputBlocker"/> is active).</summary>
        public const string PlayerMapName = "Player";

        /// <summary>Action map for UI navigation (always enabled).</summary>
        public const string UiMapName = "UI";

        private readonly bool _ownsAsset;

        /// <summary>The wrapped asset (a private clone when created via <see cref="FromResources"/>).</summary>
        public InputActionAsset Asset { get; }

        /// <summary>Player action map (Move/Interact/Pause).</summary>
        public InputActionMap Player => Asset.FindActionMap(PlayerMapName, throwIfNotFound: true);

        /// <summary>UI action map (Navigate/Submit/Cancel/Point/Click).</summary>
        public InputActionMap UI => Asset.FindActionMap(UiMapName, throwIfNotFound: true);

        /// <summary>Player/Move — WASD composite + left stick.</summary>
        public InputAction Move => Player.FindAction("Move", throwIfNotFound: true);

        /// <summary>Player/Interact — E + gamepad ButtonSouth.</summary>
        public InputAction Interact => Player.FindAction("Interact", throwIfNotFound: true);

        /// <summary>Player/Pause — Esc + gamepad Start.</summary>
        public InputAction Pause => Player.FindAction("Pause", throwIfNotFound: true);

        /// <summary>UI/Navigate.</summary>
        public InputAction Navigate => UI.FindAction("Navigate", throwIfNotFound: true);

        /// <summary>UI/Submit.</summary>
        public InputAction Submit => UI.FindAction("Submit", throwIfNotFound: true);

        /// <summary>UI/Cancel.</summary>
        public InputAction Cancel => UI.FindAction("Cancel", throwIfNotFound: true);

        /// <summary>UI/Point.</summary>
        public InputAction Point => UI.FindAction("Point", throwIfNotFound: true);

        /// <summary>UI/Click.</summary>
        public InputAction Click => UI.FindAction("Click", throwIfNotFound: true);

        private ZFrameControls(InputActionAsset asset, bool ownsAsset)
        {
            Asset = asset ?? throw new ArgumentNullException(nameof(asset));
            _ownsAsset = ownsAsset;
        }

        /// <summary>Wraps an externally owned asset (caller keeps disposal responsibility — tests use this).</summary>
        public static ZFrameControls Wrap(InputActionAsset asset)
        {
            return new ZFrameControls(asset, ownsAsset: false);
        }

        /// <summary>
        /// Loads the committed asset from Resources and returns a wrapper over
        /// a CLONE. Returns null (with an error log) when the asset is missing
        /// — callers degrade gracefully instead of throwing at boot.
        /// </summary>
        public static ZFrameControls FromResources()
        {
            InputActionAsset template = Resources.Load<InputActionAsset>(AssetResourceKey);
            if (template == null)
            {
                ZLog.Error("Input", $"ZFrameControls: asset '{AssetResourceKey}' not found in Resources — input disabled.");
                return null;
            }

            return new ZFrameControls(InputActionAsset.FromJson(template.ToJson()), ownsAsset: true);
        }

        /// <summary>Enables every action map (convenience; prefer <see cref="PlayerInputGate"/> for the Player map).</summary>
        public void Enable()
        {
            Asset.Enable();
        }

        /// <summary>Disables every action map.</summary>
        public void Disable()
        {
            Asset.Disable();
        }

        /// <summary>Disables and, when the wrapper owns its clone, destroys the asset instance.</summary>
        public void Dispose()
        {
            Asset.Disable();
            if (_ownsAsset)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(Asset);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(Asset);
                }
            }
        }
    }
}
