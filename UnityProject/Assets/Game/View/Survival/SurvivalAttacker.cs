using UnityEngine;

namespace Game.View.Survival
{
    /// <summary>
    /// TASK-A-LOOP-02：敌人的血量与受击表现。
    ///
    /// 设计边界：
    ///  - 只管自己的血量与闪白，**不自己 Destroy**——销毁权在 <see cref="SurvivalLoop"/>，
    ///    这样"致死 → 记账 → 统一清理"只有一条路径，测试也能先断言状态再断言对象消失。
    ///  - 数值默认 2 点血、每击 1 点（玩家两下打死一个），与简报一致。
    ///  - 闪白走 <see cref="MaterialPropertyBlock"/>，不复制材质、不破坏材质实例复用。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SurvivalAttacker : MonoBehaviour
    {
        /// <summary>默认血量（玩家每击 1 点，两下致死）。</summary>
        public const int DefaultMaxHp = 2;

        /// <summary>受击闪白持续时间（秒）——必须短于攻击冷却，避免"一直白着"。</summary>
        public const float FlashSeconds = 0.1f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private Renderer _renderer;
        private MaterialPropertyBlock _block;
        private Color _baseColor = new Color(0.55f, 0.12f, 0.12f);
        private float _flashUntil;

        /// <summary>最大血量。</summary>
        public int MaxHp { get; private set; } = DefaultMaxHp;

        /// <summary>当前血量（不为负）。</summary>
        public int Hp { get; private set; } = DefaultMaxHp;

        /// <summary>是否已被打死（等待外层销毁）。</summary>
        public bool IsDead => Hp <= 0;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();

            // 颜色以挂载时的材质为准，闪白结束后还原回去（刷怪方已为每个敌人建了独立材质）。
            if (_renderer != null && _renderer.sharedMaterial != null
                && _renderer.sharedMaterial.HasProperty(BaseColorId))
            {
                _baseColor = _renderer.sharedMaterial.GetColor(BaseColorId);
            }
        }

        /// <summary>覆盖血量（刷怪方按数值设计传参；非正数忽略）。</summary>
        public void Configure(int maxHp)
        {
            if (maxHp <= 0)
            {
                return;
            }

            MaxHp = maxHp;
            Hp = maxHp;
        }

        /// <summary>
        /// 受到伤害。返回 true 表示**这一下打死了**它；已死或无伤害的调用不改变状态并返回 false。
        /// </summary>
        public bool TakeDamage(int amount)
        {
            if (amount <= 0 || IsDead)
            {
                return false;
            }

            Hp -= amount;
            if (Hp <= 0)
            {
                Hp = 0;
                _flashUntil = 0f;
                ApplyColor(Color.white);
                return true;
            }

            ApplyColor(Color.Lerp(_baseColor, Color.white, 0.75f));
            _flashUntil = Time.time + FlashSeconds;
            return false;
        }

        private void Update()
        {
            if (_flashUntil <= 0f || Time.time < _flashUntil)
            {
                return;
            }

            _flashUntil = 0f;
            ApplyColor(_baseColor);
        }

        private void ApplyColor(Color color)
        {
            if (_renderer == null)
            {
                return;
            }

            _block ??= new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_block);
            _block.SetColor(BaseColorId, color);
            _renderer.SetPropertyBlock(_block);
        }
    }
}
