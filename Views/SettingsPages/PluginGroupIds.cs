namespace ClassIsland.Plugin.FaceLate.Views.SettingsPages;

/// <summary>
/// 设置页分组 ID。
/// <para>
/// 所有的设置页都带 <c>[Group(PluginGroupIds.FaceLate)]</c>，
/// 配合 <c>Plugin.Initialize</c> 里的 <c>AddSettingsPageGroup</c>，
/// 它们就会被收进设置界面左侧的一个可折叠分组里（「人脸考勤」），
/// 不再平铺在顶层。加一个常量是为了避免各处硬编码字符串写错。
/// </para>
/// </summary>
internal static class PluginGroupIds
{
    /// <summary>「人脸考勤」分组。</summary>
    public const string FaceLate = "facelate";
}
