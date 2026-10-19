using System.Globalization;
using SmartFan.App.Models;
using SmartFan.Core.Protocol;

namespace SmartFan.App.Services;

public static class SettingsValidator
{
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();
        errors.AddRange(DashboardLayoutEngine.Validate(settings.Dashboard));
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            errors.Add($"不支持设置版本 {settings.SchemaVersion}，当前支持 {AppSettings.CurrentSchemaVersion}。");
        var appearance = settings.Appearance;
        if (appearance is null) errors.Add("缺少外观设置。");
        else
        {
            if (!new[] { "Nebula", "Light", "Cyber", "Graphite", "System" }.Contains(appearance.ThemeName)) errors.Add("未知主题。");
            if (!new[] { "Layered", "Solid", "Wallpaper" }.Contains(appearance.BackgroundMode)) errors.Add("未知背景模式。");
            if (appearance.AccentColor is null || appearance.AccentColor.Length != 7 || appearance.AccentColor[0] != '#' ||
                !uint.TryParse(appearance.AccentColor.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                errors.Add("强调色必须为 #RRGGBB 格式。");
            if (!InRange(appearance.UiScale, 0.75, 1.5)) errors.Add("字体缩放必须在 0.75～1.5 之间。");
            if (string.IsNullOrWhiteSpace(appearance.UiFontFamily) || appearance.UiFontFamily.Length > 128) errors.Add("字体名称无效。");
            if (!InRange(appearance.WallpaperOpacity, 0, 1) || !InRange(appearance.PanelOpacity, 0, 1)) errors.Add("透明度必须在 0～1 之间。");
            if (!InRange(appearance.WallpaperCropX, 0, 1) || !InRange(appearance.WallpaperCropY, 0, 1) ||
                !InRange(appearance.WallpaperCropWidth, 0.001, 1) || !InRange(appearance.WallpaperCropHeight, 0.001, 1) ||
                appearance.WallpaperCropX + appearance.WallpaperCropWidth > 1.000001 ||
                appearance.WallpaperCropY + appearance.WallpaperCropHeight > 1.000001) errors.Add("壁纸裁剪区域必须位于图片以内。");
            if (appearance.TemperatureUnit is not ("C" or "F")) errors.Add("未知温度单位。");
            if (appearance.MemoryUnit is not ("MiB" or "GiB")) errors.Add("未知本地内存显示单位。");
        }
        if (settings.Window is null) errors.Add("缺少窗口设置。");
        else
        {
            if (!InRange(settings.Window.Width, 800, 10000) || !InRange(settings.Window.Height, 600, 10000)) errors.Add("窗口尺寸无效。");
            if (settings.Window.Left.HasValue && !double.IsFinite(settings.Window.Left.Value) ||
                settings.Window.Top.HasValue && !double.IsFinite(settings.Window.Top.Value)) errors.Add("窗口位置必须是有限数值。");
        }
        if (settings.Connection is null) errors.Add("缺少连接设置。");
        else
        {
            if (settings.Connection.ReconnectInitialSeconds is < 1 or > 60) errors.Add("首次重连间隔必须在 1～60 秒内。");
            if (settings.Connection.LogLevel is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical")) errors.Add("未知日志级别。");
            if (settings.Connection.RecentDevices is null || settings.Connection.RecentDevices.Count > 100) errors.Add("最近设备列表无效。");
            else if (settings.Connection.RecentDevices.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name)))
                errors.Add("最近设备必须有标识和名称。");
        }
        if (settings.Telemetry is null) errors.Add("缺少上报设置。");
        else
        {
            if (!new[] { 500, 1000, 2000, 5000 }.Contains(settings.Telemetry.IntervalMs)) errors.Add("上报间隔只支持 500、1000、2000、5000 ms。");
            if (!Enum.IsDefined(settings.Telemetry.RamWireUnit)) errors.Add("未知 RAM 协议单位。");
            if (settings.Telemetry.SensorPreferences is null || settings.Telemetry.EnabledItems is null) errors.Add("传感器或上报项设置无效。");
            if (settings.Telemetry.SensorPreferences?.ManualFanCount is < 0 or > 5) errors.Add("手动指定的内置风扇数量必须在 0～5 内。");
        }
        if (settings.Profiles is null || settings.Profiles.Count is < 1 or > 100) errors.Add("需要 1～100 个场景。");
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in settings.Profiles)
            {
                if (profile is null) { errors.Add("场景不能为空。"); continue; }
                if (!ids.Add(profile.Id)) errors.Add("场景标识重复。");
                foreach (var error in ValidateProfile(profile)) errors.Add($"场景「{profile.Name}」：{error}");
            }
            if (!ids.Contains(settings.ActiveProfileId)) errors.Add("当前场景不存在。");
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateProfile(DeviceProfile profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 128) errors.Add("场景标识无效。");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80) errors.Add("场景名称需要 1～80 个字符。");
        if (profile.FanMode is < 0 or > 2) errors.Add("风扇模式只能为 0、1、2。");
        if (profile.ManualPwm > 100 || profile.FallbackPwm > 100) errors.Add("PWM 必须在 0～100% 内。");
        if (profile.LightMode is < 0 or > 8) errors.Add("灯效模式必须在 0～8 内。");
        if (!Enum.IsDefined(profile.TemperatureSource)) errors.Add("未知温度来源。");
        if (profile.LastTemperatureTimeoutSeconds != 10 || profile.FallbackPwm != 20)
            errors.Add("当前约定的失联策略为 10 秒后 20% PWM，修改策略需要先对齐固件。");
        errors.AddRange(CurveService.Validate(profile.Curve));
        if (profile.LightingPayloads is null || profile.LightingPayloads.Count > 8) errors.Add("灯效配置字典无效。");
        else
        {
            foreach (var pair in profile.LightingPayloads)
            {
                if (!ProtocolCatalog.TryGet(pair.Key, out var key) || key < CharacteristicKey.StaticConfig || key > CharacteristicKey.RoundConfig || pair.Value is null)
                { errors.Add($"灯效配置 {pair.Key} 的名称或数据无效。"); continue; }
                try { PacketCodec.Validate(key, pair.Value); }
                catch (ArgumentException ex) { errors.Add($"灯效配置 {pair.Key}：{ex.Message}"); }
            }
        }
        return errors;
    }

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
