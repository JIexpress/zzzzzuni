using System;
using System.Collections.Generic;
using Game.Domain.Enemy;
using Game.Domain.Interaction;
using Game.View.Enemy;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.UI
{
    /// <summary>
    /// Gameplay HUD of the vertical slice (TASK-A-M4-02): two UGUI captions on
    /// the <see cref="UIRoot"/> HUD layer canvas —
    ///  - pickup counter: subscribes <see cref="ItemPickedUp"/> on the
    ///    <see cref="EventBus"/> (fired by the Domain pickup template) and
    ///    mirrors the shared inventory total;
    ///  - enemy hint: subscribes every <see cref="EnemyView"/>'s Domain FSM
    ///    state-changed event and reports patrol/chase/death ("守卫已被击败").
    ///
    /// The host GameObject lives in the gameplay scene (NOT
    /// DontDestroyOnLoad), so the HUD dies with the scene; the flow owner
    /// creates it on SCN_Game load and simply forgets it on unload. The two
    /// captions hang on the persistent <see cref="UIRoot"/> canvas instead, so
    /// <see cref="Shutdown"/> destroys them explicitly (TASK-H-01: otherwise
    /// every scene visit leaks two Texts and leaves stale text in the next
    /// scene). In play mode <c>OnDestroy</c> forwards to <see cref="Shutdown"/>;
    /// EditMode does not drive MonoBehaviour OnDestroy (A-M3-02 seam precedent:
    /// GameRoot.Initialize/Shutdown, AudioService.Shutdown), so tests call
    /// <see cref="Shutdown"/> directly (TASK-H-06).
    ///
    /// Plain UGUI captions, deliberately outside the data-driven settings
    /// framework (M3 UI 框架的面板栈之外的最简 HUD，与 M4-01 的世界空间提示
    /// 同一取舍；简报允许"订阅事件刷新文本"的最简实现).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameHud : MonoBehaviour
    {
        private Text _pickupText;
        private Text _enemyText;
        private TouchControlsView _touchControls;
        private readonly List<EnemyView> _enemies = new List<EnemyView>();
        private readonly List<IDisposable> _tokens = new List<IDisposable>();
        private bool _shutdown;

        /// <summary>Builds the captions and wires the subscriptions. Call once after spawn.</summary>
        public void Build(UIRoot root)
        {
            Canvas hud = root.GetLayerCanvas(UIConsts.UILayer.HUD);
            if (hud == null)
            {
                ZLog.Warn("Game", "GameHud: HUD canvas missing — HUD not built.");
                return;
            }

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _pickupText = CreateCaption(hud.transform, "PickupCounter", font, new Vector2(24f, -24f), TextAnchor.UpperLeft);
            _enemyText = CreateCaption(hud.transform, "EnemyHint", font, new Vector2(24f, -72f), TextAnchor.UpperLeft);

            _pickupText.text = "拾取计数：0";
            _enemyText.text = string.Empty;

            _tokens.Add(EventBus.Subscribe<ItemPickedUp>(OnItemPickedUp));

            foreach (EnemyView enemy in FindObjectsOfType<EnemyView>())
            {
                if (enemy.Fsm == null)
                {
                    continue;
                }

                _enemies.Add(enemy);
                System.Action<EnemyStateChanged> handler = OnEnemyStateChanged;
                enemy.Fsm.EnemyStateChanged += handler;
                _tokens.Add(new UnsubscribeHook(() => enemy.Fsm.EnemyStateChanged -= handler));
            }

            // TASK-A-AND-04: on-screen stick + action buttons for touch hardware.
            // Spawn returns null on desktop (nothing is created — zero residue)
            // and when the input stack is missing, so this line is inert wherever
            // the keyboard is the input device.
            _touchControls = TouchControlsView.Spawn(root, InputRuntime.Bridge);
            if (_touchControls != null)
            {
                ZLog.Info("Game", "GameHud: touch controls spawned (joystick + 互动/暂停).");
            }
        }

        /// <summary>
        /// Tears the HUD down: unsubscribes every event token and destroys the
        /// two captions on the persistent UIRoot canvas. Idempotent (safe on a
        /// HUD that was never built). Mirrors the GameRoot.Initialize/Shutdown
        /// seam pattern so EditMode — where MonoBehaviour callbacks are not
        /// reliably driven — exercises the exact path the engine runs at
        /// object teardown (TASK-H-06).
        /// </summary>
        public void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;

            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();
            _enemies.Clear();

            // The captions hang on the DontDestroyOnLoad UIRoot canvas while this host dies
            // with the scene: they must be destroyed here, otherwise every SCN_Game visit
            // leaves two orphan Texts behind (and their stale text stays visible).
            DestroyCaption(ref _pickupText);
            DestroyCaption(ref _enemyText);

            // Same contract for the touch overlay (it also hangs on the persistent
            // HUD canvas); Shutdown releases the stick first so a finger held during
            // a scene change cannot leave a stale move vector behind.
            if (_touchControls != null)
            {
                _touchControls.Shutdown();
                _touchControls = null;
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        /// <summary>Destroys one caption this HUD created (detached from the persistent canvas first).</summary>
        private static void DestroyCaption(ref Text caption)
        {
            if (caption == null)
            {
                return;
            }

            GameObject captionObject = caption.gameObject;
            caption = null;
            captionObject.transform.SetParent(null, false);

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(captionObject);
            }
            else
            {
                // EditMode: Destroy is deferred until the next editor tick, tests assert immediately.
                UnityEngine.Object.DestroyImmediate(captionObject);
            }
        }

        private sealed class UnsubscribeHook : IDisposable
        {
            private readonly System.Action _hook;

            public UnsubscribeHook(System.Action hook)
            {
                _hook = hook;
            }

            public void Dispose()
            {
                _hook();
            }
        }

        /// <summary>Sets the pickup counter directly (读档回填等非事件路径).</summary>
        public void SetPickupTotal(int total)
        {
            if (_pickupText != null)
            {
                _pickupText.text = $"拾取计数：{total}";
            }
        }

        private void OnItemPickedUp(ItemPickedUp evt)
        {
            SetPickupTotal(evt.TotalCount);
        }

        private void OnEnemyStateChanged(EnemyStateChanged evt)
        {
            if (_enemyText == null)
            {
                return;
            }

            _enemyText.text = evt.Current == EnemyStateId.Dead ? "守卫已被击败" : $"守卫状态：{evt.Current}";
        }

        private static Text CreateCaption(Transform parent, string name, Font font, Vector2 offset, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(420f, 44f);

            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = 30;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }
    }
}
