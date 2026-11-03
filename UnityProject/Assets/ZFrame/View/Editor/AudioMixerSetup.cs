#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace ZFrame.View
{
    /// <summary>
    /// One-shot asset setup for the audio system (TASK-A-M2-02): generates the
    /// committed mixer asset <c>Assets/Audio/MIX_AudioMixer.asset</c> with the
    /// four-bus layout Master → {BGM, SFX, UI} and one exposed Volume
    /// parameter per bus (MasterVolume/BGMVolume/SFXVolume/UIVolume — the
    /// names AudioService.GetExposedParamName drives via AudioMixer.SetFloat).
    /// Idempotent: re-running keeps a valid asset and only regenerates a
    /// missing/incomplete one. Run via menu (ZFrame/Setup/Generate
    /// MIX_AudioMixer) or batch:
    ///   -executeMethod ZFrame.View.AudioMixerSetup.SetupFromCommandLine
    ///
    /// Unity has no public API for authoring AudioMixer assets, so generation
    /// goes through reflection over the internal UnityEditor.Audio.*
    /// controller classes (AudioMixerController / AudioMixerGroupController /
    /// AudioMixerSnapshotController / AudioMixerEffectController) — verified
    /// against the UnityCsReference 2022.3 bindings. Editor-only by the
    /// #if UNITY_EDITOR guard (same pattern as SceneProjectSetup).
    ///
    /// Serialization order matters: the mixer asset is created FIRST and every
    /// group/effect/snapshot is added as a sub-asset BEFORE the parent/child
    /// references are wired — otherwise the references serialize as dangling
    /// fileID: 0 (Unity drops references to objects that are not part of the
    /// asset at save time).
    /// </summary>
    public static class AudioMixerSetup
    {
        private const string AudioDir = "Assets/Audio";
        private const string MixerPath = AudioDir + "/MIX_AudioMixer.asset";
        private const string SnapshotName = "Snapshot";
        private const string ViewName = "View";

        private static readonly string[] ChildGroupNames = { "BGM", "SFX", "UI" };

        [MenuItem("ZFrame/Setup/Generate MIX_AudioMixer")]
        public static void Setup()
        {
            bool created = EnsureMixer();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(created
                ? $"[AudioMixerSetup] mixer (re)generated at {MixerPath}"
                : $"[AudioMixerSetup] valid mixer already present at {MixerPath} — no change");
        }

        /// <summary>
        /// Batch entry: ensure the mixer asset, exit 0 on success. Both
        /// outcomes are success — the asset was regenerated OR a valid asset
        /// was already present (idempotency); only an exception (logged by
        /// Unity, exit 1) is a failure.
        /// </summary>
        public static void SetupFromCommandLine()
        {
            EnsureMixer();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorApplication.Exit(0);
        }

        /// <summary>Returns true when the asset was (re)generated, false when already valid.</summary>
        public static bool EnsureMixer()
        {
            System.IO.Directory.CreateDirectory(AudioDir);

            var existing = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
            string reason;
            if (existing != null)
            {
                if (Validate(existing, out reason))
                {
                    return false;
                }

                Debug.LogWarning($"[AudioMixerSetup] existing mixer invalid ({reason}) — regenerating.");
                AssetDatabase.DeleteAsset(MixerPath);
            }

            BuildMixer();

            // Persist, then validate the RELOADED on-disk artifact — that is
            // what future sessions will actually load.
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(MixerPath);
            var reloaded = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
            if (reloaded == null)
            {
                throw new InvalidOperationException("[AudioMixerSetup] mixer asset did not reload after import.");
            }

            if (!Validate(reloaded, out reason))
            {
                throw new InvalidOperationException(
                    $"[AudioMixerSetup] generated mixer failed validation: {reason}");
            }

            Debug.Log($"[AudioMixerSetup] created {MixerPath}: Master → {{BGM, SFX, UI}}, 4 exposed Volume params");
            return true;
        }

        /// <summary>
        /// Acceptance check: three child groups present (as sub-assets), all
        /// four exposed Volume params readable, and an exposed-parameter set
        /// actually round-trips through the reloaded mixer.
        /// </summary>
        public static bool Validate(AudioMixer mixer, out string reason)
        {
            foreach (string name in ChildGroupNames)
            {
                var groups = mixer.FindMatchingGroups(name);
                bool found = groups != null && Array.Exists(groups, g => g.name == name);
                if (!found)
                {
                    reason = $"missing mixer group '{name}'";
                    return false;
                }
            }

            // Groups/snapshot must be serialized INTO the asset — a mixer
            // whose group references are dangling fileID: 0 loads broken.
            string assetPath = AssetDatabase.GetAssetPath(mixer);
            if (!string.IsNullOrEmpty(assetPath))
            {
                bool hasGroupSubAsset = false;
                foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (subAsset is AudioMixerGroup)
                    {
                        hasGroupSubAsset = true;
                        break;
                    }
                }

                if (!hasGroupSubAsset)
                {
                    reason = "mixer groups missing as sub-assets";
                    return false;
                }
            }

            foreach (AudioBus bus in Enum.GetValues(typeof(AudioBus)))
            {
                string param = AudioService.GetExposedParamName(bus);
                if (!mixer.GetFloat(param, out _))
                {
                    reason = $"missing exposed parameter '{param}'";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        // ---- reflection-based construction --------------------------------

        private static void BuildMixer()
        {
            Assembly editorAssembly = typeof(Editor).Assembly;
            Type controllerType = RequireType(editorAssembly, "UnityEditor.Audio.AudioMixerController");
            Type groupType = RequireType(editorAssembly, "UnityEditor.Audio.AudioMixerGroupController");
            Type snapshotType = RequireType(editorAssembly, "UnityEditor.Audio.AudioMixerSnapshotController");
            Type effectType = RequireType(editorAssembly, "UnityEditor.Audio.AudioMixerEffectController");

            // Parameterless ctor wires up the native side.
            var mixer = (AudioMixer)Activator.CreateInstance(controllerType, true);
            mixer.name = "MIX_AudioMixer";
            AssetDatabase.CreateAsset(mixer, MixerPath);

            var master = CreateGroup(groupType, effectType, mixer, "Master");

            // Element type of the array must be the exact controller type —
            // reflection property setters reject base-typed arrays.
            Array childrenArray = Array.CreateInstance(groupType, ChildGroupNames.Length);
            var children = new AudioMixerGroup[ChildGroupNames.Length];
            for (int i = 0; i < ChildGroupNames.Length; i++)
            {
                var group = CreateGroup(groupType, effectType, mixer, ChildGroupNames[i]);
                childrenArray.SetValue(group, i);
                children[i] = group;
            }

            groupType.GetProperty("children").SetValue(master, childrenArray);
            controllerType.GetProperty("masterGroup").SetValue(mixer, master);

            // One snapshot (mixers need at least one; SetFloat targets the
            // active snapshot's exposed parameters at runtime).
            object snapshot = Activator.CreateInstance(snapshotType, new object[] { mixer });
            ((UnityEngine.Object)snapshot).name = SnapshotName;
            AssetDatabase.AddObjectToAsset((UnityEngine.Object)snapshot, mixer);
            Array snapshots = Array.CreateInstance(snapshotType, 1);
            snapshots.SetValue(snapshot, 0);
            controllerType.GetProperty("snapshots").SetValue(mixer, snapshots);
            controllerType.GetProperty("startSnapshot").SetValue(mixer, snapshot);

            SetExposedParameters(controllerType, groupType, mixer, master, children);
            SetGroupView(editorAssembly, controllerType, groupType, mixer, master, children);
        }

        private static AudioMixerGroup CreateGroup(Type groupType, Type effectType, AudioMixer mixer, string name)
        {
            var group = (AudioMixerGroup)Activator.CreateInstance(groupType, new object[] { mixer });
            group.name = name;
            groupType.GetMethod("PreallocateGUIDs").Invoke(group, null);
            AssetDatabase.AddObjectToAsset(group, mixer); // persist before anything references it

            // Every mixer group carries one Attenuation effect — the group
            // fader whose GUID the exposed Volume parameter points at.
            object effect = Activator.CreateInstance(effectType, new object[] { "Attenuation" });
            ((UnityEngine.Object)effect).hideFlags = HideFlags.HideInHierarchy;
            effectType.GetMethod("PreallocateGUIDs").Invoke(effect, null);
            AssetDatabase.AddObjectToAsset((UnityEngine.Object)effect, mixer);

            Array effects = Array.CreateInstance(effectType, 1);
            effects.SetValue(effect, 0);
            groupType.GetProperty("effects").SetValue(group, effects);

            return group;
        }

        private static void SetExposedParameters(
            Type controllerType, Type groupType, AudioMixer mixer, AudioMixerGroup master, AudioMixerGroup[] children)
        {
            Type paramType = RequireType(controllerType.Assembly, "UnityEditor.Audio.ExposedAudioParameter");
            Array parameters = Array.CreateInstance(paramType, children.Length + 1);

            AddExposedParameter(paramType, parameters, 0, groupType, master, "MasterVolume");
            for (int i = 0; i < children.Length; i++)
            {
                AddExposedParameter(
                    paramType, parameters, i + 1, groupType, children[i],
                    AudioService.GetExposedParamName((AudioBus)(i + 1)));
            }

            controllerType.GetProperty("exposedParameters").SetValue(mixer, parameters);
        }

        private static void AddExposedParameter(
            Type paramType, Array parameters, int index, Type groupType, AudioMixerGroup group, string parameterName)
        {
            // The exposed "Volume" of a group uses the group's own volume GUID
            // (GetGUIDForVolume = serialized m_Volume, allocated by
            // PreallocateGUIDs) — the same GUID the editor exposes when a user
            // ticks Volume in the mixer UI.
            object guid = groupType.GetMethod("GetGUIDForVolume").Invoke(group, null);

            object parameter = Activator.CreateInstance(paramType);
            paramType.GetField("guid").SetValue(parameter, guid);
            paramType.GetField("name").SetValue(parameter, parameterName);
            parameters.SetValue(parameter, index);
        }

        private static void SetGroupView(
            Assembly editorAssembly, Type controllerType, Type groupType,
            AudioMixer mixer, AudioMixerGroup master, AudioMixerGroup[] children)
        {
            // The mixer editor window renders through "views"; provide one
            // containing all groups so the asset opens cleanly by hand too.
            Type viewType = RequireType(editorAssembly, "UnityEditor.Audio.MixerGroupView");
            // The guids field is GUID[]; its element type is the GUID struct.
            Type guidType = viewType.GetField("guids").FieldType.GetElementType();

            var allGroups = new AudioMixerGroup[children.Length + 1];
            allGroups[0] = master;
            children.CopyTo(allGroups, 1);

            Array guids = Array.CreateInstance(guidType, allGroups.Length);
            for (int i = 0; i < allGroups.Length; i++)
            {
                guids.SetValue(groupType.GetProperty("groupID").GetValue(allGroups[i]), i);
            }

            object view = Activator.CreateInstance(viewType);
            viewType.GetField("guids").SetValue(view, guids);
            viewType.GetField("name").SetValue(view, ViewName);

            Array views = Array.CreateInstance(viewType, 1);
            views.SetValue(view, 0);
            controllerType.GetProperty("views").SetValue(mixer, views);
        }

        private static Type RequireType(Assembly assembly, string fullName)
        {
            Type type = assembly.GetType(fullName);
            if (type == null)
            {
                throw new InvalidOperationException(
                    $"[AudioMixerSetup] type '{fullName}' not found — Unity version drift?");
            }

            return type;
        }
    }
}
#endif
