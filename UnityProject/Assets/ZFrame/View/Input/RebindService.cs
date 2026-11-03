using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Runtime key-rebinding owner of TASK-A-M3-03: one place that knows how
    /// to capture a new key, refuse conflicting ones and persist the result.
    ///
    /// REBIND FLOW (per call):
    /// 1. Guard — no play mode (<c>!Application.isPlaying</c>) or an already
    ///    running rebind → the callback fires IMMEDIATELY with
    ///    <see cref="RebindOutcome.Failed"/> (never throws; EditMode tests rely
    ///    on this).
    /// 2. Capture — <c>InputAction.PerformInteractiveRebinding(bindingIndex)</c>
    ///    with <c>WithCancelingThrough("&lt;Keyboard&gt;/escape")</c> (Esc →
    ///    <see cref="RebindOutcome.Cancelled"/>) and
    ///    <c>WithControlsExcluding("&lt;Mouse&gt;")</c> (brief: 排除鼠标 — a
    ///    mouse button must never end up on a gameplay action).
    /// 3. Conflict check — the captured EFFECTIVE path is compared against the
    ///    whole action map through <see cref="BindingConflictDetector"/>
    ///    (composite headers excluded, the rebound binding itself excluded).
    ///    Conflict → <c>RemoveBindingOverride(bindingIndex)</c> rolls the
    ///    override back, nothing is written, callback = Conflict + the
    ///    offending snapshot.
    /// 4. Persist — no conflict → <c>SaveBindingOverridesAsJson()</c> into
    ///    <c>{root}/input_bindings.json</c> and callback = Done + the new snapshot.
    ///    When that write throws (disk full / read-only / locked) the callback
    ///    reports <see cref="RebindOutcome.Failed"/> — the callback always fires
    ///    exactly once, whatever the failure (a rebind that reported nothing
    ///    would leave the settings row stuck on 「按任意键…」).
    ///
    /// The persistence ROOT IS INJECTABLE (same discipline as the SaveSystem /
    /// SettingsModel precedents): runtime uses
    /// <see cref="Application.persistentDataPath"/>, tests pass a temp folder
    /// and never touch the player's real bindings file.
    /// </summary>
    public sealed class RebindService : IBindingSettings, IDisposable
    {
        /// <summary>File name inside the injected root directory.</summary>
        public const string FileName = "input_bindings.json";

        /// <summary>Text shown for an unbound / unresolvable path.</summary>
        public const string UnboundDisplay = "未绑定";

        private readonly ZFrameControls _controls;
        private readonly string _rootDirectory;
        private InputActionRebindingExtensions.RebindingOperation _operation;
        private Action<RebindOutcome, BindingSnapshot> _pending;
        private bool _disposed;

        /// <summary>
        /// Creates the service over <paramref name="controls"/>. When
        /// <paramref name="rootDirectory"/> is null/empty the real
        /// <see cref="Application.persistentDataPath"/> is used (runtime);
        /// tests MUST inject a temp root.
        /// </summary>
        public RebindService(ZFrameControls controls, string rootDirectory = null)
        {
            _controls = controls ?? throw new ArgumentNullException(nameof(controls));
            _rootDirectory = string.IsNullOrEmpty(rootDirectory)
                ? Application.persistentDataPath
                : rootDirectory;
        }

        /// <summary>The wrapped action asset (overrides live on this clone, never on the committed .inputactions file).</summary>
        public InputActionAsset Asset => _controls.Asset;

        /// <summary>True while an interactive rebind is waiting for a key.</summary>
        public bool IsRebinding => _operation != null;

        /// <summary>Full path of the persisted overrides file.</summary>
        public string FilePath => Path.Combine(_rootDirectory, FileName);

        /// <inheritdoc />
        public bool IsAvailable => !_disposed;

        // ---- persistence ----------------------------------------------------

        /// <summary>
        /// Applies the persisted overrides onto the asset. Tolerant by design
        /// (幂等): a missing file, an empty file or unreadable JSON is a
        /// silent no-op — boot must never fail because of bindings. Returns
        /// true only when a file was found and handed to the Input System.
        /// </summary>
        public bool Load()
        {
            if (_disposed || !File.Exists(FilePath))
            {
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(FilePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ZLog.Warn("Input", $"RebindService: cannot read '{FilePath}' ({e.GetType().Name}) — asset defaults kept.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return false; // saved-with-no-overrides marker
            }

            if (json.IndexOf("\"bindings\"", StringComparison.Ordinal) < 0)
            {
                ZLog.Warn("Input", $"RebindService: '{FilePath}' is not an overrides file — asset defaults kept.");
                return false;
            }

            try
            {
                _controls.Asset.LoadBindingOverridesFromJson(json);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException)
            {
                ZLog.Warn("Input", $"RebindService: '{FilePath}' is not a valid overrides file ({e.GetType().Name}) — asset defaults kept.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes the asset's current overrides to <c>{root}/input_bindings.json</c>
        /// (UTF-8 without BOM; an override-free asset writes an empty file, which
        /// <see cref="Load"/> treats as "no overrides").
        /// </summary>
        public void Save()
        {
            if (_disposed)
            {
                return;
            }

            string json = _controls.Asset.SaveBindingOverridesAsJson();
            Directory.CreateDirectory(_rootDirectory);
            File.WriteAllText(FilePath, json ?? string.Empty, new UTF8Encoding(false));
        }

        // ---- rebinding ------------------------------------------------------

        /// <summary>
        /// Rebinds the action's PRIMARY binding — the first non-composite
        /// binding of the action (for Move that is the WASD "up" part; for
        /// Interact/Pause the keyboard binding). Single-binding actions
        /// (Interact/Pause) keep one row per action; the four 移动 rows of
        /// TASK-A-M3-04 address the WASD parts through the explicit-index
        /// overload below.
        /// </summary>
        public void Rebind(string actionKey, Action<RebindOutcome, BindingSnapshot> onComplete)
        {
            if (!TryResolve(actionKey, out InputAction action))
            {
                FailNow(onComplete);
                return;
            }

            int bindingIndex = FindPrimaryBindingIndex(action);
            if (bindingIndex < 0)
            {
                ZLog.Warn("Input", $"RebindService: '{actionKey}' has no rebindable binding.");
                FailNow(onComplete);
                return;
            }

            Rebind(actionKey, bindingIndex, onComplete);
        }

        /// <summary>
        /// Rebinds ONE explicit binding index of the action (composite headers
        /// are rejected — they carry no device path). See the class doc for the
        /// full flow; the callback always fires exactly once.
        /// </summary>
        public void Rebind(string actionKey, int bindingIndex, Action<RebindOutcome, BindingSnapshot> onComplete)
        {
            if (!Application.isPlaying)
            {
                ZLog.Warn("Input", $"RebindService: rebinding '{actionKey}' needs play mode (EditMode is read-only).");
                FailNow(onComplete);
                return;
            }

            if (_disposed || _operation != null)
            {
                ZLog.Warn("Input", $"RebindService: a rebind is already running — '{actionKey}' ignored.");
                FailNow(onComplete);
                return;
            }

            if (!TryResolve(actionKey, out InputAction action))
            {
                FailNow(onComplete);
                return;
            }

            if (bindingIndex < 0 || bindingIndex >= action.bindings.Count || action.bindings[bindingIndex].isComposite)
            {
                ZLog.Warn("Input", $"RebindService: '{actionKey}' binding #{bindingIndex} is not rebindable.");
                FailNow(onComplete);
                return;
            }

            StartCapture(actionKey, action, bindingIndex, onComplete);
        }

        /// <summary>
        /// Creates and starts the interactive operation. A capture that cannot be
        /// started (device layer unavailable, disposed asset, …) reports
        /// <see cref="RebindOutcome.Failed"/> through <see cref="Complete"/> instead of
        /// leaving <c>_operation</c>/<c>_pending</c> behind — a leftover operation would
        /// make every later rebind fall into the "already running" early exit forever,
        /// i.e. the 按键 row of the settings panel would never work again.
        /// </summary>
        private void StartCapture(
            string actionKey,
            InputAction action,
            int bindingIndex,
            Action<RebindOutcome, BindingSnapshot> onComplete)
        {
            _pending = onComplete;
            try
            {
                _operation = action
                    .PerformInteractiveRebinding(bindingIndex)
                    .WithCancelingThrough("<Keyboard>/escape")
                    .WithControlsExcluding("<Mouse>")
                    .OnCancel(_ => Complete(RebindOutcome.Cancelled, null))
                    .OnComplete(_ => CompleteCaptured(actionKey, action, bindingIndex));
                _operation.Start();
            }
            catch (Exception e)
            {
                // Complete() is the single exit: it disposes a half-built operation, clears
                // _operation/_pending (so the service accepts the next rebind) and reports.
                ZLog.Error("Input", $"RebindService: cannot start rebinding '{actionKey}' ({e.GetType().Name}: {e.Message}) — no rebind is running.");
                Complete(RebindOutcome.Failed, null);
            }
        }

        /// <summary>Cancels the running rebind (the callback then reports <see cref="RebindOutcome.Cancelled"/>). False when idle.</summary>
        public bool Cancel()
        {
            if (_operation == null)
            {
                return false;
            }

            _operation.Cancel(); // → OnCancel → Complete(Cancelled)
            return true;
        }

        // ---- display --------------------------------------------------------

        /// <summary>
        /// Current key display of the action's primary binding, rendered by
        /// <c>InputControlPath.ToHumanReadableString</c> with
        /// <c>OmitDevice</c> — "E", not "Keyboard E". Unbound/unresolvable
        /// actions return <see cref="UnboundDisplay"/>.
        /// </summary>
        public string GetBindingDisplay(string actionKey)
        {
            return GetBindingDisplay(actionKey, -1);
        }

        /// <summary>
        /// Current key display of ONE binding (TASK-A-M3-04): -1 = the action's
        /// primary binding (A-M3-03 behaviour), otherwise the exact index — the
        /// 移动·上/左/下/右 rows read the W/A/S/D parts of the composite through
        /// this overload. Out-of-range indices fall back to
        /// <see cref="UnboundDisplay"/> (never throws: a row must still render).
        /// </summary>
        public string GetBindingDisplay(string actionKey, int bindingIndex)
        {
            if (!TryResolve(actionKey, out InputAction action))
            {
                return UnboundDisplay;
            }

            int index = bindingIndex >= 0 ? bindingIndex : FindPrimaryBindingIndex(action);
            return index < 0 || index >= action.bindings.Count
                ? UnboundDisplay
                : FormatBindingDisplay(action.bindings[index].effectivePath);
        }

        /// <summary>Pure formatting of one effective path into a key label (device name omitted).</summary>
        public static string FormatBindingDisplay(string effectivePath)
        {
            if (string.IsNullOrEmpty(effectivePath))
            {
                return UnboundDisplay;
            }

            string human = InputControlPath.ToHumanReadableString(
                effectivePath,
                InputControlPath.HumanReadableStringOptions.OmitDevice);
            return string.IsNullOrEmpty(human) ? effectivePath : human;
        }

        // ---- reset ----------------------------------------------------------

        /// <summary>Restores the asset defaults of ONE action (all of its bindings) and persists.</summary>
        public void ResetBinding(string actionKey)
        {
            if (_disposed || !TryResolve(actionKey, out InputAction action))
            {
                return;
            }

            action.RemoveAllBindingOverrides();
            SaveAfterReset($"'{actionKey}'");
        }

        /// <summary>
        /// Restores the asset default of ONE binding only (TASK-A-M3-04) and
        /// persists: the per-row 恢复默认 of 移动·上 must drop the up override
        /// WITHOUT touching 左/下/右. <paramref name="bindingIndex"/> -1 = the
        /// whole action (delegates to <see cref="ResetBinding(string)"/>).
        /// </summary>
        public void ResetBinding(string actionKey, int bindingIndex)
        {
            if (bindingIndex < 0)
            {
                ResetBinding(actionKey);
                return;
            }

            if (_disposed || !TryResolve(actionKey, out InputAction action))
            {
                return;
            }

            if (bindingIndex >= action.bindings.Count)
            {
                ZLog.Warn("Input", $"RebindService: '{actionKey}' binding #{bindingIndex} does not exist — reset ignored.");
                return;
            }

            action.RemoveBindingOverride(bindingIndex);
            SaveAfterReset($"'{actionKey}' binding #{bindingIndex}");
        }

        /// <summary>恢复默认: drops EVERY override of the asset and persists the empty state.</summary>
        public void ResetAll()
        {
            if (_disposed)
            {
                return;
            }

            _controls.Asset.RemoveAllBindingOverrides();
            SaveAfterReset("the whole asset (恢复默认)");
        }

        /// <summary>Cancels any running rebind and releases the operation's unmanaged memory.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_operation != null)
            {
                InputActionRebindingExtensions.RebindingOperation operation = _operation;
                _operation = null;
                _pending = null;
                operation.Dispose();
            }
        }

        // ---- internals ------------------------------------------------------

        /// <summary>
        /// Persists the state that a reset produced, CONTAINING a failed write
        /// (TASK-H-05 F3 — same policy as <see cref="CompleteCaptured"/>): the
        /// reset entry points are invoked from settings-row button callbacks,
        /// so an <see cref="IOException"/> escaping here would surface as an
        /// unhandled exception inside the UI event loop. The in-memory defaults
        /// stay applied (that is what the player asked for), the previous file
        /// on disk is left untouched, and the next successful <see cref="Save"/>
        /// rewrites the full override snapshot. Success path is unchanged: the
        /// exact same <see cref="Save"/> runs.
        /// </summary>
        private void SaveAfterReset(string scope)
        {
            try
            {
                Save();
            }
            catch (Exception e)
            {
                ZLog.Error("Input", $"RebindService: cannot persist reset of {scope} ({e.GetType().Name}: {e.Message}) — in-memory defaults kept, disk file untouched.");
            }
        }

        private void CompleteCaptured(string actionKey, InputAction action, int bindingIndex)
        {
            // PerformInteractiveRebinding already applied the override; read the
            // EFFECTIVE path so persisted + fresh overrides are treated alike.
            string newPath = action.bindings[bindingIndex].effectivePath;
            IReadOnlyList<BindingSnapshot> map = BindingConflictDetector.CaptureMap(action.actionMap);
            BindingSnapshot conflict = BindingConflictDetector.FindConflict(map, newPath, actionKey, bindingIndex);

            if (conflict != null)
            {
                action.RemoveBindingOverride(bindingIndex); // rollback: the old key stays valid
                ZLog.Warn("Input", $"RebindService: '{actionKey}' → '{newPath}' conflicts with '{conflict.ActionKey}' — rolled back.");
                Complete(RebindOutcome.Conflict, conflict);
                return;
            }

            var applied = new BindingSnapshot(
                actionKey,
                action.name,
                bindingIndex,
                newPath,
                isComposite: false,
                isCompositePart: action.bindings[bindingIndex].isPartOfComposite);

            try
            {
                Save();
            }
            catch (Exception e)
            {
                // Persisting failed (disk full, read-only profile directory, locked file, …).
                // The captured key stays live in memory for this session — never silently:
                // the outcome must reach the row, otherwise it stays on 「按任意键…」 with
                // interactable=false forever (the soft-lock). Reporting through Complete()
                // is the single exit; the old overrides file on disk is left untouched.
                ZLog.Error("Input", $"RebindService: cannot persist '{actionKey}' → '{newPath}' ({e.GetType().Name}: {e.Message}) — kept for this session only.");
                Complete(RebindOutcome.Failed, null);
                return;
            }

            Complete(RebindOutcome.Done, applied);
        }

        /// <summary>Single exit for a rebind that never started: the callback fires at once, nothing is disposed (no operation exists).</summary>
        private static void FailNow(Action<RebindOutcome, BindingSnapshot> callback)
        {
            callback?.Invoke(RebindOutcome.Failed, null);
        }

        /// <summary>Single exit: dispose the operation FIRST (so the callback may start a new rebind), then report.</summary>
        private void Complete(RebindOutcome outcome, BindingSnapshot snapshot)
        {
            InputActionRebindingExtensions.RebindingOperation operation = _operation;
            Action<RebindOutcome, BindingSnapshot> callback = _pending;
            _operation = null;
            _pending = null;

            operation?.Dispose();
            callback?.Invoke(outcome, snapshot);
        }

        private bool TryResolve(string actionKey, out InputAction action)
        {
            action = null;
            if (string.IsNullOrEmpty(actionKey))
            {
                return false;
            }

            int separator = actionKey.IndexOf('/');
            if (separator <= 0 || separator >= actionKey.Length - 1)
            {
                return false;
            }

            InputActionMap map = _controls.Asset.FindActionMap(actionKey.Substring(0, separator), throwIfNotFound: false);
            action = map?.FindAction(actionKey.Substring(separator + 1), throwIfNotFound: false);
            return action != null;
        }

        /// <summary>First non-composite binding that carries a path — the row's "primary" binding.</summary>
        private static int FindPrimaryBindingIndex(InputAction action)
        {
            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding binding = action.bindings[i];
                if (!binding.isComposite && !string.IsNullOrEmpty(binding.effectivePath))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Index of the binding of <paramref name="action"/> whose ASSET path is
        /// exactly <paramref name="path"/> (ordinal, e.g. <c>"&lt;Keyboard&gt;/w"</c>),
        /// or -1 when the action has no such binding (TASK-A-M3-04).
        ///
        /// This is how the settings registry turns "the up direction" into a
        /// binding index: the asset's binding ORDER is data that may change
        /// (composite parts reordered in the editor), so the UI must never
        /// hardcode 1/2/3/4 — it looks the part up by the key it ships with.
        /// The ASSET path (<c>binding.path</c>) is compared, never
        /// <c>effectivePath</c>: once the user rebinds W to T the row still has
        /// to resolve to the same part.
        /// </summary>
        public static int FindBindingIndexByPath(InputAction action, string path)
        {
            if (action == null || string.IsNullOrEmpty(path))
            {
                return -1;
            }

            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding binding = action.bindings[i];
                if (!binding.isComposite && string.Equals(binding.path, path, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
