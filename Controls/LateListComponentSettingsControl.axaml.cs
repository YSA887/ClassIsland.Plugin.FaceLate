using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Plugin.FaceLate.Models;

namespace ClassIsland.Plugin.FaceLate.Controls;

/// <summary>
/// 「早读迟到名单」组件的设置控件。
/// </summary>
public partial class LateListComponentSettingsControl : ComponentBase<LateListComponentSettings>
{
    public LateListComponentSettingsControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 给 NumericUpDown 用的包装属性：<c>NumericUpDown.Value</c> 是 <c>decimal?</c>，
    /// 用这个属性中转可以避免类型转换与清空输入框时的绑定异常。
    /// </summary>
    public decimal? MaxDisplayNamesValue
    {
        get => Settings?.MaxDisplayNames ?? 8m;
        set
        {
            if (Settings != null && value.HasValue)
            {
                Settings.MaxDisplayNames = (int)value.Value;
            }
        }
    }
}
