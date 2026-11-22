namespace Game.Domain.Colony
{
    /// <summary>
    /// Colony 模拟事件过桥载荷（TASK-A-COL-01）：GameWorld.Log 的一条事件
    /// 经 <see cref="ColonyEventPump"/> 转换成此 DTO 后用 ZFrame EventBus 发布。
    /// 字段语义与 <c>GameEvent</c> 一一对应；Kind/Visibility 展平为基本类型，
    /// 让订阅方（View/UI/报告层）不依赖 Sim 层枚举。
    /// </summary>
    public sealed class ColonyEventDto
    {
        public uint Day;          // 事件发生的游戏日
        public string Kind;       // EventKind 名称（如 Policy / Protest）
        public string Where;      // 发生地区
        public string Text;       // 事件文本（与 EventLog 逐字一致）
        public byte Visibility;   // EventVisibility 的 byte 值（0=Public 1=Personal 2=Covert）
    }
}
