using System;
using Game.Domain.Interaction;
using UnityEngine;
using ZFrame.Core;

namespace Game.View.Interaction
{
    /// <summary>
    /// Simplified world-space prompt HUD (TASK-A-M4-01). The task brief calls
    /// for a "M3 框架外简版" prompt, so this deliberately uses a plain
    /// <see cref="TextMesh"/> instead of a UGUI panel: it subscribes to
    /// <see cref="InteractionTargetChanged"/> on the EventBus, shows
    /// <see cref="InteractInfo.Prompt"/> above the aimed prop and hides
    /// itself when the target is lost or the prompt is empty (consumed pickup,
    /// finished dialog).
    ///
    /// The event carries no position on purpose (Domain stays engine-free),
    /// so the label follows <see cref="InteractionScanner.CurrentProxy"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionPromptHud : MonoBehaviour
    {
        [SerializeField] private float _heightOffset = 0.9f;
        [SerializeField] private float _characterSize = 0.12f;
        [SerializeField] private Color _color = Color.white;

        private TextMesh _label;
        private IDisposable _targetToken;
        private bool _visible;

        /// <summary>Text currently displayed ("" while hidden).</summary>
        public string CurrentText => _visible && _label != null ? _label.text : string.Empty;

        /// <summary>True while a prompt is on screen.</summary>
        public bool IsVisible => _visible;

        private void Awake()
        {
            BuildLabel();
        }

        private void OnEnable()
        {
            _targetToken = EventBus.Subscribe<InteractionTargetChanged>(OnTargetChanged);
        }

        private void OnDisable()
        {
            _targetToken?.Dispose();
            _targetToken = null;
            Hide();
        }

        private void LateUpdate()
        {
            if (!_visible)
            {
                return;
            }

            InteractionScanner scanner = InteractionScanner.Instance;
            InteractableProxy proxy = scanner != null ? scanner.CurrentProxy : null;
            if (proxy == null)
            {
                Hide();
                return;
            }

            Transform anchor = proxy.transform;
            _label.transform.position = anchor.position + (Vector3.up * _heightOffset);

            Camera camera = Camera.main;
            if (camera != null)
            {
                _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - camera.transform.position);
            }
        }

        private void OnTargetChanged(InteractionTargetChanged evt)
        {
            if (!evt.HasTarget || string.IsNullOrEmpty(evt.Info.Prompt))
            {
                Hide();
                return;
            }

            Show(evt.Info.Prompt);
        }

        private void Show(string prompt)
        {
            _label.text = prompt;
            _label.gameObject.SetActive(true);
            _visible = true;
        }

        private void Hide()
        {
            if (_label == null)
            {
                return;
            }

            _label.text = string.Empty;
            _label.gameObject.SetActive(false);
            _visible = false;
        }

        private void BuildLabel()
        {
            var go = new GameObject("PromptLabel");
            go.transform.SetParent(transform, false);

            _label = go.AddComponent<TextMesh>();
            _label.anchor = TextAnchor.MiddleCenter;
            _label.alignment = TextAlignment.Center;
            _label.fontSize = 64;
            _label.characterSize = _characterSize;
            _label.color = _color;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                _label.font = font;
                var meshRenderer = go.GetComponent<MeshRenderer>();
                if (meshRenderer != null)
                {
                    meshRenderer.sharedMaterial = font.material;
                }
            }

            go.SetActive(false);
        }
    }
}
