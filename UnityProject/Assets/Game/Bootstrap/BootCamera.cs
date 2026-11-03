using UnityEngine;

namespace Game.Bootstrap
{
    /// <summary>
    /// Runtime-created presentation camera for the boot scene (TASK-A-AND-03).
    ///
    /// ROOT CAUSE this type fixes: SCN_Boot contains no camera at all. A URP
    /// player renders exactly nothing without a camera — not even the
    /// Screen-Space-Overlay UGUI that the main menu lives on — so no frame is
    /// ever presented: the device shows the stale startup buffer (uniform
    /// dark gray) while the whole managed boot chain completes behind it
    /// (proven by the zframe_boot.log step markers: menu on stack at +3.45s,
    /// zero frames on screen). The editor Game view tolerates this
    /// (overlay UI still draws), which is why the symptom only appeared in
    /// the Android/Windows players.
    ///
    /// The camera is a presentation anchor only: solid-color clear, nothing
    /// to cull-render (the boot scene carries no visual content). It is a
    /// scene object of the SCN_Boot instance that created it, so it dies
    /// automatically when SCN_Game (with its own MainCamera) loads, and
    /// <see cref="Ensure"/> is idempotent for every path that re-enters the
    /// boot scene (Awake main/duplicate branches, SceneLoadedEvent).
    /// </summary>
    public static class BootCamera
    {
        /// <summary>GameObject name of the runtime camera (diagnostics).</summary>
        public const string CameraName = "~BootClearCamera";

        /// <summary>Clear color shown behind the menu (the classic boot gray).</summary>
        public static readonly Color ClearColor = new Color(0.106f, 0.106f, 0.106f, 1f);

        /// <summary>The live boot camera (null when a scene camera already exists).</summary>
        public static Camera Instance { get; private set; }

        /// <summary>
        /// Guarantees the active scene can present: creates the solid-clear
        /// boot camera only when the scene has none. Safe to call repeatedly.
        /// </summary>
        public static void Ensure()
        {
            if (Instance != null)
            {
                return; // already provided by us
            }

            if (Object.FindObjectOfType<Camera>() != null)
            {
                return; // a real scene camera exists — nothing to do
            }

            var host = new GameObject(CameraName);
            Instance = host.AddComponent<Camera>();
            Instance.clearFlags = CameraClearFlags.SolidColor;
            Instance.backgroundColor = ClearColor;
            Instance.depth = -100f; // presentation anchor: always behind any real camera
        }

        /// <summary>Forgets the destroyed camera reference (tests / scene teardown).</summary>
        public static void Forget()
        {
            Instance = null;
        }
    }
}
