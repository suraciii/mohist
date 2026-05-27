using System.Text.Json;
using Microsoft.Extensions.Configuration.Json;

namespace Mohist.Server.Config;

public static class MohistConfigurationExtensions
{
    public static IConfigurationBuilder AddMohistConfigFile(
        this IConfigurationBuilder builder,
        string? path = null,
        bool optional = true,
        bool reloadOnChange = true)
    {
        var configPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "config.jsonc");

        if (!File.Exists(configPath))
            return builder;

        // 读取并去除 JSONC 注释，然后作为标准 JSON 加载
        var json = File.ReadAllText(configPath);
        var cleaned = StripJsoncComments(json);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cleaned));
        return builder.AddJsonStream(stream);
    }

    /// <summary>
    /// 去除 JSONC 中的单行和多行注释。
    /// </summary>
    private static string StripJsoncComments(string json)
    {
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < json.Length)
        {
            // 多行注释 /* */
            if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '*')
            {
                i += 2;
                while (i < json.Length - 1 && !(json[i] == '*' && json[i + 1] == '/'))
                    i++;
                i += 2;
                continue;
            }

            // 单行注释 //
            if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n')
                    i++;
                continue;
            }

            // 字符串字面量 —— 原样保留
            if (json[i] == '"')
            {
                result.Append(json[i]);
                i++;
                while (i < json.Length)
                {
                    result.Append(json[i]);
                    if (json[i] == '\\' && i + 1 < json.Length)
                    {
                        i++;
                        result.Append(json[i]);
                    }
                    else if (json[i] == '"')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            result.Append(json[i]);
            i++;
        }

        return result.ToString();
    }
}
