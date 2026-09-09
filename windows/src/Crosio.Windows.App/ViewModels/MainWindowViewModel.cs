using System.ComponentModel;
using System.Runtime.CompilerServices;
using Crosio.Windows.Core.Activation;
using Crosio.Windows.Core.Features;

namespace Crosio.Windows.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private string _description = "截图、录屏、翻译、图片处理和课堂共享集中在一个本地工具箱中。";
    private string _status = "单实例启动、后台图片压缩和复制路径已经连接。";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Description
    {
        get => _description;
        private set => SetField(ref _description, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public void Apply(ActivationRoute route)
    {
        var feature = FeatureCatalog.Get(route.Feature);
        Description = route.Feature == FeatureId.Home
            ? "截图、录屏、翻译、图片处理和课堂共享集中在一个本地工具箱中。"
            : GetDescription(route.Feature);
        Status = route.Issue == ActivationIssue.None
            ? GetStatus(route.Feature, feature.DisplayName)
            : "无法识别这次启动请求，已安全返回首页。";
    }

    private static string GetDescription(FeatureId feature) => feature switch
    {
        FeatureId.RegionScreenshot => "拖动选择屏幕区域，完成后立即复制并进入标注。",
        FeatureId.WindowScreenshot => "选择一个窗口进行截图，完成后立即复制并进入标注。",
        FeatureId.ScreenScreenshot => "截取当前屏幕，完成后立即复制并进入标注。",
        FeatureId.DelayedScreenshot => "倒计时 5 秒后截取鼠标所在屏幕，再立即复制并进入标注。",
        FeatureId.FramedScreenshot => "倒计时 3 秒后为当前屏幕加上一爪通用设备外框。",
        FeatureId.MultiWindowScreenshot => "明确勾选多个可见窗口，合成为一张透明背景图片。",
        FeatureId.LongScreenshot => "框选滚动区域并在滚动过程中拼接长图。",
        FeatureId.ScreenRecording or FeatureId.RegionRecording or FeatureId.WindowRecording =>
            "录制屏幕画面，可选择系统声音和鼠标光标。",
        FeatureId.ColorPicker => "读取鼠标所在像素的颜色并复制 HEX。",
        FeatureId.TextTranslation => "选中文字后快捷翻译，中文与英文自动互译。",
        FeatureId.ImageCompression => "保持原图片格式和后缀，压缩后自动定位结果。",
        FeatureId.ShareFiles => "在当前局域网中分享和接收文件。",
        FeatureId.ShareText => "在当前局域网中分享文字或链接。",
        FeatureId.Settings => "管理全局快捷键、开机启动和后台运行选项。",
        _ => "使用 Windows 原生能力完成当前操作。",
    };

    private static string GetStatus(FeatureId feature, string displayName) => feature switch
    {
        FeatureId.Home => "截图、快捷键、翻译、图片压缩、复制路径和局域网共享入口已经连接。",
        FeatureId.ImageCompression => "可通过资源管理器“打开方式 → 一爪”后台压缩，完成后自动定位输出文件。",
        FeatureId.Settings => "可编辑 12 项全局快捷键与开机启动；快捷键整组注册成功后才保存。",
        FeatureId.RegionScreenshot or
        FeatureId.WindowScreenshot or
        FeatureId.ScreenScreenshot or
        FeatureId.DelayedScreenshot or
        FeatureId.FramedScreenshot or
        FeatureId.MultiWindowScreenshot =>
            "最终原图会先复制到剪贴板，再打开同一个标注、OCR 和贴图编辑器。",
        FeatureId.LongScreenshot => "滚动采集、重叠检测、最终图即时复制和编辑器入口已经连接。",
        FeatureId.ShareFiles or FeatureId.ShareText => "局域网共享服务已连接；停止共享后旧链接立即失效。",
        FeatureId.TextTranslation => "使用已安装的本机 Marian 模型翻译，不上传原文。",
        FeatureId.ColorPicker => "取色后会立即把 HEX 写入剪贴板。",
        FeatureId.ScreenRecording or FeatureId.RegionRecording or FeatureId.WindowRecording =>
            "录屏使用本机 H.264/AAC 管线；仍需在 Windows 真机验证系统音频与封装。",
        _ => $"已进入{displayName}；仍需在 Windows 真机完成交互验收。",
    };

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
