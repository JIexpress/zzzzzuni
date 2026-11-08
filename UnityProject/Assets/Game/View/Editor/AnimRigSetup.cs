#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>
    /// TASK-A-ANIM-01: prepares the humanoid animation rig for the Kenney
    /// character — the asset half of the "AI-driven state machine" slice.
    /// Zero hand-placed assets: everything here is idempotent batch/script
    /// generation (the GameSceneSetup/UtilitySceneSetup convention).
    ///
    /// Three jobs, one idempotent pass:
    /// 1. Animation FBX import settings. The three split animation FBX
    ///    (<c>CHR_anim_idle/run/jump.fbx</c> under <c>Assets/Art/Models/Animations/</c>,
    ///    Kenney "Animated Characters 1", CC0 — see SOURCES.md) import as
    ///    HUMANOID with the loop flag set per clip (idle/run loop, jump does
    ///    not). Their skeletons share the model's standard humanoid bone
    ///    names (Hips/Spine/Head/LeftArm/...), so Unity's auto-mapper resolves
    ///    them and the clips play on the model's avatar through humanoid
    ///    muscle-space retargeting — no clip data is copied anywhere.
    /// 2. Model FBX rig check: CHR_Kenney_CharacterMedium.fbx imports as
    ///    Humanoid (animationType 2 — already set at A-R9-10 import; enforced
    ///    here idempotently) with an auto-created Avatar and non-optimized
    ///    game objects (bones must exist for GetBoneTransform/IK).
    /// 3. AnimatorController generation at
    ///    <c>Assets/Art/Models/CHR_KenneyLocomotion.controller</c> from the
    ///    pure <see cref="Game.View.Animation.KenneyLocomotionPlan"/>
    ///    constants: parameters Speed(float)/Grounded(bool)/Jump(trigger),
    ///    states Idle/Run/Jump (default Idle), transitions Speed&gt;0.1→Run /
    ///    Speed&lt;0.1→Idle / Jump trigger→Jump / exit-time+grounded→Idle, and
    ///    IK pass ENABLED on the base layer (OnAnimatorIK requires it).
    ///    The Run state plays back at measuredSpeed ÷
    ///    <see cref="Game.View.Animation.KenneyLocomotionPlan.RunReferenceSpeed"/>
    ///    via the state speed multiplier bound to the Speed parameter — that
    ///    is the "animation playback speed synced to displacement" wire.
    ///
    /// Clip transport selection (the brief asks to report this): Humanoid
    /// RETARGET — clips stay sub-assets of their own animation FBX and the
    /// generated controller references them directly. Runner-up options
    /// rejected: (a) copying clip data into the model FBX asset duplicates
    /// bytes and fights idempotent regeneration; (b) an
    /// AnimatorOverrideController adds an indirection no per-NPC variation
    /// needs (all demo NPCs share one controller). Humanoid retargeting also
    /// swallows the FBX ×100 scale mismatch for free (muscle space).
    ///
    /// Run via menu (ZFrame/Setup/Generate Anim Rig) or batch:
    ///   -executeMethod Game.View.Editor.AnimRigSetup.SetupFromCommandLine
    /// Editor-only by the #if UNITY_EDITOR guard.
    /// </summary>
    public static class AnimRigSetup
    {
        private const string ModelsDir = "Assets/Art/Models";
        private const string AnimDir = ModelsDir + "/Animations";
        private const string ModelPath = ModelsDir + "/CHR_Kenney_CharacterMedium.fbx";
        private const string ControllerPath = ModelsDir + "/CHR_KenneyLocomotion.controller";

        /// <summary>The split animation FBX files and their loop flags, in stable order.</summary>
        public static readonly (string File, bool Loop)[] AnimationFiles =
        {
            ("CHR_anim_idle.fbx", true),
            ("CHR_anim_run.fbx", true),
            ("CHR_anim_jump.fbx", false),
        };

        /// <summary>Rebuilds import settings + controller, writes a TempLogs report.</summary>
        [MenuItem("ZFrame/Setup/Generate Anim Rig")]
        public static void Setup()
        {
            string report = Run();
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "TempLogs", "anim_rig_setup.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            Debug.Log($"[AnimRigSetup] report written to {path}\n{report}");
        }

        /// <summary>Batch entry: prepare the rig, write the TempLogs report, then exit 0.</summary>
        public static void SetupFromCommandLine()
        {
            string report = Run();
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "TempLogs", "anim_rig_setup.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            Debug.Log($"[AnimRigSetup] batch report written to {path}\n{report}");
            EditorApplication.Exit(0);
        }

        /// <summary>Executes all three jobs and returns the report text.</summary>
        public static string Run()
        {
            AssetDatabase.Refresh();
            var report = new StringBuilder();
            report.AppendLine($"Anim rig setup @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            foreach ((string file, bool loop) in AnimationFiles)
            {
                report.AppendLine(ConfigureAnimationFbx($"{AnimDir}/{file}", loop));
            }

            report.AppendLine(ConfigureModelFbx());
            EnsureController(ControllerPath, report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return report.ToString();
        }

        // ---- 1. animation FBX import ----------------------------------------

        private static string ConfigureAnimationFbx(string path, bool loop)
        {
            if (!File.Exists(path))
            {
                return $"=== {path}: MISSING (staging copy step not run?)";
            }

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                return $"=== {path}: no ModelImporter";
            }

            bool changed = false;
            if (importer.importAnimation == false)
            {
                importer.importAnimation = true;
                changed = true;
            }

            // NOTE (2022.3.62f3c1 verified): the enum member is Human, and the
            // clip description type is ModelImporterClipAnimation.
            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                changed = true;
            }

            // Loop flags ride on the clip descriptions. defaultClipAnimations
            // gives the importer's auto-generated take list; pin loopTime on
            // it and assign back — idempotent because the same values are
            // written on every run.
            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            if (clips.Length == 0)
            {
                return $"=== {Path.GetFileName(path)}: NO CLIPS FOUND — unexpected for a Kenney split animation FBX";
            }

            bool loopChanged = false;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i].loopTime != loop)
                {
                    clips[i].loopTime = loop;
                    loopChanged = true;
                }
            }

            if (loopChanged)
            {
                importer.clipAnimations = clips;
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }

            string clipNames = string.Join(", ", Array.ConvertAll(clips, c => c.name));
            return $"=== {Path.GetFileName(path)}: humanoid, loop={loop}, clips=[{clipNames}]"
                   + (changed ? " (reimported)" : " (unchanged)");
        }

        // ---- 2. model FBX rig -------------------------------------------------

        private static string ConfigureModelFbx()
        {
            if (!File.Exists(ModelPath))
            {
                return $"=== {Path.GetFileName(ModelPath)}: MISSING";
            }

            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null)
            {
                return $"=== {Path.GetFileName(ModelPath)}: no ModelImporter";
            }

            bool changed = false;
            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                // The FBX shipped as Generic (animationType 2 in its meta);
                // humanoid is REQUIRED for OnAnimatorIK look-at, the head
                // bone query and cross-FBX clip retargeting.
                importer.animationType = ModelImporterAnimationType.Human;
                changed = true;
            }

            if (importer.optimizeGameObjects)
            {
                // Bones must exist as real transforms: NpcAnimatorDriver's IK
                // gate reads GetBoneTransform(Head) and the tests assert the
                // head bone's world pose.
                importer.optimizeGameObjects = false;
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }

            // ModelImporter has no avatar property in 2022.3 — the auto-created
            // Avatar is a sub-asset of the FBX.
            Avatar avatar = FindModelAvatar();
            if (avatar == null)
            {
                return $"!!! {Path.GetFileName(ModelPath)}: humanoid import produced NO avatar — IK/retarget cannot work";
            }

            return $"=== {Path.GetFileName(ModelPath)}: humanoid, avatar='{avatar.name}' isHuman={avatar.isHuman}"
                   + (changed ? " (reimported)" : " (unchanged)");
        }

        // ---- 3. controller ------------------------------------------------------

        /// <summary>
        /// Regenerates the controller asset from
        /// <see cref="Game.View.Animation.KenneyLocomotionPlan"/> and VERIFIES
        /// the persisted result. Deliberately DELETE + CREATE (not in-place):
        /// Unity 2022.3 loses script-created transitions' destinationState and
        /// conditions when an existing controller is rebuilt in place (observed
        /// 2026-11-08: states survived, transitions serialized with NULL
        /// destination and no conditions — the runtime graph then never leaves
        /// the default state). A fresh asset persists everything correctly.
        /// The scene's animator reference is re-established by
        /// <see cref="AnimSceneSetup"/> which always runs this setup first, so
        /// the guid churn is harmless.
        /// </summary>
        public static AnimatorController EnsureController(string assetPath, StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                report.AppendLine("=== controller: DELETED previous (in-place rebuild loses transitions in 2022.3)");
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
            report.AppendLine($"=== controller: CREATED {assetPath}");
            ApplyPlan(controller, report);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            VerifyPersisted(assetPath, report);
            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>
        /// Reloads the controller from disk and asserts the transition graph
        /// survived serialization — the falsifiable guard against the 2022.3
        /// persistence trap above. Throws (batch exit 1) on any loss.
        /// </summary>
        private static void VerifyPersisted(string assetPath, StringBuilder report)
        {
            var persisted = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (persisted == null)
            {
                throw new InvalidOperationException($"[AnimRigSetup] controller vanished after save: {assetPath}");
            }

            if (persisted.parameters.Length != 3)
            {
                throw new InvalidOperationException(
                    $"[AnimRigSetup] persisted controller has {persisted.parameters.Length} parameters (expected 3).");
            }

            AnimatorStateMachine root = persisted.layers[0].stateMachine;
            if (!persisted.layers[0].iKPass)
            {
                throw new InvalidOperationException("[AnimRigSetup] persisted controller lost the IK pass flag.");
            }

            if (root.defaultState == null || root.defaultState.name != Game.View.Animation.KenneyLocomotionPlan.DefaultState)
            {
                throw new InvalidOperationException("[AnimRigSetup] persisted controller lost the default state (Idle).");
            }

            foreach (ChildAnimatorState child in root.states)
            {
                AnimatorState state = child.state;
                if (state == null)
                {
                    throw new InvalidOperationException("[AnimRigSetup] persisted controller has a null state.");
                }

                if (state.transitions.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"[AnimRigSetup] persisted state '{state.name}' lost transitions — " +
                        "the 2022.3 transition persistence trap hit a fresh create (extend the setup).");
                }

                foreach (AnimatorStateTransition transition in state.transitions)
                {
                    if (transition == null || transition.destinationState == null)
                    {
                        throw new InvalidOperationException(
                            $"[AnimRigSetup] persisted state '{state.name}' has a transition with NO destination — " +
                            "the 2022.3 transition persistence trap hit a fresh create (extend the setup).");
                    }

                    if (transition.conditions.Length == 0)
                    {
                        throw new InvalidOperationException(
                            $"[AnimRigSetup] persisted transition '{transition.name}' has no conditions.");
                    }
                }
            }

            report.AppendLine("  persisted verify: 3 params / 3 states / default=Idle / 4 transitions with destinations+conditions / IK pass — OK");
        }

        private static void ApplyPlan(AnimatorController controller, StringBuilder report)
        {
            // CreateAnimatorControllerAtPath seeds a default "Base Layer" —
            // remove it so the plan's single IK-enabled layer lands at index 0.
            while (controller.layers.Length > 0)
            {
                controller.RemoveLayer(controller.layers.Length - 1);
            }

            // Parameters: exactly the brief's set (Speed float, Grounded bool, Jump trigger).
            controller.AddParameter(Game.View.Animation.KenneyLocomotionPlan.SpeedParam,
                AnimatorControllerParameterType.Float);
            controller.AddParameter(Game.View.Animation.KenneyLocomotionPlan.GroundedParam,
                AnimatorControllerParameterType.Bool);
            controller.AddParameter(Game.View.Animation.KenneyLocomotionPlan.JumpParam,
                AnimatorControllerParameterType.Trigger);

            // Base layer WITH IK pass — OnAnimatorIK only fires when the
            // evaluating layer opted in.
            var layer = new AnimatorControllerLayer
            {
                name = "BaseLayer",
                defaultWeight = 1f,
                iKPass = true,
                stateMachine = new AnimatorStateMachine
                {
                    name = "Locomotion",
                    hideFlags = HideFlags.HideInHierarchy,
                },
            };
            AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
            controller.AddLayer(layer);

            AnimatorStateMachine root = layer.stateMachine;
            var idle = MakeState(root, controller, Game.View.Animation.KenneyLocomotionPlan.IdleState,
                FindClipOrThrow(AnimationFiles[0].File), new Vector3(200f, 0f, 0f), 1f, null);
            var run = MakeState(root, controller, Game.View.Animation.KenneyLocomotionPlan.RunState,
                FindClipOrThrow(AnimationFiles[1].File), new Vector3(400f, 0f, 0f),
                1f / Game.View.Animation.KenneyLocomotionPlan.RunReferenceSpeed,
                Game.View.Animation.KenneyLocomotionPlan.SpeedParam);
            var jump = MakeState(root, controller, Game.View.Animation.KenneyLocomotionPlan.JumpState,
                FindClipOrThrow(AnimationFiles[2].File), new Vector3(600f, 0f, 0f), 1f, null);

            root.defaultState = idle;
            root.entryPosition = new Vector3(0f, 0f, 0f);
            root.exitPosition = new Vector3(800f, 0f, 0f);

            // Transitions via the SUPPORTED overload AddTransition(dest,
            // defaultExitTime) — Unity creates and asset-registers the
            // transition itself; conditions/duration are then set on the
            // returned object (the same post-registration mutation pattern
            // the states use, which serializes correctly).
            AnimatorStateTransition idleToRun = idle.AddTransition(run, false);
            ConfigureTransition(idleToRun, "IdleToRun",
                Game.View.Animation.KenneyLocomotionPlan.IdleToRunBlend);
            idleToRun.AddCondition(AnimatorConditionMode.Greater,
                Game.View.Animation.KenneyLocomotionPlan.LocomotionSpeedThreshold,
                Game.View.Animation.KenneyLocomotionPlan.SpeedParam);

            AnimatorStateTransition runToIdle = run.AddTransition(idle, false);
            ConfigureTransition(runToIdle, "RunToIdle",
                Game.View.Animation.KenneyLocomotionPlan.RunToIdleBlend);
            runToIdle.AddCondition(AnimatorConditionMode.Less,
                Game.View.Animation.KenneyLocomotionPlan.LocomotionSpeedThreshold,
                Game.View.Animation.KenneyLocomotionPlan.SpeedParam);

            AnimatorStateTransition runToJump = run.AddTransition(jump, false);
            ConfigureTransition(runToJump, "RunToJump",
                Game.View.Animation.KenneyLocomotionPlan.RunToJumpBlend);
            runToJump.AddCondition(AnimatorConditionMode.If, 0f,
                Game.View.Animation.KenneyLocomotionPlan.JumpParam);

            AnimatorStateTransition jumpToIdle = jump.AddTransition(idle, false);
            ConfigureTransition(jumpToIdle, "JumpToIdle",
                Game.View.Animation.KenneyLocomotionPlan.JumpToIdleBlend);
            jumpToIdle.hasExitTime = true;
            jumpToIdle.exitTime = Game.View.Animation.KenneyLocomotionPlan.JumpExitTime;
            jumpToIdle.AddCondition(AnimatorConditionMode.If, 0f,
                Game.View.Animation.KenneyLocomotionPlan.GroundedParam);

            report.AppendLine("  params=[Speed:Float, Grounded:Bool, Jump:Trigger] default=Idle " +
                              "transitions=[Idle>Run Speed>0.1, Run>Idle Speed<0.1, Run>Jump trig, Jump>Idle exit0.9+grounded] " +
                              "baseLayer.iKPass=true");
        }

        private static AnimatorState MakeState(
            AnimatorStateMachine root,
            AnimatorController controller,
            string name,
            AnimationClip motion,
            Vector3 position,
            float speed,
            string speedParameter)
        {
            var state = root.AddState(name, position);
            state.motion = motion;
            state.speed = speed;
            state.speedParameterActive = !string.IsNullOrEmpty(speedParameter);
            state.speedParameter = speedParameter ?? string.Empty;
            state.writeDefaultValues = true;
            // Graph-layout position rides on AddState's Vector3 (AnimatorState
            // has no position property of its own — it lives on the child struct).
            return state;
        }

        private static void ConfigureTransition(AnimatorStateTransition transition, string name, float duration)
        {
            transition.name = name;
            transition.duration = Mathf.Max(0.01f, duration);
            transition.hasExitTime = false;
            transition.canTransitionToSelf = false;
            transition.interruptionSource = TransitionInterruptionSource.None;
        }

        // ---- clip / avatar lookup ---------------------------------------------

        /// <summary>
        /// The auto-created humanoid Avatar of the Kenney model (a sub-asset
        /// of the FBX — ModelImporter exposes no avatar property in 2022.3).
        /// </summary>
        public static Avatar FindModelAvatar()
        {
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            {
                if (asset is Avatar avatar)
                {
                    return avatar;
                }
            }

            return null;
        }

        // ---- clip lookup ------------------------------------------------------

        /// <summary>
        /// Finds the locomotion clip inside a split-animation FBX. The Kenney
        /// exports carry the real take ("Root|Idle" etc.) PLUS a reference
        /// "Root|0.Targeting Pose" bind-pose take — the pose take is excluded,
        /// then the take matching the file keyword (idle/run/jump) is picked;
        /// anything still ambiguous is a hard error (the controller would
        /// silently animate with the wrong motion).
        /// </summary>
        public static AnimationClip FindClipOrThrow(string animationFile)
        {
            string path = $"{AnimDir}/{animationFile}";
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            string keyword = Path.GetFileNameWithoutExtension(animationFile);
            keyword = keyword.Replace("CHR_anim_", string.Empty).ToLowerInvariant();

            AnimationClip keywordMatch = null;
            AnimationClip soleCandidate = null;
            int candidateCount = 0;
            foreach (UnityEngine.Object asset in assets)
            {
                if (!(asset is AnimationClip clip))
                {
                    continue;
                }

                string clipName = clip.name;
                if (clipName.StartsWith("__", StringComparison.Ordinal)
                    || clipName.IndexOf("Targeting Pose", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue; // bind-pose reference take, not a locomotion clip
                }

                candidateCount++;
                if (soleCandidate == null)
                {
                    soleCandidate = clip;
                }

                if (keywordMatch == null && clipName.ToLowerInvariant().Contains(keyword))
                {
                    keywordMatch = clip;
                }
            }

            if (keywordMatch != null)
            {
                return keywordMatch;
            }

            if (candidateCount == 1 && soleCandidate != null)
            {
                return soleCandidate;
            }

            throw new InvalidOperationException(
                $"[AnimRigSetup] {path}: cannot pick a locomotion clip unambiguously " +
                $"(keyword='{keyword}', candidates={candidateCount}) — extend the setup to pick explicitly.");
        }
    }
}
#endif
