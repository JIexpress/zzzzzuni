# 素材来源（仓库级）

> 各资产目录内另有更细的来源表（如 `UnityProject/Assets/Art/Models/SOURCES.md`）；本文件记录跨目录引用的新增素材。

| 资产 | 来源 | 许可 / 署名 | 备注 |
|---|---|---|---|
| `UnityProject/Assets/Art/Models/Humanoid/CHR_Soldier.fbx` | three.js 官方示例资产 `examples/models/gltf/Soldier.glb`（原模型为 Mixamo「Vanguard」人形，23 根骨骼，动作 Idle / Walk / Run）；本机 Blender 5.2 转 FBX（NLA 导出） | Mixamo 示例资产，经 three.js 仓库分发；署名与商用条款见源站（Mixamo / three.js examples） | TASK-A-ART-01 接入。材质沿用模型自带 `VanguardBodyMat` / `Vanguard_VisorMat`（URP/Lit），缺失槽位由 `MAT_CHR_Human` 兜底 |
