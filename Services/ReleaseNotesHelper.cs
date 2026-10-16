using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace APISwitch.Services;

/// <summary>
/// 负责 GitHub Release 说明与本地 Release Notes 的双语解析、提取与回退匹配。
/// </summary>
public static class ReleaseNotesHelper
{
    private static readonly Regex CommentZhRegex = new(
        @"(?i)<!--\s*lang:\s*(?:zh|zh-cn|chinese)\s*-->([\s\S]*?)<!--\s*/lang:\s*(?:zh|zh-cn|chinese)\s*-->",
        RegexOptions.Compiled);

    private static readonly Regex CommentEnRegex = new(
        @"(?i)<!--\s*lang:\s*(?:en|en-us|english)\s*-->([\s\S]*?)<!--\s*/lang:\s*(?:en|en-us|english)\s*-->",
        RegexOptions.Compiled);

    private static readonly Regex HeaderRegex = new(
        @"^#{1,3}\s+(.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// 解析 Release body 中的双语更新说明，缺失的一方尝试从本地 RELEASE_NOTES*.md 补齐回退。
    /// </summary>
    public static (string zh, string en) ParseBilingual(string? rawBody, string? targetVersion = null)
    {
        string zhNotes = "";
        string enNotes = "";

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            var body = rawBody.Replace("\r\n", "\n");

            // 1. 尝试匹配 HTML 注释语法 <!-- lang:zh --> ... <!-- /lang:zh -->
            var mZh = CommentZhRegex.Match(body);
            var mEn = CommentEnRegex.Match(body);
            if (mZh.Success) zhNotes = mZh.Groups[1].Value.Trim();
            if (mEn.Success) enNotes = mEn.Groups[1].Value.Trim();

            // 2. 尝试匹配 Markdown 标题分割（如 ## 简体中文 / ## English）
            if (string.IsNullOrWhiteSpace(zhNotes) || string.IsNullOrWhiteSpace(enNotes))
            {
                var (hdrZh, hdrEn) = SplitByMarkdownHeaders(body);
                if (!string.IsNullOrWhiteSpace(hdrZh)) zhNotes = hdrZh;
                if (!string.IsNullOrWhiteSpace(hdrEn)) enNotes = hdrEn;
            }

            // 3. 若均未匹配出双语分区，按是否有中文字符归入对应语言
            if (string.IsNullOrWhiteSpace(zhNotes) && string.IsNullOrWhiteSpace(enNotes))
            {
                if (Regex.IsMatch(body, @"[\u4e00-\u9fa5]"))
                {
                    zhNotes = body.Trim();
                }
                else
                {
                    enNotes = body.Trim();
                }
            }
        }

        // 4. 回退补齐：若某一语言为空，尝试从本地 RELEASE_NOTES 读取该版本的说明
        if (string.IsNullOrWhiteSpace(zhNotes))
        {
            zhNotes = GetLocalReleaseNotes(targetVersion, "zh-CN");
        }
        if (string.IsNullOrWhiteSpace(enNotes))
        {
            enNotes = GetLocalReleaseNotes(targetVersion, "en-US");
        }

        return (zhNotes.Trim(), enNotes.Trim());
    }

    private static (string zh, string en) SplitByMarkdownHeaders(string markdown)
    {
        var lines = markdown.Split('\n');
        var zhSb = new StringBuilder();
        var enSb = new StringBuilder();

        int currentMode = 0; // 0: None, 1: Zh, 2: En

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var m = HeaderRegex.Match(line);
            if (m.Success)
            {
                var title = m.Groups[1].Value.Trim();
                var lower = title.ToLowerInvariant();

                bool isZh = lower.Contains("中文") || lower.Contains("简体") || lower.Contains("更新说明") || lower.Contains("更新日志") || lower.Contains("(zh");
                bool isEn = lower.Contains("english") || lower.Contains("changelog") || lower.Contains("release notes") || lower.Contains("(en");

                if (isZh && !isEn)
                {
                    currentMode = 1;
                    continue;
                }
                else if (isEn && !isZh)
                {
                    currentMode = 2;
                    continue;
                }
                else if (currentMode != 0 && line.StartsWith("#"))
                {
                    // 遇到下一个非语言分割的一级/二级大标题且不是语言块，结束当前收集
                    if (line.StartsWith("# ") || line.StartsWith("## "))
                    {
                        currentMode = 0;
                    }
                }
            }

            if (currentMode == 1)
            {
                zhSb.AppendLine(line);
            }
            else if (currentMode == 2)
            {
                enSb.AppendLine(line);
            }
        }

        return (zhSb.ToString().Trim(), enSb.ToString().Trim());
    }

    /// <summary>
    /// 从本地 RELEASE_NOTES*.md 中读取指定版本的更新日志。
    /// </summary>
    public static string GetLocalReleaseNotes(string? targetVersion, string lang)
    {
        try
        {
            var fileName = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "RELEASE_NOTES_zh.md"
                : "RELEASE_NOTES.md";

            var searchPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", fileName),
                Path.Combine(Directory.GetCurrentDirectory(), fileName)
            };

            string? filePath = null;
            foreach (var p in searchPaths)
            {
                if (File.Exists(p))
                {
                    filePath = Path.GetFullPath(p);
                    break;
                }
            }

            if (filePath == null) return "";

            var content = File.ReadAllText(filePath, Encoding.UTF8).Replace("\r\n", "\n");
            var cleanVer = string.IsNullOrWhiteSpace(targetVersion) ? "" : targetVersion.TrimStart('v', 'V').Trim();

            var lines = content.Split('\n');
            var resultSb = new StringBuilder();
            bool capturing = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.StartsWith("## "))
                {
                    if (capturing)
                    {
                        // 结束当前版本块
                        break;
                    }

                    if (string.IsNullOrEmpty(cleanVer) || line.Contains(cleanVer, StringComparison.OrdinalIgnoreCase))
                    {
                        capturing = true;
                        continue;
                    }
                }

                if (capturing)
                {
                    if (line.Trim() == "---")
                    {
                        break;
                    }
                    resultSb.AppendLine(line);
                }
            }

            return resultSb.ToString().Trim();
        }
        catch
        {
            return "";
        }
    }
}
