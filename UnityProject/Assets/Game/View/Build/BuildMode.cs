using System;
using System.Collections.Generic;
using Game.View.Assembly;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.View.Build
{
    /// <summary>
    /// TASK-A-ASM-03：装配玩法化的**建造层**——在 SCN_Game 里用鼠标搭载具并存蓝图。
    ///
    /// 边界（刻意保持薄）：
    ///  - 底层全复用 <see cref="AssemblyBuilder"/> / <see cref="AssemblyPartFactory"/>
    ///    / <see cref="AssemblyGraphSync"/> / <see cref="AssemblySnapshotCodec"/> /
    ///    <see cref="AssemblyRebuilder"/>，本类只做「输入 → 调用 → HUD」；
    ///  - 与 SurvivalLoop 同一挂载纪律：`sceneLoaded` 门控 SCN_Game、宿主
    ///    DontDestroyOnLoad、**默认关闭且不建可见物**（像素用例零影响）；
    ///  - 幽灵预览是「一次切换建一次」的常驻对象，不是每帧 new；
    ///  - 幽灵把 <see cref="AssemblyPart"/> 组件禁用：它因此不在
    ///    <see cref="AssemblyPart.AllParts"/> 里——吸附搜索、右键删除、蓝图 Capture
    ///    都看不到它（存进去就是幽灵，不是零件）。
    ///
    /// 操作：B 进入/退出｜Q/E 循环 5 种零件｜左键放置（≤1.2m 自动吸附并 Connect）｜
    /// 右键删除（先 Disconnect 再销毁）｜K 存 slot1｜L 载入（清空 → Rebuild）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BuildMode : MonoBehaviour
    {
        /// <summary>挂载门控：只有这个场景会生成建造宿主。</summary>
        public const string GameSceneName = "SCN_Game";

        /// <summary>吸附半径（米）：鼠标落点距目标挂点不超过它才吸附。</summary>
        public const float SnapRadius = 1.2f;

        /// <summary>蓝图槽位名（K/L 固定用它）。</summary>
        public const string DefaultBlueprintName = "slot1";

        /// <summary>正式零件 id 前缀，序号从 1 递增（build-0001）。</summary>
        public const string PartIdPrefix = "build-";

        /// <summary>循环顺序：Cube → StructuralBeam → Thruster → Wheel → FuelTank。</summary>
        public static readonly AssemblyPartKind[] PartKinds =
        {
            AssemblyPartKind.Cube,
            AssemblyPartKind.StructuralBeam,
            AssemblyPartKind.Thruster,
            AssemblyPartKind.Wheel,
            AssemblyPartKind.FuelTank,
        };

        private const KeyCode ToggleKey = KeyCode.B;
        private const KeyCode NextKindKey = KeyCode.E;
        private const KeyCode PrevKindKey = KeyCode.Q;
        private const KeyCode SaveKey = KeyCode.K;
        private const KeyCode LoadKey = KeyCode.L;

        private const string GhostPartId = "~build-ghost";
        private static readonly Plane GroundPlane = new Plane(Vector3.up, Vector3.zero);

        private static BuildMode _instance;

        private int _kindIndex;
        private int _serial = 1;
        private bool _isBuilding;
        private AssemblyPart _ghost;
        private AssemblyPartKind _ghostKind;
        private int _ghostBuildCount;
        private Vector3 _lastDropPoint;
        private bool _hasDropPoint;
        private AssemblyPart _highlightedPart;
        private string _message = string.Empty;

        /// <summary>存活实例（未挂载/已卸载为 null）。</summary>
        public static BuildMode Instance => _instance;

        /// <summary>建造模式开关（默认关闭）。</summary>
        public bool IsBuilding => _isBuilding;

        /// <summary>当前待放置零件种类。</summary>
        public AssemblyPartKind CurrentKind => PartKinds[_kindIndex];

        /// <summary>幽灵预览对象（关闭时为 null）。</summary>
        public AssemblyPart Ghost => _ghost;

        /// <summary>幽灵创建次数（诊断/测试：验证「切换一次只重建一次」）。</summary>
        public int GhostBuildCount => _ghostBuildCount;

        /// <summary>最近一条操作反馈（HUD/测试）。</summary>
        public string Message => _message;

        /// <summary>场上零件数（不含幽灵）。</summary>
        public int PartCount => AssemblyPart.AllParts.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void HookSceneLoad()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryMount(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryMount(scene);
        }

        private static void TryMount(Scene scene)
        {
            if (!scene.IsValid() || scene.name != GameSceneName)
            {
                return;
            }

            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("~BuildMode");
            DontDestroyOnLoad(host);
            host.AddComponent<BuildMode>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey))
            {
                SetBuilding(!_isBuilding);
            }

            if (!_isBuilding || _ghost == null)
            {
                return;
            }

            if (Input.GetKeyDown(NextKindKey))
            {
                CycleKind(1);
            }
            else if (Input.GetKeyDown(PrevKindKey))
            {
                CycleKind(-1);
            }

            UpdateGhostPreview();

            if (Input.GetMouseButtonDown(0))
            {
                PlaceAtCursor();
            }
            else if (Input.GetMouseButtonDown(1))
            {
                DeleteAtCursor();
            }

            if (Input.GetKeyDown(SaveKey))
            {
                SaveBlueprint();
            }
            else if (Input.GetKeyDown(LoadKey))
            {
                LoadBlueprint();
            }
        }

        /// <summary>开关建造模式（幂等：状态不变时什么也不做，更不重建幽灵）。</summary>
        public void SetBuilding(bool enabled)
        {
            if (_isBuilding == enabled)
            {
                return;
            }

            _isBuilding = enabled;
            if (enabled)
            {
                EnsureGhost();
                _message = $"建造模式：{CurrentKind}（左键放置 / 右键删除 / Q E 换件 / K 存 / L 载）";
            }
            else
            {
                SetHighlightedTarget(null);
                DestroyGhost();
                _message = string.Empty;
            }
        }

        /// <summary>Q(-1)/E(+1) 循环零件种类；幽灵只在种类真的变化时重建一次。</summary>
        public void CycleKind(int delta)
        {
            if (delta == 0)
            {
                return;
            }

            int count = PartKinds.Length;
            _kindIndex = ((_kindIndex + delta) % count + count) % count;

            if (!_isBuilding)
            {
                return;
            }

            EnsureGhost();
            UpdateGhostAt(_hasDropPoint ? _lastDropPoint : Vector3.zero);
            _message = $"当前零件：{KindLabel(CurrentKind)}";
        }

        /// <summary>
        /// 在指定落点放一个零件：先按落点找 ≤<see cref="SnapRadius"/> 的空闲挂点，
        /// 命中则 <see cref="AssemblyBuilder.Connect"/>（自动对齐），否则自由放置。
        /// 返回新零件（未进入建造模式时为 null）。测试/调试可直接调用。
        /// </summary>
        public AssemblyPart PlaceAt(Vector3 dropPoint)
        {
            if (!_isBuilding)
            {
                _message = "先按 B 进入建造模式";
                return null;
            }

            string partId = NextPartId();
            AssemblyPart part = CreatePartOfKind(CurrentKind, dropPoint, partId);
            if (part == null)
            {
                _message = $"零件创建失败：{CurrentKind}";
                return null;
            }

            if (TryResolveSnap(part, dropPoint, out AttachmentPoint ownPoint,
                    out AssemblyBuilder.SnapCandidate candidate))
            {
                AssemblyJoint joint = AssemblyBuilder.Connect(part, ownPoint, candidate.Point);
                _message = joint != null
                    ? $"放置 {partId} → 吸附到 {candidate.Part.PartId}"
                    : $"放置 {partId}（吸附被拒绝，自由放置）";
            }
            else
            {
                _message = $"放置 {partId}";
            }

            Physics.SyncTransforms();
            _lastDropPoint = part.transform.position;
            _hasDropPoint = true;
            return part;
        }

        /// <summary>
        /// 删除一个零件：先 <see cref="AssemblyBuilder.Disconnect"/> 掉它身上的
        /// 所有关节（另一侧的松手在当帧完成），再失活 + 销毁。
        /// 失活是为了让 <see cref="AssemblyPart.AllParts"/> 立刻反映删除结果
        /// （Destroy 是帧末延迟，否则同一帧内还能被 Capture/吸附看到）。
        /// </summary>
        public bool DeletePart(AssemblyPart part)
        {
            if (part == null || part == _ghost)
            {
                return false;
            }

            var joints = new List<AssemblyJoint>(part.Joints);
            for (int i = 0; i < joints.Count; i++)
            {
                AssemblyBuilder.Disconnect(joints[i]);
            }

            if (_highlightedPart == part)
            {
                _highlightedPart = null;
            }

            part.gameObject.SetActive(false);
            Destroy(part.gameObject);
            return true;
        }

        /// <summary>清空场上所有零件（含关节）；幽灵不受影响。</summary>
        public void ClearAllParts()
        {
            var parts = new List<AssemblyPart>(AssemblyPart.AllParts);
            for (int i = 0; i < parts.Count; i++)
            {
                DeletePart(parts[i]);
            }

            _highlightedPart = null;
            _serial = 1;
            Physics.SyncTransforms();
        }

        /// <summary>
        /// K：Capture → ToSnapshot → Save（固定槽位 <see cref="DefaultBlueprintName"/>）。
        /// 序列化链路完全复用既有装配存档，不另造格式。
        /// </summary>
        public bool SaveBlueprint()
        {
            try
            {
                var graph = new AssemblyGraph();
                AssemblyGraphSync.Capture(graph);
                AssemblySnapshot snapshot = AssemblySnapshotCodec.ToSnapshot(graph, DefaultBlueprintName);
                AssemblySnapshotCodec.Save(snapshot);
                _message = $"已保存 {snapshot.Parts.Count} 件 / {snapshot.Joints.Count} 关节 → {DefaultBlueprintName}";
                return true;
            }
            catch (Exception e)
            {
                _message = "保存失败：" + e.Message;
                Debug.LogWarning("[BuildMode] 保存蓝图失败：" + e);
                return false;
            }
        }

        /// <summary>
        /// L：读槽位 → 清空场上零件 → <see cref="AssemblyRebuilder.Rebuild"/>。
        /// 先读后清：文件缺失时不会把场景清空。
        /// </summary>
        public bool LoadBlueprint()
        {
            AssemblySnapshot snapshot;
            try
            {
                snapshot = AssemblySnapshotCodec.Load(DefaultBlueprintName);
            }
            catch (Exception e)
            {
                _message = "载入失败：" + e.Message;
                Debug.LogWarning("[BuildMode] 读取蓝图失败：" + e);
                return false;
            }

            ClearAllParts();
            try
            {
                List<AssemblyPart> parts = AssemblyRebuilder.Rebuild(snapshot);
                SyncSerialFromScene();
                _message = $"已载入 {parts.Count} 件 → {DefaultBlueprintName}";
                return true;
            }
            catch (Exception e)
            {
                _message = "载入失败：" + e.Message;
                Debug.LogWarning("[BuildMode] 重建蓝图失败：" + e);
                return false;
            }
        }

        /// <summary>
        /// 把幽灵摆到落点（命中吸附则按 Align 的精确姿态预览）。
        /// 公开以便测试在没有鼠标的批处理环境里驱动预览。
        /// </summary>
        public void UpdateGhostAt(Vector3 dropPoint)
        {
            if (_ghost == null)
            {
                return;
            }

            _lastDropPoint = dropPoint;
            _hasDropPoint = true;

            // 先回到「干净落点」再算吸附：预览与真正放置（新件在落点、单位旋转）
            // 必须用同一套几何，否则预览吸上了、放下却对不齐。
            _ghost.transform.SetPositionAndRotation(dropPoint, Quaternion.identity);

            if (TryResolveSnap(_ghost, dropPoint, out AttachmentPoint ownPoint,
                    out AssemblyBuilder.SnapCandidate candidate))
            {
                SetHighlightedTarget(candidate.Part);
                AssemblyBuilder.Align(_ghost, ownPoint, candidate.Point);
            }
            else
            {
                SetHighlightedTarget(null);
            }
        }

        private void UpdateGhostPreview()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            if (TryGetGroundPoint(ray, out Vector3 dropPoint))
            {
                UpdateGhostAt(dropPoint);
            }
        }

        private bool PlaceAtCursor()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return false;
            }

            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            if (!TryGetGroundPoint(ray, out Vector3 dropPoint))
            {
                return false;
            }

            return PlaceAt(dropPoint) != null;
        }

        private bool DeleteAtCursor()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return false;
            }

            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
            {
                _message = "右键：射线没有命中任何东西";
                return false;
            }

            AssemblyPart part = hit.collider != null ? hit.collider.GetComponentInParent<AssemblyPart>() : null;
            if (part == null)
            {
                _message = "右键：命中的不是零件";
                return false;
            }

            string partId = part.PartId;
            if (!DeletePart(part))
            {
                return false;
            }

            _message = $"已删除 {partId}";
            return true;
        }

        /// <summary>鼠标射线 ∩ y=0 平面。</summary>
        private static bool TryGetGroundPoint(Ray ray, out Vector3 point)
        {
            point = Vector3.zero;
            if (!GroundPlane.Raycast(ray, out float distance) || distance < 0f)
            {
                return false;
            }

            point = ray.GetPoint(distance);
            return true;
        }

        /// <summary>
        /// 吸附判定：落点 ≤<see cref="SnapRadius"/> 内最近的**空闲且类型兼容**挂点
        /// 作为目标；再把 <paramref name="part"/> 自己离目标最近的空闲点作为对接侧。
        /// 走 <see cref="AssemblyBuilder.TryFindSnap"/>，不自己扫场景。
        /// </summary>
        private static bool TryResolveSnap(AssemblyPart part, Vector3 dropPoint,
            out AttachmentPoint ownPoint, out AssemblyBuilder.SnapCandidate candidate)
        {
            ownPoint = null;
            candidate = default;

            var filter = new AssemblyBuilder.AssemblyPartTypeFilter(part, null);
            if (!AssemblyBuilder.TryFindSnap(dropPoint, filter, SnapRadius, out candidate)
                || candidate.Point == null)
            {
                return false;
            }

            AttachmentPoint best = null;
            float bestSqr = float.MaxValue;
            IReadOnlyList<AttachmentPoint> points = part.Points;
            for (int i = 0; i < points.Count; i++)
            {
                AttachmentPoint point = points[i];
                if (point == null || point.IsOccupied
                    || !AttachmentPoint.AreCompatible(point.Type, candidate.Point.Type))
                {
                    continue;
                }

                float sqr = (point.WorldPosition - candidate.Point.WorldPosition).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = point;
                }
            }

            ownPoint = best;
            return best != null;
        }

        // ---------------------------------------------------------------- 幽灵 ----

        private void EnsureGhost()
        {
            if (_ghost != null && _ghostKind == CurrentKind)
            {
                return;
            }

            DestroyGhost();

            Vector3 anchor = _hasDropPoint ? _lastDropPoint : Vector3.zero;
            AssemblyPart ghost = CreatePartOfKind(CurrentKind, anchor, GhostPartId);
            if (ghost == null)
            {
                return;
            }

            ghost.gameObject.name = "~BuildGhost";
            Rigidbody body = ghost.Body;
            body.isKinematic = true;
            body.useGravity = false;
            body.detectCollisions = false;
            body.interpolation = RigidbodyInterpolation.None;

            Collider[] colliders = ghost.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }

            ApplyGhostMaterial(ghost.gameObject);

            // 关键：禁用 AssemblyPart 组件 = 从 AllParts 注销。
            // 幽灵因此不会被吸附搜索命中、不会被右键删除、不会被蓝图 Capture 抓走。
            ghost.enabled = false;

            _ghost = ghost;
            _ghostKind = CurrentKind;
            _ghostBuildCount++;
            Physics.SyncTransforms();
        }

        private void DestroyGhost()
        {
            if (_ghost != null)
            {
                Destroy(_ghost.gameObject);
            }

            _ghost = null;
        }

        private static void ApplyGhostMaterial(GameObject ghost)
        {
            var renderer = ghost.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                return;
            }

            var material = new Material(shader) { name = "~BuildGhostMat" };
            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f); // 0 = Opaque, 1 = Transparent (URP)
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }

            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = (int)RenderQueue.Transparent;

            var tint = new Color(0.35f, 0.85f, 1f, 0.35f);
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", tint);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", tint);
            }

            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private void SetHighlightedTarget(AssemblyPart part)
        {
            if (_highlightedPart == part)
            {
                return;
            }

            if (_highlightedPart != null)
            {
                _highlightedPart.SetHighlighted(false);
            }

            _highlightedPart = part;
            if (_highlightedPart != null)
            {
                _highlightedPart.SetHighlighted(true);
            }
        }

        // ---------------------------------------------------------------- 工厂 ----

        private AssemblyPart CreatePartOfKind(AssemblyPartKind kind, Vector3 position, string partId)
        {
            switch (kind)
            {
                case AssemblyPartKind.Cube:
                    return AssemblyPartFactory.CreateCube(partId, position);
                case AssemblyPartKind.StructuralBeam:
                    return AssemblyPartFactory.CreateStructuralBeam(partId, position);
                case AssemblyPartKind.Thruster:
                    return AssemblyPartFactory.CreateThruster(partId, position);
                case AssemblyPartKind.Wheel:
                    return AssemblyPartFactory.CreateWheel(partId, position);
                case AssemblyPartKind.FuelTank:
                    return AssemblyPartFactory.CreateFuelTank(partId, position);
                default:
                    Debug.LogWarning($"[BuildMode] 不支持的零件种类：{kind}");
                    return null;
            }
        }

        private string NextPartId()
        {
            string id = PartIdPrefix + _serial.ToString("0000");
            _serial++;
            return id;
        }

        /// <summary>读档后把递增序号推到场景里已有 build-NNNN 之上，避免 id 撞车。</summary>
        private void SyncSerialFromScene()
        {
            int max = 0;
            IReadOnlyList<AssemblyPart> parts = AssemblyPart.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                AssemblyPart part = parts[i];
                if (part == null || string.IsNullOrEmpty(part.PartId)
                    || !part.PartId.StartsWith(PartIdPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (int.TryParse(part.PartId.Substring(PartIdPrefix.Length), out int number) && number > max)
                {
                    max = number;
                }
            }

            _serial = max + 1;
        }

        private void OnDestroy()
        {
            SetHighlightedTarget(null);
            DestroyGhost();

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnGUI()
        {
            if (!_isBuilding)
            {
                // 待机不显示任何提示：与 A-LOOP-01 同一纪律，像素用例零影响。
                return;
            }

            var style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            style.normal.textColor = Color.white;

            GUI.Label(new Rect(20f, 20f, 760f, 28f),
                $"建造模式　零件：{KindLabel(CurrentKind)}　（Q/E 切换）", style);
            GUI.Label(new Rect(20f, 50f, 900f, 26f),
                "左键放置（≤1.2m 吸附）｜右键删除｜K 存 slot1｜L 载入｜B 退出", style);

            if (!string.IsNullOrEmpty(_message))
            {
                GUI.Label(new Rect(20f, 78f, 900f, 26f), _message, style);
            }
        }

        private static string KindLabel(AssemblyPartKind kind)
        {
            switch (kind)
            {
                case AssemblyPartKind.Cube:
                    return "方块 Cube";
                case AssemblyPartKind.StructuralBeam:
                    return "结构梁 Beam";
                case AssemblyPartKind.Thruster:
                    return "推进器 Thruster";
                case AssemblyPartKind.Wheel:
                    return "车轮 Wheel";
                case AssemblyPartKind.FuelTank:
                    return "燃料罐 FuelTank";
                default:
                    return kind.ToString();
            }
        }
    }
}
