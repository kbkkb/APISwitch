using System.Globalization;
using System.Windows;

namespace APISwitch.Services;

/// <summary>
/// 轻量多语言服务：基于 WPF ResourceDictionary 热插拔实现 zh-CN / en-US 无缝切换。
/// XAML 通过 {DynamicResource Key} 绑定；C# 代码统一通过 T(key) / F(key, args) 取词。
/// </summary>
public static class I18nService
{
    static int _localeDictIndex = -1;
    static string _requestedLanguage = "auto";

    /// <summary>语言切换后触发（UI 线程）。托盘菜单、动态文本等订阅此事件重建。</summary>
    public static event Action? LanguageChanged;

    /// <summary>用户设置的语言（auto / zh-CN / en-US）。</summary>
    public static string RequestedLanguage => _requestedLanguage;

    /// <summary>实际生效的语言（解析 auto 后的结果）。</summary>
    public static string ResolvedLanguage { get; private set; } = "zh-CN";

    /// <summary>应用启动时调用：读取设置并应用语言。</summary>
    public static void Initialize()
    {
        ApplyLanguage(AppSettingsService.Current.Language ?? "auto", save: false);
    }

    /// <summary>应用语言。language 取值：auto / zh-CN / en-US。</summary>
    public static void ApplyLanguage(string language, bool save = true)
    {
        _requestedLanguage = language;
        var resolved = Resolve(language);
        if (LoadDictionary(resolved))
        {
            ResolvedLanguage = resolved;
        }
        if (save)
        {
            AppSettingsService.Current.Language = language;
            AppSettingsService.Save();
        }
        LanguageChanged?.Invoke();
    }

    static string Resolve(string language)
    {
        if (string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        if (string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase)) return "en-US";
        try
        {
            var ui = CultureInfo.CurrentUICulture.Name;
            if (ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        }
        catch { }
        return "en-US";
    }

    static bool LoadDictionary(string lang)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Locales/{lang}.xaml");
            var sri = Application.GetResourceStream(uri);
            if (sri?.Stream == null)
            {
                App.LogStartup($"I18n dictionary resource missing: {lang}.xaml");
                return false;
            }
            using var stream = sri.Stream;
            var dict = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);

            var dicts = Application.Current.Resources.MergedDictionaries;
            if (_localeDictIndex >= 0 && _localeDictIndex < dicts.Count)
            {
                dicts[_localeDictIndex] = dict;
            }
            else
            {
                dicts.Insert(0, dict);
                _localeDictIndex = 0;
            }
            return true;
        }
        catch (Exception ex)
        {
            App.LogStartup($"I18n dictionary load FAILED ({lang}): {ex.Message}");
            return false;
        }
    }

    /// <summary>取词。缺失时回退为 key 本身（便于发现漏译）。</summary>
    public static string T(string key)
    {
        try
        {
            if (Application.Current?.Resources[key] is string s) return s;
        }
        catch { }
        return key;
    }

    /// <summary>取词并格式化。</summary>
    public static string F(string key, params object[] args) => string.Format(T(key), args);
}
