namespace QPet.Core;

/// <summary>
/// 宠物展示状态(表情帧),客户端本地定义。
/// 常态外每个状态对应一张素材帧;限时状态由前端定时恢复,常驻状态(睡觉 / 走路)由交互唤醒。
/// </summary>
public enum PetMood
{
    /// <summary>常态(站住 / 眨眼)。</summary>
    Normal,

    /// <summary>比心(动作,约 1 秒)。</summary>
    Heart,

    /// <summary>跪下(动作,约 3 秒)。</summary>
    Kneel,

    /// <summary>被鼠标点击(动作,约 3 秒)。</summary>
    Clicked,

    /// <summary>吃面(动作,约 3 秒)。</summary>
    Eat,

    /// <summary>睡觉(常驻,点击 / 拖拽唤醒)。</summary>
    Sleep,

    /// <summary>被拎起拖拽(跟随鼠标,放下恢复)。</summary>
    Drag,

    /// <summary>走路(常驻,两帧轮流动画,不眨眼;再点走路 / 点击唤醒)。</summary>
    Walk,
}
